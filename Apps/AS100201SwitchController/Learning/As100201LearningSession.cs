namespace PSCAutomation.Apps.AS100201SwitchController;

// 学習モードの記録の中身(SPEC_調整操作の記録.md「記録の考え方」「出力」「異常時」)。画面・機器に依存しない。
// 1 個体は「測定 → 操作 → … → 測定 → 完了」の列。台帳へ 1 行 1 出来事で追記し、測定のたびに xdt を保存する。
//   - 測定の行は LOGGER が止まった時点で書き(sw4 / 操作ボタン)、xdt は LOGGER の状態が停止に戻った時点で保存する
//     (停止直後はデータの読み出し中のことがあるため)。LOGGER が自動で止まったときは、停止に戻った時点で行と xdt を一緒に書く
//   - 完了のあとに操作が来たら「完了の取消」(同じ個体の続き)。測定の行がないのに操作が来たら「直前の測定なし」
//   - 取消は直前の操作 1 件だけ。操作のあとに測定・完了があれば取り消せない
//   - 台帳や xdt に書けなかったら止まる(Blocked)。測定の行と xdt は覚えておいて「再試行」で書き直す。
//     操作ボタンには NAK を返し、記録しない(再試行のあとに押し直してもらう)
internal sealed class As100201LearningSession
{
    private readonly IAs100201LearningLedger _ledger;
    private readonly Func<string, long> _saveXdt;
    private readonly Func<DateTime> _clock;
    private readonly Func<string, bool> _fileExists;
    private readonly Action<string> _createDirectory;
    private readonly List<PendingWrite> _pending = [];
    private string? _armedXdtPath;
    private UndoTarget? _undoable;
    private bool _lastEventIsMeasurement;

    private sealed record PendingWrite(string Description, Action Write);
    private sealed record UndoTarget(As100201LedgerRow Row, bool MeasurementBefore);

    public const string StopBySwitch = "sw4";
    public const string StopByPanel = "操作ボタン";
    public const string StopByLogger = "LOGGER の停止";

    public As100201LearningSession(IAs100201LearningLedger ledger, Func<string, long> saveXdt,
        Func<DateTime>? clock = null, Func<string, bool>? fileExists = null, Action<string>? createDirectory = null)
    {
        _ledger = ledger;
        _saveXdt = saveXdt;
        _clock = clock ?? (() => DateTime.Now);
        _fileExists = fileExists ?? File.Exists;
        _createDirectory = createDirectory ?? (path => Directory.CreateDirectory(path));
    }

    // 保存先フォルダー。exe の隣の「学習データ」(2026-11-10 人の決定。それまでは雛形と同じフォルダー)。
    // 台帳はこの直下(呼び出し側が ledger に渡す)、xdt は日付フォルダー SaveDirectory\yyyyMMdd\ に置く。無ければ作る。
    public string SaveDirectory { get; init; } = "";
    public string LedgerLocation => _ledger.Location;

    public event Action<string>? Log;

    // 台帳か xdt に書けず、保存できるまで止めている。
    public bool Blocked { get; private set; }
    public string? BlockReason { get; private set; }
    // 止まった測定の xdt をまだ保存していない(LOGGER の停止待ち)。
    public bool HasArmedWaveform => _armedXdtPath is not null;
    public int MeasurementCount { get; private set; }
    public int OperationCount { get; private set; }
    public bool Completed { get; private set; }
    public int PendingCount => _pending.Count;

    public string StateText => Blocked
        ? $"保存できず停止中：{BlockReason}"
        : $"記録中：この個体の測定 {MeasurementCount} 回・操作 {OperationCount} 回" + (Completed ? "（完了済み）" : "");

