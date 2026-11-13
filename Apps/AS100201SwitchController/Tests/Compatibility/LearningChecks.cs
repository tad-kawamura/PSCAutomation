// 学習モード(SPEC_調整操作の記録.md、2026-11-09 承認)の模擬テスト。機器・LOGGER・タッチパネルは使わない。
// グループ: パネルの行の読み取り / 台帳の行の組み立て / 完了・取消・完了の取消 / 書けないときの停止と再試行 /
//           パネル途切れ中の受け付け / USB と Wi-Fi の 2 本・電池残量(2026-11-13) / 通常モードを選んだとき今までと同じ / タッチパネルの代わり(開発用)
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
        PressedRowChecks();
        await PanelOutageChecks();
        await PanelPathAndBatteryChecks();
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

        // 並べる行は全部パネルの行として読め、重複がない(着磁 1・減磁 2・ノズル 4・スペーサー 4・完了・取消・HOLD)。
        var lines = As100201VirtualPanelLink.AllCommands.Select(c => c.Line).ToList();
        Check(lines.Count == 14 && lines.Distinct().Count() == 14 && lines.Contains("ACT:NOZZLE:R:OUT") && lines.Contains("ACT:SPACER:P:DOWN:0.450:NA") &&
            lines[^3] == "DONE" && lines[^2] == "UNDO" && lines[^1] == "HOLD", "virtual panel command list");
        Check(!link.Press("ACT:MAG") && received.Count == 0, "pressed before connection");

        await panel.VerifyAsync();
        Check(panel.IsConnected && !panel.InputReady, "virtual panel verified but ready before frames");
        // 定期応答はアプリの中で 250ms ごとに出る。1 秒の安定確認のあとに受け付ける。
        await Task.Delay(1600);
        Check(panel.InputReady, "virtual panel frames did not make input ready");
        Check(link.Press("ACT:NOZZLE:P:IN") && received.Count == 1 && received[0].DisplayName == "ノズル P 側（近づける）", "virtual press not delivered");
        Check(panel.TrySend(As100201PanelProtocol.Ack(received[0])) && sent.SequenceEqual(new[] { "ACK:ACT:NOZZLE:P:IN" }), "reply to virtual panel not shown");

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
        Pass("virtual panel (--virtual-panel, dev only): 14 panel lines, same protocol path, 250 ms frames -> ready, ACK/NAK shown, pause -> stale + NAK, unplug/replug reconnect, only with the switch in learning mode");
    }

    // 1. パネルの行の読み取り
    private static void PanelCommandChecks()
    {
        Check(As100201PanelCommand.TryParse("ACT:MAG", out var c) && c.Kind == As100201PanelCommandKind.Operation &&
            c.Operation == As100201Operation.Magnetize && c.Direction == "" && c.Degree == "" && c.OperationText == "着磁" && c.DisplayName == "着磁", "MAG");
        Check(As100201PanelCommand.TryParse("ACT:DEMAG:FULL", out c) && c.Operation == As100201Operation.Demagnetize && c.Degree == "しっかり" && c.DisplayName == "減磁（しっかり）", "DEMAG FULL");
        Check(As100201PanelCommand.TryParse("ACT:DEMAG:TAP", out c) && c.Degree == "ちょんちょん", "DEMAG TAP");
        // 版 3(2026-11-13): ノズルは方向だけ(IN=近づける、OUT=離す)。
        Check(As100201PanelCommand.TryParse("ACT:NOZZLE:P:IN", out c) && c.Operation == As100201Operation.NozzleP && c.Direction == "近づける" &&
            c.Degree == "" && c.Thickness == "" && c.OperationText == "ノズル P" && c.DisplayName == "ノズル P 側（近づける）", "NOZZLE P IN");
        Check(As100201PanelCommand.TryParse("ACT:NOZZLE:R:OUT", out c) && c.Operation == As100201Operation.NozzleR && c.Direction == "離す" && c.Degree == "", "NOZZLE R OUT");
        // スペーサーは向き・厚さ(箱の表示)・表示との差(μm。NA は測っていない)。差は「程度」の列の言葉にする。
        Check(As100201PanelCommand.TryParse("ACT:SPACER:P:UP:0.450:+2", out c) && c.Operation == As100201Operation.SpacerP && c.Direction == "上げる" &&
            c.Thickness == "0.450" && c.Degree == "差+2μm" && c.DisplayName == "スペーサー P 側（上げる・0.450・差+2μm）", "SPACER P UP +2");
        Check(As100201PanelCommand.TryParse("ACT:SPACER:R:DOWN:0.271:-3", out c) && c.Operation == As100201Operation.SpacerR && c.Direction == "下げる" && c.Degree == "差-3μm", "SPACER R DOWN -3");
        Check(As100201PanelCommand.TryParse("ACT:SPACER:R:DOWN:0.593:0", out c) && c.Degree == "差0μm", "SPACER diff 0");
        Check(As100201PanelCommand.TryParse("ACT:SPACER:P:UP:0.400:NA", out c) && c.Degree == "差未測定" && c.Thickness == "0.400", "SPACER not measured");
        Check(As100201PanelCommand.TryParse("ACT:SPACER:P:UP:0.400:+50", out _) && !As100201PanelCommand.TryParse("ACT:SPACER:P:UP:0.400:+51", out _), "SPACER diff limit");
        Check(As100201PanelCommand.TryParse("DONE", out c) && c.Kind == As100201PanelCommandKind.Done && c.Operation is null && c.DisplayName == "完了", "DONE");
        Check(As100201PanelCommand.TryParse("UNDO", out c) && c.Kind == As100201PanelCommandKind.Undo && c.DisplayName == "取消", "UNDO");
        Check(As100201PanelCommand.TryParse("HOLD", out c) && c.Kind == As100201PanelCommandKind.Hold && c.Operation is null && c.DisplayName == "パネルに触れた", "HOLD");
        // 版 2 までの行(ノズルの程度・厚さのないスペーサー)は受け付けない。
        foreach (string bad in new[] { "ACT", "ACT:", "ACT:MAG:1", "ACT:DEMAG", "ACT:DEMAG:HALF", "ACT:NOZZLE:P:IN:2", "ACT:NOZZLE:P", "ACT:NOZZLE:X:IN",
            "ACT:NOZZLE:P:UP", "ACT:SPACER:P", "ACT:SPACER:P:UP", "ACT:SPACER:R:DOWN", "ACT:SPACER:P:IN:0.450:0", "ACT:SPACER:P:UP:0.45:0", "ACT:SPACER:P:UP:.4500:0",
            "ACT:SPACER:P:UP:0.450", "ACT:SPACER:P:UP:0.450:2", "ACT:SPACER:P:UP:0.450:+0", "ACT:SPACER:P:UP:0.450:na", "ACT:SPACER:P:UP:0.450:+2:1",
            "act:mag", "DONE ", "UNDO:1", "HOLD:1", "PC_go", "DIAG_FRAME:1:250", "AS100201_PANEL_READY:1", "" })
            Check(!As100201PanelCommand.TryParse(bad, out _), "accepted bad panel line: " + bad);
        Check(As100201PanelProtocol.Ack(As100201PanelCommand.Parse("ACT:NOZZLE:P:IN")) == "ACK:ACT:NOZZLE:P:IN", "ACK format");
        Check(As100201PanelProtocol.Nak("理由") == "NAK:理由", "NAK format");
        // 代わりのパネル: スペーサーだけ厚さと差を差し替える。
        Check(As100201VirtualPanelLink.WithSpacer(As100201PanelCommand.Parse("ACT:SPACER:R:UP:0.450:NA"), "0.510", "-1").Line == "ACT:SPACER:R:UP:0.510:-1" &&
            As100201VirtualPanelLink.WithSpacer(As100201PanelCommand.Parse("ACT:MAG"), "0.510", "-1").Line == "ACT:MAG", "virtual spacer");
        Check(As100201SpacerCatalog.Thicknesses.Count == 49 && As100201SpacerCatalog.Thicknesses.Distinct().Count() == 49 &&
            As100201SpacerCatalog.Thicknesses.All(t => As100201PanelCommand.TryParse($"ACT:SPACER:P:UP:{t}:NA", out _)) &&
            As100201SpacerCatalog.Differences.All(d => As100201PanelCommand.TryParse($"ACT:SPACER:P:UP:0.450:{d}", out _)), "spacer catalog");
        // 版 3(2026-11-13、ノズルは方向だけ・スペーサーの厚さ・HOLD)。版 2 までのスケッチは書き込み直しを求める。
        Check(As100201PanelProtocol.CheckHello("AS100201_PANEL_READY:3") is null, "panel version 3 rejected");
        Check(As100201PanelProtocol.CheckHello("AS100201_PANEL_READY:2")?.Contains("版 2") == true, "panel version 2 accepted");
        Check(As100201PanelProtocol.CheckHello("AS100201_PANEL_READY:1")?.Contains("版 1") == true, "panel version 1 accepted");
        Check(As100201PanelProtocol.CheckHello("AS100201_READY:2") is not null && As100201PanelProtocol.CheckHello(null)?.Contains("不明") == true, "Arduino reply accepted as panel");
        Check(As100201PanelProtocol.HelloRequest != "AS100201_PING" && !As100201PanelProtocol.HelloPrefix.StartsWith(As100201ArduinoProtocol.HelloPrefix) &&
            !As100201ArduinoProtocol.HelloPrefix.StartsWith(As100201PanelProtocol.HelloPrefix), "panel and Arduino hello strings collide");
        Pass("panel lines (v3): ACT:MAG / DEMAG / NOZZLE direction / SPACER thickness+diff / DONE / UNDO / HOLD parsed with ledger words; v2 and malformed lines rejected; ACK/NAK; panel version 3 only");
    }

    // 2. 台帳の行の組み立て(CSV の形)
    private static void LedgerRowChecks()
    {
        var time = new DateTime(2026, 11, 9, 13, 5, 7);
        Check(As100201LedgerRow.HeaderLine == "日時,種類,側,位相,停止のきっかけ,操作,方向,程度,厚さ,ファイル,備考", "header columns");
        var measurement = new As100201LedgerRow(time, As100201LedgerEventKind.Measurement) { Side = "PC（2回押し）", Phase = "75", StopTrigger = "sw4", File = "20261109_130507_PC2.xdt" };
        Check(measurement.ToCsvLine() == "2026-11-09 13:05:07,測定,PC（2回押し）,75,sw4,,,,,20261109_130507_PC2.xdt,", "measurement row");
        var operation = new As100201LedgerRow(time, As100201LedgerEventKind.Operation) { Operation = "ノズル P", Direction = "近づける", Degree = "", Note = "完了の取消、直前の測定なし" };
        Check(operation.ToCsvLine() == "2026-11-09 13:05:07,操作,,,,ノズル P,近づける,,,,完了の取消、直前の測定なし", "operation row");
        Check(new As100201LedgerRow(time, As100201LedgerEventKind.Done).KindText == "完了" && new As100201LedgerRow(time, As100201LedgerEventKind.Undo).KindText == "取消", "kind text");
        // 保存先は exe の隣の「学習データ」(2026-11-10。雛形のフォルダーではない)。台帳の「ファイル」列は日付フォルダー付き。
        Check(MainForm.LearningDataDirectory(@"C:\apps\AS100201") == @"C:\apps\AS100201\学習データ" && MainForm.LedgerFileName == "調整記録.csv" &&
            MainForm.TemplateFolderName == "雛形", "save directory is not next to the exe");
        Check(As100201LearningSession.LedgerFileText(@"C:\apps\学習データ\20261110\20261110_133028_PC.xdt") == @"20261110\20261110_133028_PC.xdt", "ledger file column without date folder");
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

            // 記録の状態は、保存先フォルダーが無ければ作ってから台帳に書く(2026-11-10。exe の隣の「学習データ」を最初の 1 行で作る)。
            string data = Path.Combine(root, "学習データ");
            var real = new As100201LearningSession(new As100201CsvLedger(Path.Combine(data, "調整記録.csv")), _ => 0) { SaveDirectory = data };
            Check(real.HandleCommand(As100201PanelCommand.Parse("ACT:MAG")) == "ACK:ACT:MAG" && File.Exists(Path.Combine(data, "調整記録.csv")) && !real.Blocked, "save folder not created on first row");

            // 自動で読み込む雛形: 「雛形」フォルダーの xdt がちょうど 1 つのときだけ。
            string templates = Path.Combine(root, "雛形");
            Check(MainForm.FindLearningTemplate(templates, out string reason) is null && reason.Contains("雛形フォルダーがありません"), "missing template folder");
            Directory.CreateDirectory(templates);
            File.WriteAllText(Path.Combine(templates, "メモ.txt"), "");
            Check(MainForm.FindLearningTemplate(templates, out reason) is null && reason.Contains("xdt がありません"), "no template");
            string first = Path.Combine(templates, "雛形_AS100_201流量調整用_650kPa_0_02Hz_10Vpp.xdt");
            File.WriteAllText(first, "");
            Check(MainForm.FindLearningTemplate(templates, out reason) == first && reason == "", "single template not found");
            File.WriteAllText(Path.Combine(templates, "別の雛形.xdt"), "");
            Check(MainForm.FindLearningTemplate(templates, out reason) is null && reason.Contains("2 つ"), "two templates accepted");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        Pass("ledger rows: 11 columns, RFC4180 quoting, CSV file with BOM + header once, append-only, locked/missing folder -> exception; save folder next to exe, created on first row; template folder needs exactly one xdt");
    }

    private sealed class FakeLedger : IAs100201LearningLedger
    {
        public List<As100201LedgerRow> Rows { get; } = new();
        public bool Fail;
        public string Location => @"C:\fake\調整記録.csv";
        public void Append(As100201LedgerRow row) { if (Fail) throw new IOException("fake ledger failure"); Rows.Add(row); }
        public void EnsureWritable() { if (Fail) throw new IOException("fake ledger failure"); }
    }

    private static (As100201LearningSession Session, FakeLedger Ledger, List<string> Saved, List<string> Logs, Func<long> SaveResult) NewSession(DateTime start, List<string>? created = null)
    {
        var ledger = new FakeLedger();
        var saved = new List<string>();
        var logs = new List<string>();
        long saveResult = 0;
        DateTime now = start;
        var session = new As100201LearningSession(ledger, path => { if (saveResult != 0) return saveResult; saved.Add(path); return 0; },
            () => now = now.AddSeconds(1), path => saved.Contains(path), path => created?.Add(path))
        { SaveDirectory = @"C:\fake" };
        session.Log += logs.Add;
        return (session, ledger, saved, logs, () => saveResult);
    }

    // 3. 完了・取消・完了の取消(1 個体の流れ)
    private static void SessionFlowChecks()
    {
        var created = new List<string>();
        var (s, ledger, saved, logs, _) = NewSession(new DateTime(2026, 11, 9, 9, 0, 0), created);
        var mag = As100201PanelCommand.Parse("ACT:MAG");
        var nozzle = As100201PanelCommand.Parse("ACT:NOZZLE:P:IN");
        var done = As100201PanelCommand.Parse("DONE");
        var undo = As100201PanelCommand.Parse("UNDO");

        // 起動直後に操作: 「直前の測定なし」。測定: sw4 で止めて行、停止に戻ってから xdt。
        Check(s.HandleCommand(mag) == "ACK:ACT:MAG" && ledger.Rows[^1].Kind == As100201LedgerEventKind.Operation && ledger.Rows[^1].Note == "直前の測定なし", "first operation without measurement");
        s.MeasurementStopped("PC", "PC", "0", As100201LearningSession.StopBySwitch);
        Check(ledger.Rows[^1] is { Kind: As100201LedgerEventKind.Measurement, Side: "PC", Phase: "0", StopTrigger: "sw4", File: @"20261109\20261109_090002_PC.xdt", Note: "" } && s.HasArmedWaveform && saved.Count == 0,
            "measurement row at stop, xdt not yet saved");
        s.MeasurementCompleted("PC", "PC", "0");
        Check(!s.HasArmedWaveform && saved.SequenceEqual(new[] { @"C:\fake\20261109\20261109_090002_PC.xdt" }) && ledger.Rows.Count == 2, "xdt saved at completion, no extra row");
        // 台帳の前に保存先を、xdt の前に日付フォルダーを作る(2026-11-10)。
        Check(created.Contains(@"C:\fake") && created[^1] == @"C:\fake\20261109", "save folder / date folder not created before writing");
        // 操作 → 測定(操作ボタンで止めた) → 操作 → 測定(LOGGER が自動で止まった: 行と xdt を一緒に)
        Check(s.HandleCommand(nozzle) == "ACK:ACT:NOZZLE:P:IN" && ledger.Rows[^1] is { Operation: "ノズル P", Direction: "近づける", Degree: "", Thickness: "", Note: "" }, "operation after measurement");
        s.MeasurementStopped("CR（2回押し）", "CR2", "250", As100201LearningSession.StopByPanel);
        s.MeasurementCompleted("CR（2回押し）", "CR2", "250");
        Check(ledger.Rows[^1] is { Kind: As100201LedgerEventKind.Measurement, Side: "CR（2回押し）", Phase: "250", StopTrigger: "操作ボタン", File: @"20261109\20261109_090004_CR2.xdt" }, "measurement stopped by panel");
        Check(s.HandleCommand(As100201PanelCommand.Parse("ACT:DEMAG:TAP")) == "ACK:ACT:DEMAG:TAP", "demag");
        s.MeasurementCompleted("CR", "CR", "140");
        Check(ledger.Rows[^1] is { Kind: As100201LedgerEventKind.Measurement, StopTrigger: "LOGGER の停止", File: @"20261109\20261109_090006_CR.xdt" } && saved[^1].EndsWith("20261109_090006_CR.xdt"), "auto-stopped measurement: row and xdt together");
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
        Check(s.HandleCommand(nozzle) == "ACK:ACT:NOZZLE:P:IN" && ledger.Rows[^1].Note == "直前の測定なし", "state restored after undo");
        // 測定の次の操作は取り消せるが、測定を挟むと取り消せない。
        s.MeasurementStopped("PC", "PC", "0", As100201LearningSession.StopBySwitch);
        s.MeasurementCompleted("PC", "PC", "0");
        Check(s.HandleCommand(undo).StartsWith("NAK:"), "undo across measurement");
        Check(s.HandleCommand(As100201PanelCommand.Parse("ACT:SPACER:R:DOWN:0.450:+2")) == "ACK:ACT:SPACER:R:DOWN:0.450:+2" && ledger.Rows[^1] is { Operation: "スペーサー R", Direction: "下げる", Thickness: "0.450", Degree: "差+2μm", Note: "" }, "spacer after measurement");
        Check(s.HandleCommand(As100201PanelCommand.Parse("HOLD")) == "ACK:HOLD" && ledger.Rows[^1].Operation == "スペーサー R", "hold wrote a ledger row");
        Check(s.HandleCommand(undo) == "ACK:UNDO" && ledger.Rows[^1].Note.EndsWith(" スペーサー R（下げる・0.450・差+2μm）"), "undo spacer");
        // 完了 → 測定: 新しい個体(件数を 0 から)。
        Check(s.HandleCommand(done) == "ACK:DONE", "done 2");
        s.MeasurementStopped("", "", "", As100201LearningSession.StopBySwitch);
        Check(ledger.Rows[^1] is { Side: "", Phase: "", Note: "位相の出し直しなし", File: @"20261109\20261109_090015.xdt" } && s.MeasurementCount == 1 && s.OperationCount == 0 && !s.Completed, "new individual after done");
        // 同じ秒に 2 つ目の xdt は _2。日付フォルダーは止めた時刻の日付(日をまたいだら次の日のフォルダー)。
        s.MeasurementCompleted("", "", "");
        Check(s.BuildXdtPath(new DateTime(2026, 11, 9, 9, 0, 15), "") == @"C:\fake\20261109\20261109_090015_2.xdt", "duplicate name suffix");
        Check(s.BuildXdtPath(new DateTime(2026, 11, 10, 0, 0, 1), "PC") == @"C:\fake\20261110\20261110_000001_PC.xdt", "date folder after midnight");
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
        bool folderFails = false;
        var s = new As100201LearningSession(ledger, path => { if (saveResult != 0) return saveResult; saved.Add(path); return 0; }, () => now = now.AddSeconds(1),
            createDirectory: _ => { if (folderFails) throw new UnauthorizedAccessException("fake folder failure"); })
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
        Check(s.Retry() && !s.Blocked && s.PendingCount == 0 && ledger.Rows.Count == 1 && ledger.Rows[0] is { Kind: As100201LedgerEventKind.Measurement, File: @"20261109\20261109_100002_PC.xdt" } &&
            saved.SequenceEqual(new[] { @"C:\fake\20261109\20261109_100002_PC.xdt" }), "retry writes pending in order");
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
        // 保存先・日付フォルダーを作れない(2026-11-10): 台帳の行も xdt も書かずに止め、作れるようになったら再試行で書く。
        folderFails = true;
        rows = ledger.Rows.Count;
        Check(s.HandleCommand(mag).StartsWith("NAK:台帳に書けません") && s.Blocked && s.BlockReason!.Contains("fake folder failure") && ledger.Rows.Count == rows, "folder failure did not block");
        s.MeasurementStopped("PC", "PC", "0", As100201LearningSession.StopBySwitch);
        s.MeasurementCompleted("PC", "PC", "0");
        Check(!s.Retry() && s.PendingCount == 2 && saved.Count == 3, "retry succeeded while folder cannot be created");
        folderFails = false;
        Check(s.Retry() && !s.Blocked && ledger.Rows.Count == rows + 1 && saved.Count == 4, "retry after folder fixed");
        // 例外の種類: COM 例外も止める対象。引数の誤りは上へ出す。
        var throwing = new As100201LearningSession(ledger, _ => throw new System.Runtime.InteropServices.COMException("fake COM"), () => now, createDirectory: _ => { })
        { SaveDirectory = @"C:\fake" };
        throwing.MeasurementCompleted("PC", "PC", "0");
        Check(throwing.Blocked && throwing.BlockReason!.Contains("fake COM"), "COM exception blocks");
        Check(logs.Any(l => l.Contains("再試行") && l.Contains("再開")), "resume log missing");
        Pass("write failures: operation NAK + block, measurement rows/xdt queued in order, retry fails until fixed, retry replays row then xdt, folder creation failure blocks, COM failures block");
    }

    // 8. 押した行(PC_go / CR_go で WF1973 を出し直した瞬間、2026-11-12。SPEC_測定中の取り込み.md「押した瞬間の記録」)
    private static void PressedRowChecks()
    {
        var (s, ledger, saved, logs, _) = NewSession(new DateTime(2026, 11, 12, 9, 0, 0));
        var mag = As100201PanelCommand.Parse("ACT:MAG");
        var undo = As100201PanelCommand.Parse("UNDO");

        // 測定していないとき(F8 の前): 側・位相と時刻(ミリ秒まで)。点数と F8 からの秒は無い。
        s.WaveformRestarted("PC", "0", null, null);
        Check(ledger.Rows[^1] is { Kind: As100201LedgerEventKind.Pressed, KindText: "押した", Side: "PC", Phase: "0", StopTrigger: "", File: "", Note: "09:00:01.000、測定していないとき" },
            "pressed row before F8: " + ledger.Rows[^1].Note);
        // 測定中: F8 からの秒(ミリ秒まで)と、点数(xdt の何点目から後か)。2 回押しの側は測定の行と同じ書き方。
        s.WaveformRestarted("CR（2回押し）", "250", 12.3456, 1234);
        Check(ledger.Rows[^1] is { Kind: As100201LedgerEventKind.Pressed, Side: "CR（2回押し）", Phase: "250" } &&
            ledger.Rows[^1].Note == "09:00:02.000、F8 から 12.346 秒、点数 1234（xdt の 1235 点目から後）", "pressed row while measuring: " + ledger.Rows[^1].Note);
        Check(ledger.Rows[^1].ToCsvLine() == "2026-11-12 09:00:02,押した,CR（2回押し）,250,,,,,,,09:00:02.000、F8 から 12.346 秒、点数 1234（xdt の 1235 点目から後）",
            "pressed row CSV: " + ledger.Rows[^1].ToCsvLine());
        // 点数を読めなかった: 理由を残す。
        s.WaveformRestarted("PC", "0", 3.5, null, "fake COM");
        Check(ledger.Rows[^1].Note == "09:00:03.000、F8 から 3.500 秒、点数を読めず：fake COM", "count failure note: " + ledger.Rows[^1].Note);
        Check(s.MeasurementCount == 0 && s.OperationCount == 0 && logs[^1].Contains("押した PC（位相 0°）"), "pressed row changed counts or no log");

        // 押した行は「直前の測定なし」・取消の対象・個体の数え方を変えない。
        s.MeasurementStopped("PC", "PC", "0", As100201LearningSession.StopBySwitch);
        s.MeasurementCompleted("PC", "PC", "0");
        s.WaveformRestarted("CR", "140", null, null);
        Check(s.HandleCommand(mag) == "ACK:ACT:MAG" && ledger.Rows[^1].Note == "", "press between measurement and operation added 直前の測定なし");
        s.WaveformRestarted("PC", "0", null, null);
        Check(s.HandleCommand(undo) == "ACK:UNDO" && ledger.Rows[^1].Note.EndsWith(" 着磁"), "press blocked undo of the operation");
        Check(s.MeasurementCount == 1 && s.OperationCount == 0 && saved.Count == 1, "counts after press/undo");

        // 書けない: 測定の行と同じく覚えて止め、再試行で書く(順番を守る)。
        ledger.Fail = true;
        int rows = ledger.Rows.Count;
        s.WaveformRestarted("CR", "140", 1.0, 100);
        Check(s.Blocked && s.PendingCount == 1 && ledger.Rows.Count == rows, "pressed row failure did not block");
        s.MeasurementStopped("CR", "CR", "140", As100201LearningSession.StopBySwitch);
        Check(s.PendingCount == 2, "measurement row not queued behind pressed row");
        ledger.Fail = false;
        Check(s.Retry() && ledger.Rows.Count == rows + 2 && ledger.Rows[rows].Kind == As100201LedgerEventKind.Pressed &&
            ledger.Rows[rows + 1].Kind == As100201LedgerEventKind.Measurement, "retry did not write pressed row then measurement row");
        Pass("pressed rows: kind 押した, side/phase, ms time + F8 seconds + point count in note, count failure reason, no effect on counts/undo/直前の測定なし, queued and retried in order");
    }

    private sealed class FakePanelLink : IAs100201SerialLink
    {
        public event Action<string>? LineReceived;
        public event Action? Disconnected;
        public bool IsConnected { get; private set; }
        public string HelloReply = "AS100201_PANEL_READY:3";
        public bool Fail;
        public List<string> Sent { get; } = new();
        public List<string> Requests { get; } = new();
        public int Disconnects;
        public Task VerifyAsync(string request, Func<string, bool> isReply, Action<string>? timingLog = null)
        {
            Requests.Add(request);
            // Fail: その道にパネルが無い(USB なら COM ポートに見つからない、Wi-Fi ならつながらない)。
            if (Fail) throw new InvalidOperationException("fake: not found");
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
        link.Receive("ACT:NOZZLE:R:OUT");
        Check(received.Count == 1 && received[0].Line == "ACT:NOZZLE:R:OUT" && link.Sent.Count == 1, "command while ready not delivered");
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
        link.HelloReply = "AS100201_PANEL_READY:3";
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

    // 8. タッチパネルの USB と Wi-Fi の 2 本(2026-11-13 承認)と電池残量
    private static async Task PanelPathAndBatteryChecks()
    {
        const string ping = As100201PanelProtocol.HelloRequest;
        static bool IsHello(string line) => line.StartsWith(As100201PanelProtocol.HelloPrefix, StringComparison.Ordinal);
        var usb = new FakePanelLink();
        var wifi = new FakePanelLink();
        using (var paths = new As100201PanelPathLink(usb, wifi))
        {
            var changes = new List<As100201PanelPath>();
            var lines = new List<string>();
            int disconnected = 0;
            paths.PathChanged += changes.Add;
            paths.LineReceived += lines.Add;
            paths.Disconnected += () => disconnected++;

            // 両方あれば USB。Wi-Fi は確かめない
            await paths.VerifyAsync(ping, IsHello);
            Check(paths.ActivePath == As100201PanelPath.Usb && paths.IsConnected && usb.Requests.Count == 1 && wifi.Requests.Count == 0 &&
                changes.SequenceEqual(new[] { As100201PanelPath.Usb }), "USB not preferred");
            // 今の道でない側の行・切断は捨て、送るのは今の道だけ
            wifi.Receive("ACT:MAG");
            usb.Receive("DIAG_FRAME:1:250");
            wifi.Drop();
            paths.SendLine("ACK:ACT:MAG");
            Check(lines.SequenceEqual(new[] { "DIAG_FRAME:1:250" }) && disconnected == 0 &&
                usb.Sent.SequenceEqual(new[] { "ACK:ACT:MAG" }) && wifi.Sent.Count == 0, "inactive path not ignored");
            // USB が切れた: 切断を知らせる。選び直すと Wi-Fi へ移る
            usb.Fail = true;
            usb.Drop();
            Check(disconnected == 1 && paths.ActivePath == As100201PanelPath.None && !paths.IsConnected, "USB drop not propagated");
            bool refused = false;
            try { paths.SendLine("ACK:x"); } catch (InvalidOperationException) { refused = true; }
            Check(refused, "send without path");
            await paths.VerifyAsync(ping, IsHello);
            Check(paths.ActivePath == As100201PanelPath.Wifi && changes.SequenceEqual(new[] { As100201PanelPath.Usb, As100201PanelPath.Wifi }), "no failover to Wi-Fi");
            // Wi-Fi で使っている間に USB が戻っても、選び直すまで移らない
            usb.Fail = false;
            Check(paths.ActivePath == As100201PanelPath.Wifi, "moved to USB without re-verify");
            // 選び直す(「確認」)と USB に戻り、Wi-Fi は閉じる
            await paths.VerifyAsync(ping, IsHello);
            Check(paths.ActivePath == As100201PanelPath.Usb && wifi.Disconnects == 1 && !wifi.IsConnected && changes[^1] == As100201PanelPath.Usb, "not back to USB on re-verify");
            // 同じ道でつながり直したときは道の変化を知らせない
            int changeCount = changes.Count;
            await paths.DisconnectAsync();
            await paths.VerifyAsync(ping, IsHello);
            Check(changes.Count == changeCount && paths.ActivePath == As100201PanelPath.Usb, "same path reported as change");
            // 両方なければ、両方の理由を出す
            usb.Fail = wifi.Fail = true;
            await paths.DisconnectAsync();
            string message = "";
            try { await paths.VerifyAsync(ping, IsHello); } catch (InvalidOperationException ex) { message = ex.Message; }
            Check(message.Contains("USB：") && message.Contains("Wi-Fi：") && paths.ActivePath == As100201PanelPath.None, "both missing not reported: " + message);
        }

        // 見張り(As100201PanelProtocol)と組み合わせ: USB が切れると、再接続で Wi-Fi に移る。電池残量の行を知らせる
        var usb2 = new FakePanelLink();
        var wifi2 = new FakePanelLink();
        var paths2 = new As100201PanelPathLink(usb2, wifi2);
        using (var panel = new As100201PanelProtocol(paths2))
        {
            var batteries = new List<As100201PanelBattery>();
            panel.BatteryReported += batteries.Add;
            await panel.VerifyAsync();
            Check(paths2.ActivePath == As100201PanelPath.Usb && panel.IsConnected, "protocol verify not on USB");
            usb2.Fail = true;
            usb2.Drop();
            Check(!panel.IsConnected, "protocol still connected after USB drop");
            await panel.MaintainAsync();
            Check(paths2.ActivePath == As100201PanelPath.Wifi && panel.IsConnected && wifi2.Requests.Count == 1, "protocol did not fail over to Wi-Fi");
            wifi2.Receive("BATT:75:7900:0");
            wifi2.Receive("BATT:abc");
            usb2.Receive("BATT:10:6500:0");
            Check(batteries.Count == 1 && batteries[0] == new As100201PanelBattery(75, 7900, false), "battery line not reported once");
        }

        // BATT: の読み取り
        Check(As100201PanelBattery.TryParse("BATT:40:7600:1", out var b) && b.Level == 40 && b.Millivolts == 7600 && b.Charging && b.Present && b.DisplayText == "充電中 40%", "BATT charging");
        Check(As100201PanelBattery.TryParse("BATT:75:7900:0", out b) && b.DisplayText == "電池 75%", "BATT text");
        Check(As100201PanelBattery.TryParse("BATT:-1:0:0", out b) && !b.Present && b.DisplayText == "電池なし", "BATT none");
        foreach (string bad in new[] { "BATT:101:7000:0", "BATT:-2:7000:0", "BATT:50:7000:2", "BATT:50:-1:0", "BATT:50:7000", "BATT:50:7000:0:1", "BATT:x:7000:0", "batt:50:7000:0" })
            Check(!As100201PanelBattery.TryParse(bad, out _), "accepted bad battery line: " + bad);

        // 残量が少ないときのログ: 20% 以下で 1 回、30% を超えたらまた出せる。充電中・電池なしは出さない
        var watch = new As100201BatteryWatch();
        Check(watch.Update(new(50, 7800, false)) is null, "warned at 50%");
        Check(watch.Update(new(20, 7000, false))?.Contains("20%") == true, "not warned at 20%");
        Check(watch.Update(new(15, 6900, false)) is null && watch.Update(new(25, 7100, false)) is null && watch.Update(new(30, 7200, false)) is null, "warned twice");
        Check(watch.Update(new(31, 7200, false)) is null && watch.Update(new(19, 7000, false))?.Contains("19%") == true, "not rearmed above 30%");
        var charging = new As100201BatteryWatch();
        Check(charging.Update(new(10, 6600, true)) is null && charging.Update(new(-1, 0, false)) is null, "warned while charging / without battery");
        Check(charging.Update(new(10, 6600, false))?.Contains("10%") == true, "charging consumed the warning");
        Pass("panel paths: USB preferred, inactive path ignored, failover to Wi-Fi on drop (also via protocol reconnect), stays on Wi-Fi until re-verify then back to USB, both-missing reasons; BATT lines; low battery log once at 20% (rearm above 30%, not while charging)");
    }

    // 6. 通常モードを選んだとき今までと同じ
    private static void NormalModeChecks()
    {
        Check(default(As100201StartupMode) == As100201StartupMode.Normal, "default mode is not Normal");
        using (var dialog = new StartupModeDialog())
            Check(dialog.SelectedMode == As100201StartupMode.Normal && !dialog.SelectedVirtualPanel, "dialog initial selection is not Normal");
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
