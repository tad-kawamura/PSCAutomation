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
    // 最後の漏れ試験が合格、または自動判定なし(NR-X。人が判断)なら合格にできる。
    public bool CanComplete => Mode == TestMode.Production && HasSaved && LeakChecks > 0 && LastLeakOutcome is LeakOutcome.Pass or LeakOutcome.NotJudged;
    // 最後の漏れ試験が不合格・判定できなかった。設定で回避を許したときだけ、人の判断で合格にできる。
    public bool LeakNeedsOverride => Mode == TestMode.Production && HasSaved && LeakChecks > 0 && LastLeakOutcome is LeakOutcome.Fail or LeakOutcome.Unmeasurable;
    public string BaseName => $"{Model.Name}_SN{Serial}_{MeasuredAt:yyyyMMdd}_6500";
    public void Configure(string serial, ProductModel model)
    {
        if (!long.TryParse(serial, NumberStyles.None, CultureInfo.InvariantCulture, out long n) || n < 0 || n == long.MaxValue)
            throw new ArgumentException("シリアル番号を数字で入力してください。");
        if (Measuring) throw new InvalidOperationException("測定中です。");
        if (Serial == serial && Model == model) return;
        Serial = serial; Model = model; Invalidate();
    }
    public void SetLastSerial(string lastSerial)
    {
        if (lastSerial.Length > 0 && (!long.TryParse(lastSerial, NumberStyles.None, CultureInfo.InvariantCulture, out long n) || n < 0 || n == long.MaxValue))
            throw new ArgumentException("最後のシリアル番号を数字で入力してください（空は上限なし）。");
        LastSerial = lastSerial;
    }
    public void ConfigureAdjustment(ProductModel model) => ConfigureWithoutSerial(model);
    // データ取りで測定開始S/N がまだ入っていないとき。S/N を空にして型式だけ持つ(測定開始はできない)。
    public void ConfigureWithoutSerial(ProductModel model)
    {
        if (Measuring) throw new InvalidOperationException("測定中です。");
        if (Serial.Length == 0 && Model == model) return;
        Serial = ""; Model = model; Invalidate();
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
    public void LeakStarted() { if (!HasSaved) throw new InvalidOperationException("一式保存が必要です。"); LastLeakOutcome = null; }
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
    public CompletionStore(string root) { _root = Path.GetFullPath(root); Directory.CreateDirectory(_root); }
    public IDisposable Lock() => new FileStream(Path.Combine(_root,"完了記録.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    private string Journal => Path.Combine(_root,"完了記録.txt");
    private static string Key(string serial) => long.Parse(serial,CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
    public Dictionary<string,string> Read()
    {
        var result = new Dictionary<string,string>();
        if (!File.Exists(Journal)) return result;
        foreach (string line in File.ReadAllLines(Journal, Encoding.UTF8))
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
        using var file = new FileStream(Journal,FileMode.Append,FileAccess.Write,FileShare.Read);
        byte[] bytes = new UTF8Encoding(false).GetBytes($"{action}\t{DateTime.Now:O}\t{Key(serial)}\t{model}\t{baseName}\n");
        file.Write(bytes); file.Flush(true);
    }
}