    // ファイル名: yyyyMMdd_HHmmss_<側>.xdt(側は PC / CR、2 回押しは PC2 / CR2。側が無ければ日時だけ)。
    // 同じ秒に 2 つできたら _2, _3 … を付ける(2026-11-09 人の決定。台帳の行と 1 対 1)。
    // 置き場は日付フォルダー SaveDirectory\yyyyMMdd\(2026-11-10 人の決定。日付は止めた時刻の日付)。
    public string BuildXdtPath(DateTime time, string sideCode)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        string directory = Path.Combine(SaveDirectory, time.ToString("yyyyMMdd", culture));
        string stem = time.ToString("yyyyMMdd_HHmmss", culture) + (sideCode.Length == 0 ? "" : "_" + sideCode);
        string path = Path.Combine(directory, stem + ".xdt");
        for (int n = 2; _fileExists(path); n++) path = Path.Combine(directory, $"{stem}_{n}.xdt");
        return path;
    }

    // 台帳の「ファイル」列: 台帳のフォルダーからの相対パス(日付フォルダー\ファイル名)。
    public static string LedgerFileText(string xdtPath) =>
        Path.Combine(Path.GetFileName(Path.GetDirectoryName(xdtPath)) ?? "", Path.GetFileName(xdtPath));

    // LOGGER を止めた(sw4 か操作ボタン)。測定の行を書き、xdt の保存を停止待ちにする。
    public void MeasurementStopped(string side, string sideCode, string phase, string trigger)
    {
        DateTime now = _clock();
        string path = BuildXdtPath(now, sideCode);
        var row = BuildMeasurementRow(now, side, phase, trigger, path);
        _armedXdtPath = path;
        WriteOrQueue("測定の行", row);
    }

    // LOGGER の状態が停止に戻った。止めてあった測定の xdt を保存する。止めていなければ(自動で止まった)行も書く。
    public void MeasurementCompleted(string side, string sideCode, string phase)
    {
        string? path = _armedXdtPath;
        if (path is null)
        {
            DateTime now = _clock();
            path = BuildXdtPath(now, sideCode);
            WriteOrQueue("測定の行", BuildMeasurementRow(now, side, phase, StopByLogger, path));
        }
        _armedXdtPath = null;
        string target = path;
        RunOrQueue("xdt の保存", () => SaveWaveform(target));
    }

    private As100201LedgerRow BuildMeasurementRow(DateTime now, string side, string phase, string trigger, string path)
    {
        if (Completed)
        {
            // 完了の次の測定から新しい個体。
            Completed = false;
            MeasurementCount = 0;
            OperationCount = 0;
        }
        MeasurementCount++;
        _lastEventIsMeasurement = true;
        _undoable = null;
        return new As100201LedgerRow(now, As100201LedgerEventKind.Measurement)
        {
            Side = side,
            Phase = phase,
            StopTrigger = trigger,
            File = LedgerFileText(path),
            Note = side.Length == 0 ? "位相の出し直しなし" : ""
        };
    }

    private void SaveWaveform(string path)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory) _createDirectory(directory);
        long result = _saveXdt(path);
        if (result != 0)
            throw new InvalidOperationException($"WAVE LOGGER が xdt を保存できませんでした（戻り値 {result}）：{path}");
        Log?.Invoke("xdt を保存：" + LedgerFileText(path));
    }

    // パネルの行を 1 つ処理し、パネルへ返す行(ACK / NAK)を返す。
    public string HandleCommand(As100201PanelCommand command)
    {
        if (Blocked)
        {
            Log?.Invoke($"{command.DisplayName} を受信しましたが、保存できるまで記録を止めています（{BlockReason}）。");
            return As100201PanelProtocol.Nak("保存できるまで記録を止めています。再試行してください");
        }
        return command.Kind switch
        {
            As100201PanelCommandKind.Operation => RecordOperation(command),
            As100201PanelCommandKind.Done => RecordDone(command),
            _ => RecordUndo(command)
        };
    }

    private string RecordOperation(As100201PanelCommand command)
    {
        var notes = new List<string>();
        bool cancelsDone = Completed;
        if (cancelsDone) notes.Add("完了の取消");
        bool measurementBefore = _lastEventIsMeasurement;
        if (!measurementBefore) notes.Add("直前の測定なし");
        var row = new As100201LedgerRow(_clock(), As100201LedgerEventKind.Operation)
        {
            Operation = command.OperationText,
            Direction = command.Direction,
            Degree = command.Degree,
            Note = string.Join("、", notes)
        };
        if (!TryWrite("操作の行", row, out string nak)) return nak;
        if (cancelsDone) Completed = false;
        OperationCount++;
        _lastEventIsMeasurement = false;
        _undoable = new UndoTarget(row, measurementBefore);
        Log?.Invoke($"操作 {command.DisplayName} を記録" + (notes.Count == 0 ? "" : $"（{row.Note}）"));
        return As100201PanelProtocol.Ack(command);
    }

    private string RecordDone(As100201PanelCommand command)
    {
        if (Completed)
        {
            Log?.Invoke("完了は記録済みです（続けて押された完了は何もしません）。");
            return As100201PanelProtocol.Nak("完了は記録済みです");
        }
        var row = new As100201LedgerRow(_clock(), As100201LedgerEventKind.Done)
        {
            Note = $"この個体の測定 {MeasurementCount} 回・操作 {OperationCount} 回"
        };
        if (!TryWrite("完了の行", row, out string nak)) return nak;
        Completed = true;
        _lastEventIsMeasurement = false;
        _undoable = null;
        Log?.Invoke($"完了を記録（{row.Note}）");
        return As100201PanelProtocol.Ack(command);
    }

    private string RecordUndo(As100201PanelCommand command)
    {
        if (_undoable is not UndoTarget target)
        {
            Log?.Invoke("取消：取り消せる操作がありません（直前の操作だけ取り消せます）。");
            return As100201PanelProtocol.Nak("取り消せる操作がありません");
        }
        string described = DescribeOperation(target.Row);
        var row = new As100201LedgerRow(_clock(), As100201LedgerEventKind.Undo)
        {
            Note = "取消の対象: " + described
        };
        if (!TryWrite("取消の行", row, out string nak)) return nak;
        OperationCount--;
        _undoable = null;
        _lastEventIsMeasurement = target.MeasurementBefore;
        Log?.Invoke($"取消を記録：{described} を無効にしました");
        return As100201PanelProtocol.Ack(command);
    }

    private static string DescribeOperation(As100201LedgerRow row)
    {
        string details = string.Join("・", new[] { row.Direction, row.Degree }.Where(s => s.Length > 0));
        return $"{row.Time:HH:mm:ss} {row.Operation}" + (details.Length == 0 ? "" : $"（{details}）");
    }

    // 操作・完了・取消の行。書けなければ止めて NAK(行は覚えない。再試行のあとに押し直してもらう)。
    private bool TryWrite(string description, As100201LedgerRow row, out string nak)
    {
        try
        {
            AppendRow(row);
            nak = "";
            return true;
        }
        catch (Exception ex) when (IsWriteFailure(ex))
        {
            Block($"{description}を台帳に書けません：{ex.Message}");
            nak = As100201PanelProtocol.Nak("台帳に書けません。保存先を直して再試行してください");
            return false;
        }
    }

    // 測定の行。止まっていれば順番を守るため後ろに並べる。書けなければ覚えておいて止める。
    private void WriteOrQueue(string description, As100201LedgerRow row) =>
        RunOrQueue(description, () => AppendRow(row));

    // 台帳への追記。保存先フォルダーが無ければ先に作る(消されていても次の行で作り直す)。
    private void AppendRow(As100201LedgerRow row)
    {
        EnsureSaveDirectory();
        _ledger.Append(row);
    }

    private void EnsureSaveDirectory()
    {
        if (SaveDirectory.Length > 0) _createDirectory(SaveDirectory);
    }

    private void RunOrQueue(string description, Action write)
    {
        if (Blocked)
        {
            _pending.Add(new PendingWrite(description, write));
            Log?.Invoke($"{description}は保存できるようになってから書きます（{_pending.Count} 件待ち）。");
            return;
        }
        try { write(); }
        catch (Exception ex) when (IsWriteFailure(ex))
        {
            _pending.Add(new PendingWrite(description, write));
            Block($"{description}に失敗：{ex.Message}");
        }
    }

    // 「再試行」。覚えていたものを順に書き直し、台帳が書けることを確かめて再開する。失敗したら止まったまま。
    public bool Retry()
    {
        try
        {
            while (_pending.Count > 0)
            {
                var item = _pending[0];
                item.Write();
                _pending.RemoveAt(0);
                Log?.Invoke($"再試行：{item.Description}を書きました。");
            }
            EnsureSaveDirectory();
            _ledger.EnsureWritable();
        }
        catch (Exception ex) when (IsWriteFailure(ex))
        {
            Block($"再試行に失敗：{ex.Message}");
            return false;
        }
        if (Blocked) Log?.Invoke("再試行：保存できるようになりました。記録を再開します。");
        Blocked = false;
        BlockReason = null;
        return true;
    }

    private void Block(string reason)
    {
        Blocked = true;
        BlockReason = reason;
        Log?.Invoke(reason + " / 保存できるまで PC_go・CR_go・F8 と操作ボタンを受け付けません。保存先を直して「再試行」を押してください。");
    }

    // 台帳(ファイル)と xdt(LOGGER の COM 呼び出し)の失敗。プログラムの誤り(引数など)は止めずに上へ出す。
    private static bool IsWriteFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or
        InvalidOperationException or System.Runtime.InteropServices.COMException or NotSupportedException;
}
