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
    public bool HasCapture => !Measuring && CapturedRevision == Revision && Revision > 0;
    public bool HasTransfer => HasCapture && TransferredRevision == Revision;
    public bool HasSaved => HasTransfer && SavedRevision == Revision && !SaveFailed;
    public bool CanComplete => Mode == TestMode.Production && HasSaved && LeakChecks > 0;
    public string BaseName => $"{Model.Name}_SN{Serial}_{MeasuredAt:yyyyMMdd}_6500";
    public void Configure(string serial, ProductModel model)
    {
        if (!long.TryParse(serial, NumberStyles.None, CultureInfo.InvariantCulture, out long n) || n < 0 || n == long.MaxValue)
            throw new ArgumentException("シリアル番号を数字で入力してください。");
        if (Measuring) throw new InvalidOperationException("測定中です。");
        if (Serial == serial && Model == model) return;
        Serial = serial; Model = model; Invalidate();
    }
    public void ConfigureAdjustment(ProductModel model)
    {
        if (Measuring) throw new InvalidOperationException("測定中です。");
        if (Serial.Length == 0 && Model == model) return;
        Serial = ""; Model = model; Invalidate();
    }
    public void Invalidate()
    {
        Revision++; CapturedRevision = TransferredRevision = SavedRevision = -1;
        CaptureFailed = SaveFailed = false; LeakChecks = 0; Measuring = false;
    }
    public void BeginMeasurement(DateTime now)
    {
        if (SaveFailed || CaptureFailed) throw new InvalidOperationException("保存失敗を再試行してから測定してください。");
        Invalidate(); MeasuredAt = now; Measuring = true;
    }
    public void CaptureError() { Measuring = false; CaptureFailed = true; }
    public void TransferFailed() { TransferredRevision = SavedRevision = -1; LeakChecks = 0; }
    public void CaptureSucceeded() { CapturedRevision = Revision; CaptureFailed = false; Measuring = false; }
    public void TransferSucceeded() { if (!HasCapture) throw new InvalidOperationException("最新の測定がありません。"); TransferredRevision = Revision; SavedRevision = -1; SaveFailed = false; LeakChecks = 0; }
    public void SaveSucceeded() { if (!HasTransfer) throw new InvalidOperationException("最新結果を転送してください。"); SavedRevision = Revision; SaveFailed = false; LeakChecks = 0; }
    public void LeakSucceeded() { if (!HasSaved) throw new InvalidOperationException("一式保存が必要です。"); LeakChecks++; }
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
    public void Append(string action,string serial,string model,string baseName)
    {
        if (action is not ("COMPLETE" or "RELEASE")) throw new ArgumentException(nameof(action));
        using var file = new FileStream(Journal,FileMode.Append,FileAccess.Write,FileShare.Read);
        byte[] bytes = new UTF8Encoding(false).GetBytes($"{action}\t{DateTime.Now:O}\t{Key(serial)}\t{model}\t{baseName}\n");
        file.Write(bytes); file.Flush(true);
    }
}
