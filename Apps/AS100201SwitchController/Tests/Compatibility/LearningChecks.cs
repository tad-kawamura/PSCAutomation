// 学習モード(SPEC_調整操作の記録.md、2026-11-09 承認)の模擬テスト。機器・LOGGER・タッチパネルは使わない。
// グループ: パネルの行の読み取り / 台帳の行の組み立て / 完了・取消・完了の取消 / 書けないときの停止と再試行 /
//           パネル途切れ中の受け付け / 通常モードを選んだとき今までと同じ / タッチパネルの代わり(開発用)
using PSCAutomation.Apps.AS100201SwitchController;
using System.Text;

internal static class LearningChecks
{
    private static void Check(bool ok, string message) => Program.Check(ok, message);
    private static void Pass(string message) => Program.Pass(message);

    public static async Task Run()
    {
        PanelCommandChecks();
        LedgerRowChecks();
        SessionFlowChecks();
        WriteFailureChecks();
        await PanelOutageChecks();
        await VirtualPanelChecks();
        // 画面(MainForm)を作るとこのスレッドに WinForms の SynchronizationContext が入り、以後の await が戻らなくなる。
        // 画面を作る確認は await の後に置く(VirtualPanelChecks も画面の確認は最後で、その後に await しない)。
        NormalModeChecks();
    }

    // 7. タッチパネルの代わり(開発用、--virtual-panel、2026-11-10)。本物と同じ As100201PanelProtocol を通ること
    private static async Task VirtualPanelChecks()
    {
        var link = new As100201VirtualPanelLink();
        using var panel = new As100201PanelProtocol(link);
        var received = new List<As100201PanelCommand>();
        var sent = new List<string>();
        var statuses = new List<string>();
        int disconnected = 0;
        panel.CommandReceived += received.Add;
        panel.StatusChanged += statuses.Add;
        panel.Disconnected += () => disconnected++;
        link.LineSent += sent.Add;

        // 並べる行は全部パネルの行として読め、重複がない(着磁 1・減磁 2・ノズル 12・スペーサー 4・完了・取消)。
        var lines = As100201VirtualPanelLink.AllCommands.Select(c => c.Line).ToList();
        Check(lines.Count == 21 && lines.Distinct().Count() == 21 && lines.Contains("ACT:NOZZLE:R:OUT:3") && lines.Contains("ACT:SPACER:P:DOWN") &&
            lines[^2] == "DONE" && lines[^1] == "UNDO", "virtual panel command list");
        Check(!link.Press("ACT:MAG") && received.Count == 0, "pressed before connection");

        await panel.VerifyAsync();
        Check(panel.IsConnected && !panel.InputReady, "virtual panel verified but ready before frames");
        // 定期応答はアプリの中で 250ms ごとに出る。1 秒の安定確認のあとに受け付ける。
        await Task.Delay(1600);
        Check(panel.InputReady, "virtual panel frames did not make input ready");
        Check(link.Press("ACT:NOZZLE:P:IN:2") && received.Count == 1 && received[0].DisplayName == "ノズル P 側（押し込む・普通）", "virtual press not delivered");
        Check(panel.TrySend(As100201PanelProtocol.Ack(received[0])) && sent.SequenceEqual(new[] { "ACK:ACT:NOZZLE:P:IN:2" }), "reply to virtual panel not shown");

        // 定期応答を止める: 1 秒で受け付けなくなり、押しても NAK。再開すると 1 秒後に戻る。
        link.FramesPaused = true;
        await Task.Delay(1300);
        Check(!panel.InputReady, "paused frames still ready");
        await panel.MaintainAsync();
        Check(statuses.Count > 0 && statuses[^1].Contains("定期応答が停止"), "paused frames not reported");
        Check(link.Press("ACT:MAG") && received.Count == 1 && sent[^1].StartsWith("NAK:"), "press during pause not refused");
        link.FramesPaused = false;
        await Task.Delay(1600);
        Check(panel.InputReady, "not ready after frames resumed");

        // USB を抜いた状態: 切断を知らせ、差し直すまで再接続に失敗し、差し直すと再接続する。
        link.Unplugged = true;
        Check(disconnected == 1 && !panel.IsConnected && !panel.InputReady && !link.Press("DONE"), "unplug not propagated");
        await panel.MaintainAsync();
        Check(!panel.IsConnected && statuses[^1].Contains("接続を待っています"), "reconnected while unplugged");
        link.Unplugged = false;
        await Task.Delay(2100);
        await panel.MaintainAsync();
        Check(panel.IsConnected && statuses[^1].Contains("再接続"), "not reconnected after replug");
        await Task.Delay(1600);
        Check(panel.InputReady && link.Press("DONE") && received[^1].Kind == As100201PanelCommandKind.Done, "not usable after replug");

        // 代わりのパネルは --virtual-panel のときだけ。画面にはタイトル・帯・代わりのボタンが加わる。
        Check(As100201VirtualPanelLink.CommandLineSwitch == "--virtual-panel", "command line switch");
        using (var learning = new MainForm(As100201StartupMode.Learning))
            Check(!learning.UsesVirtualPanel && learning.Controls.Count == 4, "virtual panel shown without the switch");
        using (var normal = new MainForm(As100201StartupMode.Normal, virtualPanel: true))
            Check(!normal.UsesVirtualPanel && !normal.IsLearningMode && normal.Controls.Count == 1, "virtual panel in normal mode");
        using (var dev = new MainForm(As100201StartupMode.Learning, virtualPanel: true))
        {
            Check(dev.UsesVirtualPanel && dev.IsLearningMode && dev.Text == As100201Application.LearningWindowTitle + As100201Application.VirtualPanelTitleSuffix, "virtual panel title");
            Check(dev.Controls.Count == 5 && FindLabels(dev).Any(l => l.Text.StartsWith("パネルへの返事")), "virtual panel controls missing");
        }
        Pass("virtual panel (--virtual-panel, dev only): 21 panel lines, same protocol path, 250 ms frames -> ready, ACK/NAK shown, pause -> stale + NAK, unplug/replug reconnect, only with the switch in learning mode");
    }

