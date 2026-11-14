using System.Globalization;
using System.Text;
namespace PSCAutomation.Apps.AS110AllCheck;

internal enum TestMode { Motion, Adjustment, Production }
internal sealed record ProductModel(string Name, int SupplyPressure)
{
    public static readonly ProductModel[] All = [new("AS110-035",539), new("AS110-015",588),
        new("AS110-135B",490), new("AS110-006",588), new("AS110-116B",490), new("AS110-116B350",350)];
    public override string ToString() => $"{Name}（{SupplyPressure} kPa）";
}

// 判定した型式を、別の供給圧力で測り直して出荷データにする型式(2026-11-12、人と決めた。SPEC「AS110-116B350 の出荷データ(データ取り２)」)。
// AS110-116B350 は、AS110-116B を 490 kPa で調整・判定したあと、供給圧力を 350 kPa にして同じ台を測る。350 kPa の規格は無く、合否判定・漏れ試験はしない。
// 型式は参照波形で判定した型式(AS110-116B)に決まり、データ取りの「供給圧力」でこの表の型式に切り替える。
internal static class ShippingDataModels
{
    private static readonly Dictionary<string,string> ByJudgedModel = new(StringComparer.OrdinalIgnoreCase) { ["AS110-116B"] = "AS110-116B350" };
    // 判定した型式に出荷データの工程があれば、その型式(無ければ null)。
    public static ProductModel? For(ProductModel judged) =>
        ByJudgedModel.TryGetValue(judged.Name, out string? name) ? ProductModel.All.FirstOrDefault(m => m.Name == name) : null;
    // 出荷データの保存先。データ取り(判定)の保存先の中に作る(例: 出荷データ(350kPa))。
    public static string FolderName(ProductModel shipping) => $"出荷データ({shipping.SupplyPressure}kPa)";

    // データ取り(判定)の保存先の中にある、出荷データの保存先(いまあるものだけ。無いフォルダーは作らない)。
    public static string[] ExistingRoots(string judgedRoot) => ProductModel.All
        .Select(For).Where(m => m is not null).Select(m => Path.Combine(judgedRoot, FolderName(m!)))
        .Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToArray();

    // 判定した型式の保護を解除する S/N について、出荷データ(保存ファイル・完了の記録)を探す(2026-11-14、全体見直し・区切り 5、人の決定:
    // 判定した型式を測り直すなら、前の調整のときの出荷データは消去する。それまでは「完了」のまま残り、測り直しを忘れると古い調整のデータが出荷用に残った)。
    // 保存ファイルも完了の記録も無い S/N は返さない。
    public static List<ShippingDataFound> FindData(string judgedRoot, IEnumerable<string> serials)
    {
        var found = new List<ShippingDataFound>();
        string[] wanted = serials.ToArray();
        if (wanted.Length == 0) return found;
        foreach (string root in ExistingRoots(judgedRoot))
        {
            var store = new CompletionStore(root);
            Dictionary<string, string> records;
            using (store.Lock()) records = store.Read();
            foreach (string serial in wanted)
            {
                string[] files = SaveFolder.FindSerialFiles(root, serial).ToArray();
                bool completed = long.TryParse(serial, NumberStyles.None, CultureInfo.InvariantCulture, out long n)
                    && records.GetValueOrDefault(n.ToString(CultureInfo.InvariantCulture)) == "COMPLETE";
                if (files.Length > 0 || completed) found.Add(new(root, serial, files, completed));
            }
        }
        return found;
    }

    // 見つけた出荷データを消去する。先に全部のファイルを開けるか確かめ(ほかで開いていたら 1 つも消さずに止める)、deleteFile で消し、
    // 完了の記録があれば解除(RELEASE)を書く。deleteFile は画面側が渡す(ごみ箱へ入れる)。
    public static void Remove(IReadOnlyList<ShippingDataFound> found, Action<string> deleteFile)
    {
        foreach (string file in found.SelectMany(f => f.Files))
            FileAccessAdvice.Run("出荷データのファイル", file, () => { using var probe = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None); },
                "閉じたあと、もう一度「完了番号の保護と解除」からやり直してください（まだ何も変えていません）。");
        foreach (var group in found.GroupBy(f => f.Root, StringComparer.OrdinalIgnoreCase))
        {
            var store = new CompletionStore(group.Key);
            using var held = store.Lock();
            foreach (var item in group)
            {
                foreach (string file in item.Files) FileAccessAdvice.Run("出荷データのファイル", file, () => deleteFile(file));
                if (store.IsComplete(item.Serial)) store.Append("RELEASE", item.Serial, "-", "-");
            }
        }
    }
}

