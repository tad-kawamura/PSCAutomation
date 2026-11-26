namespace PSCAutomation.Apps.AS100201SwitchController;

// 学習モードの記録の中身(SPEC_調整操作の記録.md「記録の考え方」「出力」「異常時」)。画面・機器に依存しない。
// 1 個体は「測定 → 操作 → … → 測定 → 完了」の列。台帳へ 1 行 1 出来事で追記し、測定のたびに xdt を保存する。
//   - 測定の行は LOGGER が止まった時点で書き(sw4 / 操作ボタン)、xdt は LOGGER の状態が停止に戻った時点で保存する
//     (停止直後はデータの読み出し中のことがあるため)。LOGGER が自動で止まったときは、停止に戻った時点で行と xdt を一緒に書く
//   - 完了のあとに操作が来たら「完了の取消」(同じ個体の続き)。測定の行がないのに操作が来たら「直前の測定なし」
//   - 取消は直前の操作 1 件だけ。操作のあとに測定・完了があれば取り消せない
//   - 台帳や xdt に書けなかったら止まる(Blocked)。測定の行と xdt は覚えておいて「再試行」で書き直す。
//     操作ボタンには NAK を返し、記録しない(再試行のあとに押し直してもらう)
//   - 2026-11-26 からシリアル番号で個体を分ける(SPEC_調整操作の記録.md「シリアル番号で管理する」)。番号は PC の画面で入れ、
//     起動直後と完了のあとは空。空の間は操作・完了・取消に NAK を返す(測定の開始は MainForm が止める)。
//     同じ番号をもう一度入れたら、台帳から数え直して続きにする。完了済みの番号の続きは、次の操作で「完了の取消」
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
    // 測定中の取り込みに失敗して、アプリが LOGGER を止めた(SPEC_測定中の取り込み.md、2026-11-11)。
    public const string StopByCapture = "取り込み異常";
    // 番号が空の間にパネルの操作・完了・取消が届いたときの返事(Tab5 の下の帯に出る)。
    public const string NakNoSerial = "シリアル番号を入れてください";

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
    // 台帳はこの直下(呼び出し側が ledger に渡す)、xdt はシリアル番号のフォルダー SaveDirectory\SN145\ に置く
    // (2026-11-26。それまでは日付フォルダー)。無ければ作る。
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
    // 今の個体のシリアル番号(SN145 の形)。起動直後と完了のあとは null(入れるまで測定・操作を受け付けない)。
    public string? SerialNumber { get; private set; }

    public string StateText => Blocked
        ? $"保存できず停止中：{BlockReason}"
        : SerialNumber is null
            ? "シリアル番号を入れてください（入れるまで測定・操作を受け付けません）"
            : $"記録中：{SerialNumber}　この個体の測定 {MeasurementCount} 回・操作 {OperationCount} 回" + (Completed ? "（完了済み）" : "");

    // 番号が無いときの xdt の置き場(番号を入れずに LOGGER が止まったときなど。ふだんは使わない。AI の仮決め)。
    public const string NoSerialFolderName = "番号なし";

    // 入れた文字をシリアル番号にする: 数字だけ(先頭の SN は付けても付けなくてもよい)。3 桁にそろえ、1000 以上はそのまま。
    // 0 と 6 桁以上は受け付けない(打ち間違いとみなす。AI の仮決め)。
    public static bool TryFormatSerial(string? input, out string serial, out string error)
    {
        serial = "";
        string text = (input ?? "").Trim();
        if (text.StartsWith("SN", StringComparison.OrdinalIgnoreCase)) text = text[2..].Trim();
        if (text.Length == 0) { error = "シリアル番号を数字で入れてください。"; return false; }
        if (!text.All(char.IsAsciiDigit)) { error = "シリアル番号は数字だけで入れてください（SN は自動で付きます）：" + input; return false; }
        string digits = text.TrimStart('0');
        if (digits.Length == 0) { error = "シリアル番号に 0 は使えません。"; return false; }
        if (digits.Length > 5) { error = "シリアル番号の桁が多すぎます：" + input; return false; }
        serial = "SN" + digits.PadLeft(3, '0');
        error = "";
        return true;
    }

    // ファイル名: yyyyMMdd_HHmmss_<番号>_<側>.xdt(側は PC / CR、2 回押しは PC2 / CR2。側が無ければ側を付けない)。
    // 同じ秒に 2 つできたら _2, _3 … を付ける(2026-11-09 人の決定。台帳の行と 1 対 1)。
    // 置き場はシリアル番号のフォルダー SaveDirectory\SN145\(2026-11-26 人の決定。それまでは日付フォルダー)。
    public string BuildXdtPath(DateTime time, string sideCode)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        string directory = Path.Combine(SaveDirectory, SerialNumber ?? NoSerialFolderName);
        string stem = time.ToString("yyyyMMdd_HHmmss", culture) + (SerialNumber is null ? "" : "_" + SerialNumber) +
            (sideCode.Length == 0 ? "" : "_" + sideCode);
        string path = Path.Combine(directory, stem + ".xdt");
        for (int n = 2; _fileExists(path); n++) path = Path.Combine(directory, $"{stem}_{n}.xdt");
        return path;
    }

    // 台帳にある、ある番号の今までの記録(続きとして数え直すため)。
    public sealed record SerialHistory(string SerialNumber, int MeasurementCount, int OperationCount, bool Completed,
        bool LastIsMeasurement, DateTime LastTime);

    // 台帳からその番号の記録を探す。無ければ null。台帳を読めなければ例外(呼び出し側で知らせる)。
    public SerialHistory? FindSerial(string serial) => BuildHistory(_ledger.ReadRows(), serial);

    // 台帳の行から、その番号の測定・操作の回数と完了かどうかを数える。取消は直前の操作を消す(押した・番号の行は数えない)。
    public static SerialHistory? BuildHistory(IEnumerable<As100201LedgerRow> rows, string serial)
    {
        var events = new List<As100201LedgerEventKind>();
        int measurements = 0, operations = 0;
        bool completed = false, found = false;
        DateTime last = default;
        foreach (var row in rows)
        {
            if (row.SerialNumber != serial) continue;
            found = true;
            last = row.Time;
            switch (row.Kind)
            {
                case As100201LedgerEventKind.Measurement:
                    measurements++;
                    events.Add(row.Kind);
                    break;
                case As100201LedgerEventKind.Operation:
                    operations++;
                    completed = false;
                    events.Add(row.Kind);
                    break;
                case As100201LedgerEventKind.Done:
                    completed = true;
                    events.Add(row.Kind);
                    break;
                case As100201LedgerEventKind.Undo:
                    int index = events.LastIndexOf(As100201LedgerEventKind.Operation);
                    if (index >= 0) { events.RemoveAt(index); operations--; }
                    break;
            }
        }
        if (!found) return null;
        return new SerialHistory(serial, measurements, operations, completed,
            events.Count > 0 && events[^1] == As100201LedgerEventKind.Measurement, last);
    }

    // 台帳を読めるか・列の形が合うかを確かめる(起動時)。読めなければ止めて理由を出す(「再試行」で確かめ直す)。
    public bool CheckLedger()
    {
        try
        {
            _ledger.ReadRows();
            return true;
        }
        catch (Exception ex) when (IsWriteFailure(ex))
        {
            Block("台帳を読めません：" + ex.Message);
            return false;
        }
    }

    // PC の画面でシリアル番号を決めた。番号の行を書き、台帳の記録から回数と完了かどうかを引き継ぐ。
    // history: FindSerial の結果(新しい番号なら null)。続きにするかの確認は呼び出し側(画面)で済ませておく。
    // 書けなければ止めて false(番号は変えない)。
    public bool SelectSerial(string serial, SerialHistory? history)
    {
        if (Blocked)
        {
            Log?.Invoke($"シリアル番号 {serial} は、保存できるまで決められません（{BlockReason}）。");
            return false;
        }
        var notes = new List<string>
        {
            history is null ? "新規" : history.Completed ? "続き（完了済み）" : "続き（未完了）"
        };
        string? previous = SerialNumber;
        bool previousUnfinished = previous is not null && previous != serial && !Completed;
        if (previousUnfinished) notes.Add($"前の番号 {previous} は完了せず切り替え");
        var row = new As100201LedgerRow(_clock(), As100201LedgerEventKind.SerialSet)
        {
            SerialNumber = serial,
            Note = string.Join("、", notes)
        };
        if (!TryWrite("番号の行", row, out _)) return false;
        SerialNumber = serial;
        MeasurementCount = history?.MeasurementCount ?? 0;
        OperationCount = history?.OperationCount ?? 0;
        Completed = history?.Completed ?? false;
        _lastEventIsMeasurement = history?.LastIsMeasurement ?? false;
        _undoable = null;
        if (previousUnfinished) Log?.Invoke($"{previous} は完了せずに次の番号へ切り替えました。");
        Log?.Invoke(history is null
            ? $"シリアル番号 {serial} を記録します（新規）。"
            : $"シリアル番号 {serial} の続きを記録します（{(history.Completed ? "完了済み" : "未完了")}、これまでの測定 {history.MeasurementCount} 回・操作 {history.OperationCount} 回）。");
        return true;
    }

    // 台帳の「ファイル」列: 台帳のフォルダーからの相対パス(番号のフォルダー\ファイル名)。
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
    // features: 測定中に取り込んで加工した値(SPEC_測定中の取り込み.md)。xdt の後に、同じ名前の .csv で書く。
    // 書けなければ xdt と同じく覚えておいて止め、「再試行」で書き直す。
    public void MeasurementCompleted(string side, string sideCode, string phase, As100201FeatureTable? features = null)
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
        if (features is not null)
        {
            string csv = FeaturesPath(target);
            RunOrQueue("取り込みの CSV", () => SaveFeatures(features, csv));
        }
    }

    // PC_go / CR_go で WF1973 を出し直した(2026-11-12。SPEC_測定中の取り込み.md「押した瞬間の記録」)。
    // 区間をあとで切り出せるよう、押すたびに「押した」行を足す。細かい値は「備考」に書く(形は試しながら変える。AI の仮決め)。
    //   secondsSinceStart / pointCount: 測定中なら F8 からの秒と、そのときの LOGGER の点数。測定していなければ null
    //   countProblem: 測定中なのに点数を読めなかったときの理由
    // 押した行は個体の数え方・「直前の測定なし」・取消の対象を変えない(調整の操作ではないため)。
    // 書けなければ測定の行と同じく覚えておいて止め、「再試行」で書き直す。
    public void WaveformRestarted(string side, string phase, double? secondsSinceStart, long? pointCount, string? countProblem = null)
    {
        DateTime now = _clock();
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var notes = new List<string> { now.ToString("HH:mm:ss.fff", culture) };
        if (secondsSinceStart is double seconds) notes.Add("F8 から " + seconds.ToString("0.000", culture) + " 秒");
        else notes.Add("測定していないとき");
        if (pointCount is long count) notes.Add($"点数 {count}（xdt の {count + 1} 点目から後）");
        else if (countProblem is not null) notes.Add("点数を読めず：" + countProblem);
        var row = new As100201LedgerRow(now, As100201LedgerEventKind.Pressed)
        {
            SerialNumber = SerialNumber ?? "",
            Side = side,
            Phase = phase,
            Note = string.Join("、", notes)
        };
        WriteOrQueue("押した行", row);
        // 書けなかった(待たせた)ときは WriteOrQueue / Block がログを出す。
        if (!Blocked) Log?.Invoke($"押した {(side.Length == 0 ? "（側なし）" : side)}（位相 {phase}°）を台帳に記録：{row.Note}");
    }

    // 取り込みの CSV: xdt と同じフォルダー・同じ名前で拡張子だけ .csv。
    public static string FeaturesPath(string xdtPath) => Path.ChangeExtension(xdtPath, ".csv");

    private void SaveFeatures(As100201FeatureTable features, string path)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory) _createDirectory(directory);
        features.Write(path);
        Log?.Invoke($"取り込みの CSV を保存：{LedgerFileText(path)}（{features.Rows.Count} 行）");
    }

    private As100201LedgerRow BuildMeasurementRow(DateTime now, string side, string phase, string trigger, string path)
    {
        // 個体の区切りはシリアル番号(2026-11-26)。完了済みの番号の続きで測っても、同じ個体のまま数える
        // (それまでは「完了の次の測定から新しい個体」として数え直していた)。
        MeasurementCount++;
        _lastEventIsMeasurement = true;
        _undoable = null;
        return new As100201LedgerRow(now, As100201LedgerEventKind.Measurement)
        {
            SerialNumber = SerialNumber ?? "",
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
        // パネルに触れた合図(HOLD)は台帳に書かない。LOGGER を止めるのは呼び出し側(MainForm)。
        if (command.Kind == As100201PanelCommandKind.Hold) return As100201PanelProtocol.Ack(command);
        if (Blocked)
        {
            Log?.Invoke($"{command.DisplayName} を受信しましたが、保存できるまで記録を止めています（{BlockReason}）。");
            return As100201PanelProtocol.Nak("保存できるまで記録を止めています。再試行してください");
        }
        if (SerialNumber is null)
        {
            Log?.Invoke($"{command.DisplayName} を受信しましたが、シリアル番号が入っていないため記録しません（PC の画面で入れてください）。");
            return As100201PanelProtocol.Nak(NakNoSerial);
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
            SerialNumber = SerialNumber ?? "",
            Operation = command.OperationText,
            Direction = command.Direction,
            Degree = command.Degree,
            Thickness = command.Thickness,
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
            SerialNumber = SerialNumber ?? "",
            Note = $"この個体の測定 {MeasurementCount} 回・操作 {OperationCount} 回"
        };
        if (!TryWrite("完了の行", row, out string nak)) return nak;
        Completed = true;
        _lastEventIsMeasurement = false;
        _undoable = null;
        Log?.Invoke($"完了を記録：{SerialNumber}（{row.Note}）。次の個体のシリアル番号を PC の画面で入れてください。");
        // 完了したら番号を空にする。次の個体の番号を入れるまで測定・操作を受け付けない(2026-11-26 人の決定)。
        SerialNumber = null;
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
            SerialNumber = SerialNumber ?? "",
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
        string details = string.Join("・", new[] { row.Direction, row.Thickness, row.Degree }.Where(s => s.Length > 0));
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