    // 1. パネルの行の読み取り
    private static void PanelCommandChecks()
    {
        Check(As100201PanelCommand.TryParse("ACT:MAG", out var c) && c.Kind == As100201PanelCommandKind.Operation &&
            c.Operation == As100201Operation.Magnetize && c.Direction == "" && c.Degree == "" && c.OperationText == "着磁" && c.DisplayName == "着磁", "MAG");
        Check(As100201PanelCommand.TryParse("ACT:DEMAG:FULL", out c) && c.Operation == As100201Operation.Demagnetize && c.Degree == "しっかり" && c.DisplayName == "減磁（しっかり）", "DEMAG FULL");
        Check(As100201PanelCommand.TryParse("ACT:DEMAG:TAP", out c) && c.Degree == "ちょんちょん", "DEMAG TAP");
        Check(As100201PanelCommand.TryParse("ACT:NOZZLE:P:IN:2", out c) && c.Operation == As100201Operation.NozzleP && c.Direction == "押し込む" &&
            c.Degree == "普通" && c.OperationText == "ノズル P" && c.DisplayName == "ノズル P 側（押し込む・普通）", "NOZZLE P IN 2");
        Check(As100201PanelCommand.TryParse("ACT:NOZZLE:R:OUT:1", out c) && c.Operation == As100201Operation.NozzleR && c.Direction == "外に出す" && c.Degree == "微調整", "NOZZLE R OUT 1");
        Check(As100201PanelCommand.TryParse("ACT:NOZZLE:R:IN:3", out c) && c.Degree == "大きく", "NOZZLE R IN 3");
        Check(As100201PanelCommand.TryParse("ACT:SPACER:P:UP", out c) && c.Operation == As100201Operation.SpacerP && c.Direction == "上げる" && c.Degree == "" &&
            c.DisplayName == "スペーサー P 側（上げる）", "SPACER P UP");
        Check(As100201PanelCommand.TryParse("ACT:SPACER:R:DOWN", out c) && c.Operation == As100201Operation.SpacerR && c.Direction == "下げる", "SPACER R DOWN");
        Check(As100201PanelCommand.TryParse("DONE", out c) && c.Kind == As100201PanelCommandKind.Done && c.Operation is null && c.DisplayName == "完了", "DONE");
        Check(As100201PanelCommand.TryParse("UNDO", out c) && c.Kind == As100201PanelCommandKind.Undo && c.DisplayName == "取消", "UNDO");
        foreach (string bad in new[] { "ACT", "ACT:", "ACT:MAG:1", "ACT:DEMAG", "ACT:DEMAG:HALF", "ACT:NOZZLE:P:IN", "ACT:NOZZLE:P:IN:4", "ACT:NOZZLE:X:IN:2",
            "ACT:NOZZLE:P:UP:2", "ACT:SPACER:P", "ACT:SPACER:P:IN", "ACT:SPACER:P:UP:0.1", "act:mag", "DONE ", "UNDO:1", "PC_go", "DIAG_FRAME:1:250", "AS100201_PANEL_READY:1", "" })
            Check(!As100201PanelCommand.TryParse(bad, out _), "accepted bad panel line: " + bad);
        Check(As100201PanelProtocol.Ack(As100201PanelCommand.Parse("ACT:NOZZLE:P:IN:2")) == "ACK:ACT:NOZZLE:P:IN:2", "ACK format");
        Check(As100201PanelProtocol.Nak("理由") == "NAK:理由", "NAK format");
        Check(As100201PanelProtocol.CheckHello("AS100201_PANEL_READY:1") is null, "panel version 1 rejected");
        Check(As100201PanelProtocol.CheckHello("AS100201_PANEL_READY:2")?.Contains("版 2") == true, "panel version 2 accepted");
        Check(As100201PanelProtocol.CheckHello("AS100201_READY:2") is not null && As100201PanelProtocol.CheckHello(null)?.Contains("不明") == true, "Arduino reply accepted as panel");
        Check(As100201PanelProtocol.HelloRequest != "AS100201_PING" && !As100201PanelProtocol.HelloPrefix.StartsWith(As100201ArduinoProtocol.HelloPrefix) &&
            !As100201ArduinoProtocol.HelloPrefix.StartsWith(As100201PanelProtocol.HelloPrefix), "panel and Arduino hello strings collide");
        Pass("panel lines: ACT:MAG / DEMAG / NOZZLE / SPACER / DONE / UNDO parsed with ledger words; malformed lines rejected; ACK/NAK; panel version 1 only");
    }