// 保護を解除する S/N の出荷データ。Root は出荷データの保存先、Files は保存ファイル(Excel・PDF・LOGGER)、Completed は出荷データが完了(保護中)か。
internal sealed record ShippingDataFound(string Root, string Serial, string[] Files, bool Completed);

// UIやCOMから独立した工程状態。測定・転送・保存を同一revisionで結び付ける。
internal sealed class ProductionWorkflow
{
    public TestMode Mode { get; set; }
    public string Serial { get; private set; } = "";
    // 最後の S/N（空は上限なし）。開始 S/N がこれを超えたら測定を始めない。
    public string LastSerial { get; private set; } = "";
    public bool SerialRangeFinished => Mode == TestMode.Production && LastSerial.Length > 0 && Serial.Length > 0 &&
        long.Parse(Serial, CultureInfo.InvariantCulture) > long.Parse(LastSerial, CultureInfo.InvariantCulture);
    public ProductModel Model { get; private set; } = ProductModel.All[0];
    // 出荷データ(データ取り２)のときは、判定に使った型式(AS110-116B350 なら AS110-116B)。ふつうのデータ取り・データ調整は null。
    public ProductModel? JudgedModel { get; private set; }
    // 判定しない出荷データの工程か(合否判定・漏れ試験をせず、保存して完了する)。
    public bool ShippingData => Mode == TestMode.Production && JudgedModel is not null;
    // Excel 雛形を決める型式。出荷データは判定した型式の雛形に転送する(116B350 専用の雛形は作らない)。
    public ProductModel TemplateModel => JudgedModel ?? Model;
    public DateTime MeasuredAt { get; private set; }
    public long Revision { get; private set; }
    public long CapturedRevision { get; private set; } = -1;
    public long TransferredRevision { get; private set; } = -1;
    public long SavedRevision { get; private set; } = -1;
    public bool CaptureFailed { get; set; }
    public bool SaveFailed { get; set; }
    public bool Measuring { get; private set; }
    public int LeakChecks { get; private set; }
    // 最後に行った漏れ試験の結果(試験を始めたら空に戻す。合否は最後の試験で決める。2026-11-12)。
    public LeakOutcome? LastLeakOutcome { get; private set; }
    public bool HasCapture => !Measuring && CapturedRevision == Revision && Revision > 0;
    public bool HasTransfer => HasCapture && TransferredRevision == Revision;
    public bool HasSaved => HasTransfer && SavedRevision == Revision && !SaveFailed;
    // 最後の漏れ試験が合格、または自動判定なし(NR-X。人が判断)なら合格にできる。出荷データは漏れ試験をしないので、保存できれば完了にできる。
    public bool CanComplete => Mode == TestMode.Production && HasSaved && (ShippingData || (LeakChecks > 0 && LastLeakOutcome is LeakOutcome.Pass or LeakOutcome.NotJudged));
    // 最後の漏れ試験が不合格・判定できなかった。設定で回避を許したときだけ、人の判断で合格にできる。
    public bool LeakNeedsOverride => Mode == TestMode.Production && !ShippingData && HasSaved && LeakChecks > 0 && LastLeakOutcome is LeakOutcome.Fail or LeakOutcome.Unmeasurable;
    // 保存するファイル名(拡張子なし)。末尾の点数「_6500」は 2026-11-12 にやめた(人の指示。AS110-006 は 13000 点で名前と合わなくなるため)。
    public string BaseName => $"{Model.Name}_SN{Serial}_{MeasuredAt:yyyyMMdd}";
    public void Configure(string serial, ProductModel model) => ConfigureFor(serial, model, null);
    // 出荷データ(データ取り２)の工程を含めて設定する。judgedModel は判定に使った型式(ふつうは null)。
    public void ConfigureFor(string serial, ProductModel model, ProductModel? judgedModel)
    {
        if (!long.TryParse(serial, NumberStyles.None, CultureInfo.InvariantCulture, out long n) || n < 0 || n == long.MaxValue)
            throw new ArgumentException("シリアル番号を数字で入力してください。");
        if (Measuring) throw new InvalidOperationException("測定中です。");
        if (Serial == serial && Model == model && JudgedModel == judgedModel) return;
        Serial = serial; Model = model; JudgedModel = judgedModel; Invalidate();
    }
    public void SetLastSerial(string lastSerial)
    {
        if (lastSerial.Length > 0 && (!long.TryParse(lastSerial, NumberStyles.None, CultureInfo.InvariantCulture, out long n) || n < 0 || n == long.MaxValue))
            throw new ArgumentException("最後のシリアル番号を数字で入力してください（空は上限なし）。");
        LastSerial = lastSerial;
    }
    public void ConfigureAdjustment(ProductModel model) => ConfigureWithoutSerial(model);
    // データ取りで測定開始S/N がまだ入っていないとき。S/N を空にして型式だけ持つ(測定開始はできない)。
    public void ConfigureWithoutSerial(ProductModel model) => ConfigureWithoutSerialFor(model, null);
    public void ConfigureWithoutSerialFor(ProductModel model, ProductModel? judgedModel)
    {
        if (Measuring) throw new InvalidOperationException("測定中です。");
        if (Serial.Length == 0 && Model == model && JudgedModel == judgedModel) return;
        Serial = ""; Model = model; JudgedModel = judgedModel; Invalidate();
    }
    public bool SerialMissing => Mode == TestMode.Production && Serial.Length == 0;
    public void Invalidate()
    {
        Revision++; CapturedRevision = TransferredRevision = SavedRevision = -1;
        CaptureFailed = SaveFailed = false; LeakChecks = 0; LastLeakOutcome = null; Measuring = false;
    }
    public void BeginMeasurement(DateTime now)
    {
        if (SaveFailed || CaptureFailed) throw new InvalidOperationException("保存失敗を再試行してから測定してください。");
        Invalidate(); MeasuredAt = now; Measuring = true;
    }
    public void CaptureError() { Measuring = false; CaptureFailed = true; }
    public void TransferFailed() { TransferredRevision = SavedRevision = -1; LeakChecks = 0; LastLeakOutcome = null; }
    public void CaptureSucceeded() { CapturedRevision = Revision; CaptureFailed = false; Measuring = false; }
    public void TransferSucceeded() { if (!HasCapture) throw new InvalidOperationException("最新の測定がありません。"); TransferredRevision = Revision; SavedRevision = -1; SaveFailed = false; LeakChecks = 0; LastLeakOutcome = null; }
    public void SaveSucceeded() { if (!HasTransfer) throw new InvalidOperationException("最新結果を転送してください。"); SavedRevision = Revision; SaveFailed = false; LeakChecks = 0; LastLeakOutcome = null; }
    // 漏れ試験を始めた。前の試験の結果では合格にさせない(この試験が正常に終わるまで「合格」を押せない)。
    public void LeakStarted() { if (ShippingData) throw new InvalidOperationException("出荷データでは漏れ試験をしません。"); if (!HasSaved) throw new InvalidOperationException("一式保存が必要です。"); LastLeakOutcome = null; }
    // 漏れ試験が正常に終わった(停止まで確認できた)。結果を覚える。
    public void LeakFinished(LeakOutcome outcome) { if (!HasSaved) throw new InvalidOperationException("一式保存が必要です。"); LeakChecks++; LastLeakOutcome = outcome; }
    // 自動判定なしで正常に終わった(従来の呼び方)。
    public void LeakSucceeded() => LeakFinished(LeakOutcome.NotJudged);
    public void Advance() { Serial = (long.Parse(Serial, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture).PadLeft(Serial.Length,'0'); Invalidate(); }
}

// 追記ジャーナル。排他ロックを保持する間に保護確認とファイル更新を行う。
// 不正行・途中書込は無視せず停止し、完了済みSNの誤解除を防ぐ。
internal sealed class CompletionStore
{
    private readonly string _root;
    // 開けない・書けないときは、何を確かめるかを日本語で出す(FileAccessAdvice。2026-11-14、全体見直し・区切り 5。それまでは .NET の英語の文がそのまま出た)。
    public CompletionStore(string root)
    {
        _root = Path.GetFullPath(root);
        FileAccessAdvice.Run("保存先", _root, () => Directory.CreateDirectory(_root),
            openHint: "保存先のフォルダーが消えていないか・名前が変わっていないか確かめてください");
    }
    public IDisposable Lock()
    {
        string path = Path.Combine(_root,"完了記録.lock");
        return FileAccessAdvice.Run("完了記録", path, () => new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None),
            openHint: "AS110AllCheck をもう 1 つ起動して同じ保存先を使っていたら、片方を閉じてください");
    }
    private string Journal => Path.Combine(_root,"完了記録.txt");
    private const string JournalOpenHint = "「完了記録.txt」を Excel やメモ帳で開いていたら閉じてください";
    private static string Key(string serial) => long.Parse(serial,CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
    public Dictionary<string,string> Read()
    {
        var result = new Dictionary<string,string>();
        if (!File.Exists(Journal)) return result;
        foreach (string line in FileAccessAdvice.Run("完了記録", Journal, () => File.ReadAllLines(Journal, Encoding.UTF8), openHint: JournalOpenHint))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] p = line.Split('\t');
            if (p.Length != 5 || p[0] is not ("COMPLETE" or "RELEASE") || !long.TryParse(p[2],out _))
                throw new InvalidDataException("完了記録.txtに不正な行があります。記録を確認してください。");
            result[Key(p[2])] = p[0];
        }
        return result;
    }
    public bool IsComplete(string serial) => Read().GetValueOrDefault(Key(serial)) == "COMPLETE";
    public bool WasReleased(string serial) => Read().GetValueOrDefault(Key(serial)) == "RELEASE";
    // 記録のある S/N を番号順に、今保護されているか(COMPLETE か)と一緒に返す。保護解除の一覧に使う。
    public (string Serial, bool Protected)[] Entries() => Read()
        .OrderBy(p => long.Parse(p.Key, CultureInfo.InvariantCulture))
        .Select(p => (p.Key, p.Value == "COMPLETE")).ToArray();
    // 一覧で決めた保護の状態を反映する。保護中→解除は RELEASE、解除済み→再保護は COMPLETE(型式・ファイル名は「-」「再保護」)を追記する。
    // 今の記録と同じ状態の S/N は何もしない(画面を開いている間にほかで変わっていても二重に書かない)。呼ぶ側が Lock() を持つ。
    public (string[] Released, string[] Protected) SetProtection(IEnumerable<(string Serial, bool Protected)> desired)
        => SetProtection(desired.Select(d => (d.Serial, d.Protected, "-", "再保護")));
    // 記録の無いデータを保護するときは、ファイル名から分かる型式とファイル名を記録に残す。
    public (string[] Released, string[] Protected) SetProtection(IEnumerable<(string Serial, bool Protected, string Model, string BaseName)> desired)
    {
        var current = Read();
        var released = new List<string>(); var reprotected = new List<string>();
        foreach (var (serial, protect, model, baseName) in desired)
        {
            string key = Key(serial);
            bool isProtected = current.GetValueOrDefault(key) == "COMPLETE";
            if (protect == isProtected) continue;
            if (protect) { Append("COMPLETE", key, model, baseName); reprotected.Add(key); }
            else { Append("RELEASE", key, "-", "-"); released.Add(key); }
        }
        return (released.ToArray(), reprotected.ToArray());
    }
    public void Append(string action,string serial,string model,string baseName)
    {
        if (action is not ("COMPLETE" or "RELEASE")) throw new ArgumentException(nameof(action));
        byte[] bytes = new UTF8Encoding(false).GetBytes($"{action}\t{DateTime.Now:O}\t{Key(serial)}\t{model}\t{baseName}\n");
        FileAccessAdvice.Run("完了記録", Journal, () =>
        {
            using var file = new FileStream(Journal,FileMode.Append,FileAccess.Write,FileShare.Read);
            file.Write(bytes); file.Flush(true);
        }, openHint: JournalOpenHint);
    }
}