    // 2. 台帳の行の組み立て(CSV の形)
    private static void LedgerRowChecks()
    {
        var time = new DateTime(2026, 11, 9, 13, 5, 7);
        Check(As100201LedgerRow.HeaderLine == "日時,種類,側,位相,停止のきっかけ,操作,方向,程度,厚さ,ファイル,備考", "header columns");
        var measurement = new As100201LedgerRow(time, As100201LedgerEventKind.Measurement) { Side = "PC（2回押し）", Phase = "75", StopTrigger = "sw4", File = "20261109_130507_PC2.xdt" };
        Check(measurement.ToCsvLine() == "2026-11-09 13:05:07,測定,PC（2回押し）,75,sw4,,,,,20261109_130507_PC2.xdt,", "measurement row");
        var operation = new As100201LedgerRow(time, As100201LedgerEventKind.Operation) { Operation = "ノズル P", Direction = "押し込む", Degree = "普通", Note = "完了の取消、直前の測定なし" };
        Check(operation.ToCsvLine() == "2026-11-09 13:05:07,操作,,,,ノズル P,押し込む,普通,,,完了の取消、直前の測定なし", "operation row");
        Check(new As100201LedgerRow(time, As100201LedgerEventKind.Done).KindText == "完了" && new As100201LedgerRow(time, As100201LedgerEventKind.Undo).KindText == "取消", "kind text");
        Check(MainForm.SaveDirectoryForTemplate(@"C:\data\AS100-201\流量PC.xdt") == @"C:\data\AS100-201" && MainForm.LedgerFileName == "調整記録.csv", "save directory is not the template folder");
        Check(As100201LedgerRow.Quote("a,b") == "\"a,b\"" && As100201LedgerRow.Quote("say \"hi\"") == "\"say \"\"hi\"\"\"" && As100201LedgerRow.Quote("plain") == "plain" &&
            As100201LedgerRow.Quote("x\ny") == "\"x\ny\"", "CSV quoting");
        Check(measurement.ToFields().Length == As100201LedgerRow.Columns.Length, "field count differs from columns");

        string root = Path.Combine(Path.GetTempPath(), "AS100201LearningTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var ledger = new As100201CsvLedger(Path.Combine(root, "調整記録.csv"));
            ledger.EnsureWritable();
            ledger.Append(measurement);
            ledger.Append(operation);
            byte[] bytes = File.ReadAllBytes(ledger.Location);
            Check(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "UTF-8 BOM missing");
            string[] lines = File.ReadAllText(ledger.Location, new UTF8Encoding(false)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Check(lines.Length == 3 && lines[0].TrimStart('\uFEFF') == As100201LedgerRow.HeaderLine && lines[1] == measurement.ToCsvLine() && lines[2] == operation.ToCsvLine(),
                "header once then rows in order");
            // 人が Excel で開いていても占有していなければ書ける(共有読み取り)。
            using (var reader = new FileStream(ledger.Location, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                ledger.Append(operation);
            Check(File.ReadAllLines(ledger.Location).Length == 4, "append while shared-read open");
            // 占有されていれば書けない(例外)。
            using (var locker = new FileStream(ledger.Location, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try { ledger.Append(operation); throw new Exception("append while locked succeeded"); }
                catch (IOException) { }
                try { ledger.EnsureWritable(); throw new Exception("EnsureWritable while locked succeeded"); }
                catch (IOException) { }
            }
            var missing = new As100201CsvLedger(Path.Combine(root, "nodir", "x.csv"));
            try { missing.EnsureWritable(); throw new Exception("missing directory accepted"); }
            catch (DirectoryNotFoundException) { }
            try { missing.Append(operation); throw new Exception("append to missing directory succeeded"); }
            catch (IOException) { }
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        Pass("ledger rows: 11 columns, RFC4180 quoting, CSV file with BOM + header once, append-only, locked/missing folder -> exception");
    }

    private sealed class FakeLedger : IAs100201LearningLedger
    {
        public List<As100201LedgerRow> Rows { get; } = new();
        public bool Fail;
        public string Location => @"C:\fake\調整記録.csv";
        public void Append(As100201LedgerRow row) { if (Fail) throw new IOException("fake ledger failure"); Rows.Add(row); }
        public void EnsureWritable() { if (Fail) throw new IOException("fake ledger failure"); }
    }

    private static (As100201LearningSession Session, FakeLedger Ledger, List<string> Saved, List<string> Logs, Func<long> SaveResult) NewSession(DateTime start)
    {
        var ledger = new FakeLedger();
        var saved = new List<string>();
        var logs = new List<string>();
        long saveResult = 0;
        DateTime now = start;
        var session = new As100201LearningSession(ledger, path => { if (saveResult != 0) return saveResult; saved.Add(path); return 0; },
            () => now = now.AddSeconds(1), path => saved.Contains(path))
        { SaveDirectory = @"C:\fake" };
        session.Log += logs.Add;
        return (session, ledger, saved, logs, () => saveResult);
    }

    // 3. 完了・取消・完了の取消(1 個体の流れ)
    private static void SessionFlowChecks()
    {
        var (s, ledger, saved, logs, _) = NewSession(new DateTime(2026, 11, 9, 9, 0, 0));
        var mag = As100201PanelCommand.Parse("ACT:MAG");
        var nozzle = As100201PanelCommand.Parse("ACT:NOZZLE:P:IN:2");
        var done = As100201PanelCommand.Parse("DONE");
        var undo = As100201PanelCommand.Parse("UNDO");

        // 起動直後に操作: 「直前の測定なし」。測定: sw4 で止めて行、停止に戻ってから xdt。
        Check(s.HandleCommand(mag) == "ACK:ACT:MAG" && ledger.Rows[^1].Kind == As100201LedgerEventKind.Operation && ledger.Rows[^1].Note == "直前の測定なし", "first operation without measurement");
        s.MeasurementStopped("PC", "PC", "0", As100201LearningSession.StopBySwitch);
        Check(ledger.Rows[^1] is { Kind: As100201LedgerEventKind.Measurement, Side: "PC", Phase: "0", StopTrigger: "sw4", File: "20261109_090002_PC.xdt", Note: "" } && s.HasArmedWaveform && saved.Count == 0,
            "measurement row at stop, xdt not yet saved");
        s.MeasurementCompleted("PC", "PC", "0");
        Check(!s.HasArmedWaveform && saved.SequenceEqual(new[] { @"C:\fake\20261109_090002_PC.xdt" }) && ledger.Rows.Count == 2, "xdt saved at completion, no extra row");
        // 操作 → 測定(操作ボタンで止めた) → 操作 → 測定(LOGGER が自動で止まった: 行と xdt を一緒に)
        Check(s.HandleCommand(nozzle) == "ACK:ACT:NOZZLE:P:IN:2" && ledger.Rows[^1] is { Operation: "ノズル P", Direction: "押し込む", Degree: "普通", Note: "" }, "operation after measurement");
        s.MeasurementStopped("CR（2回押し）", "CR2", "250", As100201LearningSession.StopByPanel);
        s.MeasurementCompleted("CR（2回押し）", "CR2", "250");
        Check(ledger.Rows[^1] is { Kind: As100201LedgerEventKind.Measurement, Side: "CR（2回押し）", Phase: "250", StopTrigger: "操作ボタン", File: "20261109_090004_CR2.xdt" }, "measurement stopped by panel");
        Check(s.HandleCommand(As100201PanelCommand.Parse("ACT:DEMAG:TAP")) == "ACK:ACT:DEMAG:TAP", "demag");
        s.MeasurementCompleted("CR", "CR", "140");
        Check(ledger.Rows[^1] is { Kind: As100201LedgerEventKind.Measurement, StopTrigger: "LOGGER の停止", File: "20261109_090006_CR.xdt" } && saved[^1].EndsWith("20261109_090006_CR.xdt"), "auto-stopped measurement: row and xdt together");
        Check(s.MeasurementCount == 3 && s.OperationCount == 3 && !s.Completed, "counts");

        // 完了: 件数を備考に。2 回目は何もしない(NAK、行なし)。
        int before = ledger.Rows.Count;
        Check(s.HandleCommand(done) == "ACK:DONE" && ledger.Rows[^1] is { Kind: As100201LedgerEventKind.Done, Note: "この個体の測定 3 回・操作 3 回" } && s.Completed, "done row");
        Check(s.HandleCommand(done).StartsWith("NAK:") && ledger.Rows.Count == before + 1, "second done recorded");
        // 完了の後の取消: 取り消せる操作がない。
        Check(s.HandleCommand(undo).StartsWith("NAK:") && ledger.Rows.Count == before + 1 && logs[^1].Contains("取り消せる操作がありません"), "undo after done");
        // 完了のあとに操作: 「完了の取消」+「直前の測定なし」、同じ個体の続き。
        Check(s.HandleCommand(mag) == "ACK:ACT:MAG" && ledger.Rows[^1].Note == "完了の取消、直前の測定なし" && !s.Completed && s.OperationCount == 4, "operation after done cancels done");
        // 取消: 直前の操作 1 件だけ。2 回目は NAK。取消のあと操作は「直前の測定なし」のまま(取り消した操作の前の状態に戻る)。
        Check(s.HandleCommand(undo) == "ACK:UNDO" && ledger.Rows[^1] is { Kind: As100201LedgerEventKind.Undo } && ledger.Rows[^1].Note.StartsWith("取消の対象: 09:00:") &&
            ledger.Rows[^1].Note.EndsWith(" 着磁") && s.OperationCount == 3, "undo row");
        Check(s.HandleCommand(undo).StartsWith("NAK:"), "second undo");
        Check(s.HandleCommand(nozzle) == "ACK:ACT:NOZZLE:P:IN:2" && ledger.Rows[^1].Note == "直前の測定なし", "state restored after undo");
        // 測定の次の操作は取り消せるが、測定を挟むと取り消せない。
        s.MeasurementStopped("PC", "PC", "0", As100201LearningSession.StopBySwitch);
        s.MeasurementCompleted("PC", "PC", "0");
        Check(s.HandleCommand(undo).StartsWith("NAK:"), "undo across measurement");
        Check(s.HandleCommand(As100201PanelCommand.Parse("ACT:SPACER:R:DOWN")) == "ACK:ACT:SPACER:R:DOWN" && ledger.Rows[^1].Note == "", "spacer after measurement");
        Check(s.HandleCommand(undo) == "ACK:UNDO" && ledger.Rows[^1].Note.EndsWith(" スペーサー R（下げる）"), "undo spacer");
        // 完了 → 測定: 新しい個体(件数を 0 から)。
        Check(s.HandleCommand(done) == "ACK:DONE", "done 2");
        s.MeasurementStopped("", "", "", As100201LearningSession.StopBySwitch);
        Check(ledger.Rows[^1] is { Side: "", Phase: "", Note: "位相の出し直しなし", File: "20261109_090015.xdt" } && s.MeasurementCount == 1 && s.OperationCount == 0 && !s.Completed, "new individual after done");
        // 同じ秒に 2 つ目の xdt は _2。
        s.MeasurementCompleted("", "", "");
        Check(s.BuildXdtPath(new DateTime(2026, 11, 9, 9, 0, 15), "") == @"C:\fake\20261109_090015_2.xdt", "duplicate name suffix");
        Check(ledger.Rows.All(r => r.KindText is "測定" or "操作" or "完了" or "取消") && !s.Blocked, "unexpected kinds or blocked");
        Pass("learning flow: first-op note, row at stop + xdt at completion, panel/auto stop triggers, done counts, double done, done-cancel note, single undo with state restore, new individual");
    }

    // 4. 書けないときの停止と再試行
    private static void WriteFailureChecks()
    {
        var ledger = new FakeLedger();
        var saved = new List<string>();
        var logs = new List<string>();
        long saveResult = 0;
        DateTime now = new(2026, 11, 9, 10, 0, 0);
        var s = new As100201LearningSession(ledger, path => { if (saveResult != 0) return saveResult; saved.Add(path); return 0; }, () => now = now.AddSeconds(1))
        { SaveDirectory = @"C:\fake" };
        s.Log += logs.Add;
        var mag = As100201PanelCommand.Parse("ACT:MAG");

        // 操作の行が書けない: NAK、止まる、行も件数も増えない。
        ledger.Fail = true;
        Check(s.HandleCommand(mag).StartsWith("NAK:台帳に書けません") && s.Blocked && s.OperationCount == 0 && ledger.Rows.Count == 0 && s.BlockReason!.Contains("fake ledger failure"), "operation write failure");
        // 止まっている間の操作・完了・取消は NAK(記録しない)。測定の行は順番を守って待たせる。
        Check(s.HandleCommand(mag).StartsWith("NAK:保存できるまで") && s.HandleCommand(As100201PanelCommand.Parse("DONE")).StartsWith("NAK:") &&
            s.HandleCommand(As100201PanelCommand.Parse("UNDO")).StartsWith("NAK:") && ledger.Rows.Count == 0, "commands while blocked");
        s.MeasurementStopped("PC", "PC", "0", As100201LearningSession.StopBySwitch);
        s.MeasurementCompleted("PC", "PC", "0");
        Check(s.PendingCount == 2 && saved.Count == 0 && ledger.Rows.Count == 0, "measurement queued while blocked");
        // 直せていない再試行は失敗のまま。
        Check(!s.Retry() && s.Blocked && s.PendingCount == 2, "retry without fix");
        // 直して再試行: 待っていた行と xdt を順に書いて再開。
        ledger.Fail = false;
        Check(s.Retry() && !s.Blocked && s.PendingCount == 0 && ledger.Rows.Count == 1 && ledger.Rows[0] is { Kind: As100201LedgerEventKind.Measurement, File: "20261109_100002_PC.xdt" } &&
            saved.SequenceEqual(new[] { @"C:\fake\20261109_100002_PC.xdt" }), "retry writes pending in order");
        Check(s.HandleCommand(mag) == "ACK:ACT:MAG" && ledger.Rows[^1].Note == "", "resumed after retry");

        // xdt が保存できない(LOGGER の戻り値が 0 以外): 行は書けていて、xdt だけ待つ。再試行で SaveXdt し直す。
        saveResult = -5;
        s.MeasurementStopped("CR", "CR", "140", As100201LearningSession.StopByPanel);
        Check(!s.Blocked && ledger.Rows[^1].Kind == As100201LedgerEventKind.Measurement, "row written before save");
        s.MeasurementCompleted("CR", "CR", "140");
        Check(s.Blocked && s.PendingCount == 1 && s.BlockReason!.Contains("戻り値 -5") && saved.Count == 1, "xdt save failure blocks");
        Check(s.HandleCommand(mag).StartsWith("NAK:"), "operation NAK while xdt pending");
        saveResult = 0;
        Check(s.Retry() && !s.Blocked && saved[^1].EndsWith("20261109_100004_CR.xdt") && saved.Count == 2, "retry re-saves xdt");
        // 測定の行の書き込みそのものが失敗: 行を覚えて止める。xdt も後ろに並ぶ。再試行で行 → xdt の順。
        ledger.Fail = true;
        s.MeasurementStopped("PC", "PC", "0", As100201LearningSession.StopBySwitch);
        Check(s.Blocked && s.PendingCount == 1, "measurement row failure queued");
        s.MeasurementCompleted("PC", "PC", "0");
        Check(s.PendingCount == 2, "xdt queued behind row");
        ledger.Fail = false;
        int rows = ledger.Rows.Count;
        Check(s.Retry() && ledger.Rows.Count == rows + 1 && saved.Count == 3 && saved[^1].EndsWith(ledger.Rows[^1].File), "row then xdt on retry");
        // 例外の種類: COM 例外も止める対象。引数の誤りは上へ出す。
        var throwing = new As100201LearningSession(ledger, _ => throw new System.Runtime.InteropServices.COMException("fake COM"), () => now)
        { SaveDirectory = @"C:\fake" };
        throwing.MeasurementCompleted("PC", "PC", "0");
        Check(throwing.Blocked && throwing.BlockReason!.Contains("fake COM"), "COM exception blocks");
        Check(logs.Any(l => l.Contains("再試行") && l.Contains("再開")), "resume log missing");
        Pass("write failures: operation NAK + block, measurement rows/xdt queued in order, retry fails until fixed, retry replays row then xdt, COM failures block");
    }

    private sealed class FakePanelLink : IAs100201SerialLink
    {
        public event Action<string>? LineReceived;
        public event Action? Disconnected;
        public bool IsConnected { get; private set; }
        public string HelloReply = "AS100201_PANEL_READY:1";
        public List<string> Sent { get; } = new();
        public List<string> Requests { get; } = new();
        public int Disconnects;
        public Task VerifyAsync(string request, Func<string, bool> isReply, Action<string>? timingLog = null)
        {
            Requests.Add(request);
            IsConnected = true;
            if (!isReply(HelloReply)) throw new TimeoutException("fake: no reply");
            return Task.CompletedTask;
        }
        public Task DisconnectAsync() { IsConnected = false; Disconnects++; return Task.CompletedTask; }
        public void SendLine(string line) => Sent.Add(line);
        public void Receive(string line) => LineReceived?.Invoke(line);
        public void Drop() { IsConnected = false; Disconnected?.Invoke(); }
        public void Dispose() { }
    }

    // 5. パネル途切れ中の受け付け(定期応答の見張りと、スイッチの受け付けの決まり)
    private static async Task PanelOutageChecks()
    {
        var link = new FakePanelLink();
        using var panel = new As100201PanelProtocol(link);
        var received = new List<As100201PanelCommand>();
        var statuses = new List<string>();
        int disconnected = 0;
        panel.CommandReceived += received.Add;
        panel.StatusChanged += statuses.Add;
        panel.Disconnected += () => disconnected++;

        await panel.VerifyAsync();
        Check(link.Requests.SequenceEqual(new[] { "AS100201_PANEL_PING" }) && panel.IsConnected && !panel.InputReady, "verified but not ready before frames");
        // 定期応答が安定する前の操作は捨てて NAK。
        link.Receive("ACT:MAG");
        Check(received.Count == 0 && link.Sent.Count == 1 && link.Sent[0].StartsWith("NAK:"), "command before warm-up not dropped with NAK");
        uint seq = 1;
        for (int i = 0; i < 6; i++) { link.Receive($"DIAG_FRAME:{seq}:{seq * 250}"); seq++; await Task.Delay(250); }
        Check(panel.InputReady, "not ready after 1 s of frames");
        long version = panel.RequestVersion;
        link.Receive("ACT:NOZZLE:R:OUT:3");
        Check(received.Count == 1 && received[0].Line == "ACT:NOZZLE:R:OUT:3" && link.Sent.Count == 1, "command while ready not delivered");
        // 定期応答が 1 秒止まる: 操作禁止、見張りが知らせる、まだ再接続はしない。
        await Task.Delay(1100);
        Check(!panel.InputReady && panel.IsConnected, "stale frames still ready");
        await panel.MaintainAsync();
        Check(statuses.Count == 1 && statuses[0].Contains("定期応答が停止") && link.Disconnects == 0 && panel.RequestVersion != version, "stale not reported or reconnected too early");
        link.Receive("ACT:MAG");
        Check(received.Count == 1 && link.Sent[^1].StartsWith("NAK:"), "command during stale delivered");
        // 受信が戻ったら 1 秒の安定確認のあとに受け付ける。
        for (int i = 0; i < 6; i++) { link.Receive($"DIAG_FRAME:{seq}:{seq * 250}"); seq++; await Task.Delay(250); }
        Check(panel.InputReady, "not ready after recovery");
        // 切断: 見張りが 2 秒おきに再接続する。
        link.Drop();
        Check(disconnected == 1 && !panel.IsConnected && !panel.InputReady, "disconnect not propagated");
        await panel.MaintainAsync();
        Check(link.Requests.Count == 2 && panel.IsConnected && !panel.InputReady && statuses[^1].Contains("再接続"), "reconnect not attempted");
        // 版違いの応答は接続エラー(つなぎ直して版を見る)。
        link.Drop();
        link.HelloReply = "AS100201_PANEL_READY:9";
        await Task.Delay(2100);
        await panel.MaintainAsync();
        Check(!panel.IsConnected && statuses[^1].Contains("版 9"), "wrong version accepted");
        // 起動通知で古い状態を捨てる。
        link.HelloReply = "AS100201_PANEL_READY:1";
        await Task.Delay(2100);
        await panel.MaintainAsync();
        for (int i = 0; i < 6; i++) { link.Receive($"DIAG_FRAME:{seq}:{seq * 250}"); seq++; await Task.Delay(250); }
        Check(panel.InputReady, "not ready after version fix");
        link.Receive("DIAG_BOOT:1");
        Check(!panel.InputReady, "boot did not reset");

        // スイッチの受け付けの決まり: sw4 だけは途切れ中・保存の停止中でも受け付ける。
        string refusal;
        Check(As100201LearningGate.Allows("sw4", baseReady: true, panelReady: false, hasSession: true, saveBlocked: true, readyLedOn: false, out _), "sw4 refused during outage");
        Check(!As100201LearningGate.Allows("sw4", baseReady: false, panelReady: true, hasSession: true, saveBlocked: false, readyLedOn: true, out refusal) && refusal == As100201LearningGate.NotReady, "sw4 without base");
        foreach (string button in new[] { "PC_go", "CR_go", "F8" })
        {
            Check(!As100201LearningGate.Allows(button, true, panelReady: false, true, false, true, out refusal) && refusal == As100201LearningGate.PanelLost, button + " allowed during outage");
            Check(!As100201LearningGate.Allows(button, true, true, hasSession: false, false, true, out refusal) && refusal == As100201LearningGate.NoSaveDirectory, button + " allowed without save directory");
            Check(!As100201LearningGate.Allows(button, true, true, true, saveBlocked: true, true, out refusal) && refusal == As100201LearningGate.SaveBlocked, button + " allowed while blocked");
            Check(!As100201LearningGate.Allows(button, true, true, true, false, readyLedOn: false, out _), button + " allowed with LED off");
            Check(As100201LearningGate.Allows(button, true, true, true, false, true, out _), button + " refused when all ready");
        }
        Pass("panel outage: warm-up 1 s, stale after 1 s -> NAK + status, recovery 1 s, reconnect after drop, version check, DIAG_BOOT reset; gate lets only sw4 through");
    }

    // 6. 通常モードを選んだとき今までと同じ
    private static void NormalModeChecks()
    {
        Check(default(As100201StartupMode) == As100201StartupMode.Normal, "default mode is not Normal");
        using (var dialog = new StartupModeDialog())
            Check(dialog.SelectedMode == As100201StartupMode.Normal, "dialog initial selection is not Normal");
        Check(As100201Application.GetWindowTitle(As100201StartupMode.Normal) == As100201Application.WindowTitle &&
            As100201Application.WindowTitle == "AS100-201 スイッチ制御" &&
            As100201Application.GetWindowTitle(As100201StartupMode.Learning).Contains("学習モード"), "window titles");
        using (var normal = new MainForm(As100201StartupMode.Normal))
        {
            Check(!normal.IsLearningMode && normal.Text == As100201Application.WindowTitle, "normal form marked as learning");
            Check(normal.Controls.Count == 1, "normal form gained learning controls");
            Check(FindLabels(normal).All(l => !l.Text.Contains("学習モード") && !l.Text.Contains("タッチパネル")), "learning text shown in normal mode");
        }
        using (var learning = new MainForm(As100201StartupMode.Learning))
        {
            Check(learning.IsLearningMode && learning.Text == As100201Application.LearningWindowTitle, "learning form title");
            Check(learning.Controls.Count == 4 && FindLabels(learning).Any(l => l.Text.StartsWith("学習モード")) && FindLabels(learning).Any(l => l.Text == "タッチパネル："), "learning controls missing");
        }
        // 異常ログ: 通常モードの異常種別はそのまま。パネルの種別は学習モードだけで起きる。
        foreach (var kind in new[] { "HEARTBEAT_STALE", "FRAME_GAP", "PROTOCOL_RECOVERY", "PANEL_HEARTBEAT_STALE", "PANEL_FRAME_GAP", "PANEL_RECOVERY" })
            Check(PSCAutomation.Wrappers.Arduino.ArduinoDiagnosticLog.IsIncident(kind), "incident kind missing: " + kind);
        Pass("normal mode: default/× -> Normal, same title, no learning controls or text; learning mode adds banner, panel row and title");
    }

    private static IEnumerable<Label> FindLabels(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child is Label label) yield return label;
            foreach (var nested in FindLabels(child)) yield return nested;
        }
    }
}
