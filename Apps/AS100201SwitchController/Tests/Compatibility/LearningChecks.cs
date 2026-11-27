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
        NozzleFlowChecks();
        WriteFailureChecks();
        PressedRowChecks();
        NextStepChecks();
        await PanelOutageChecks();
        await PanelPathAndBatteryChecks();
        await UsbReturnChecks();
        await WifiRefreshChecks();
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

        // 並べる行は全部パネルの行として読め、重複がない(版 6: 発振器 4・バルブを動かす・着磁 1・減磁 2・ノズル 6・測り直す・
        // スペーサー 4・完了・取消・測定停止)。
        var lines = As100201VirtualPanelLink.AllCommands.Select(c => c.Line).ToList();
        Check(lines.Count == 22 && lines.Distinct().Count() == 22 && lines.Contains("ACT:NOZZLE:R:CANCEL") && lines.Contains("ACT:SPACER:P:DOWN:0.450:NA") &&
            lines[0] == "PHASE:PC1" && lines.Contains("VALVE") && lines.Contains("REMEASURE") &&
            lines[^3] == "DONE" && lines[^2] == "UNDO" && lines[^1] == "STOP", "virtual panel command list");
        Check(!link.Press("ACT:MAG") && received.Count == 0, "pressed before connection");

        await panel.VerifyAsync();
        Check(panel.IsConnected && !panel.InputReady, "virtual panel verified but ready before frames");
        // 定期応答はアプリの中で 250ms ごとに出る。1 秒の安定確認のあとに受け付ける。
        await Task.Delay(1600);
        Check(panel.InputReady, "virtual panel frames did not make input ready");
        Check(link.Press("ACT:NOZZLE:P:START") && received.Count == 1 && received[0].DisplayName == "ノズル P 側（合わせ始め）", "virtual press not delivered");
        Check(panel.TrySend(As100201PanelProtocol.Ack(received[0])) && sent.SequenceEqual(new[] { "ACK:ACT:NOZZLE:P:START" }), "reply to virtual panel not shown");

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
        // 2026-11-26 から、どちらのモードも「次にすること」の分だけ 1 つ多い。
        Check(As100201VirtualPanelLink.CommandLineSwitch == "--virtual-panel", "command line switch");
        using (var learning = new MainForm(As100201StartupMode.Learning))
            Check(!learning.UsesVirtualPanel && learning.Controls.Count == 5, "virtual panel shown without the switch");
        using (var normal = new MainForm(As100201StartupMode.Normal, virtualPanel: true))
            Check(!normal.UsesVirtualPanel && !normal.IsLearningMode && normal.Controls.Count == 2, "virtual panel in normal mode");
        using (var dev = new MainForm(As100201StartupMode.Learning, virtualPanel: true))
        {
            Check(dev.UsesVirtualPanel && dev.IsLearningMode && dev.Text == As100201Application.LearningWindowTitle + As100201Application.VirtualPanelTitleSuffix, "virtual panel title");
            Check(dev.Controls.Count == 6 && FindLabels(dev).Any(l => l.Text.StartsWith("パネルへの返事")), "virtual panel controls missing");
        }
        Pass("virtual panel (--virtual-panel, dev only): 22 panel lines (v6), same protocol path, 250 ms frames -> ready, ACK/NAK shown, pause -> stale + NAK, unplug/replug reconnect, only with the switch in learning mode");
    }

    // 1. パネルの行の読み取り
    private static void PanelCommandChecks()
    {
        Check(As100201PanelCommand.TryParse("ACT:MAG", out var c) && c.Kind == As100201PanelCommandKind.Operation &&
            c.Operation == As100201Operation.Magnetize && c.Direction == "" && c.Degree == "" && c.OperationText == "着磁" && c.DisplayName == "着磁", "MAG");
        Check(As100201PanelCommand.TryParse("ACT:DEMAG:FULL", out c) && c.Operation == As100201Operation.Demagnetize && c.Degree == "しっかり" && c.DisplayName == "減磁（しっかり）", "DEMAG FULL");
        Check(As100201PanelCommand.TryParse("ACT:DEMAG:TAP", out c) && c.Degree == "ちょんちょん", "DEMAG TAP");
        // 版 6(2026-11-27): ノズルは方向をやめ、合わせ始め(START)・合わせた(DONE)・戻る(CANCEL)。
        Check(As100201PanelCommand.TryParse("ACT:NOZZLE:P:START", out c) && c.Kind == As100201PanelCommandKind.Operation && c.Operation == As100201Operation.NozzleP &&
            c.NozzleStep == As100201NozzleStep.Start && c.Direction == "" && c.Degree == "" && c.Thickness == "" && c.OperationText == "ノズル P" &&
            c.NozzleStepText == "合わせ始め" && c.NozzleSide == 'P' && c.DisplayName == "ノズル P 側（合わせ始め）", "NOZZLE P START");
        Check(As100201PanelCommand.TryParse("ACT:NOZZLE:R:DONE", out c) && c.Operation == As100201Operation.NozzleR && c.NozzleStep == As100201NozzleStep.Aligned &&
            c.DisplayName == "ノズル R 側（合わせた）", "NOZZLE R DONE");
        Check(As100201PanelCommand.TryParse("ACT:NOZZLE:P:CANCEL", out c) && c.Kind == As100201PanelCommandKind.NozzleCancel && c.Operation == As100201Operation.NozzleP &&
            c.NozzleStep == As100201NozzleStep.None && c.DisplayName == "ノズル P 側（戻る）", "NOZZLE P CANCEL");
        // 版 6: 発振器(位相ごとのボタン)・バルブを動かす・測り直す・測定停止。発振器は今までのスイッチ名の 1 回押し・2 回押しに置き換わる。
        Check(As100201PanelCommand.TryParse("PHASE:PC1", out c) && c.Kind == As100201PanelCommandKind.Phase && c.PhaseButton == "PC1" && c.DisplayName == "PC 位相1" &&
            c.ToSwitch() is { Button: "PC_go", Kind: As100201PressKind.Single }, "PHASE PC1");
        Check(As100201PanelCommand.TryParse("PHASE:CR2", out c) && c.ToSwitch() is { Button: "CR_go", Kind: As100201PressKind.Double } && c.DisplayName == "CR 位相2", "PHASE CR2");
        Check(As100201PanelCommand.TryParse("PHASE:PC2", out c) && c.ToSwitch().Kind == As100201PressKind.Double &&
            As100201PanelCommand.TryParse("PHASE:CR1", out c) && c.ToSwitch() is { Button: "CR_go", Kind: As100201PressKind.Single }, "PHASE PC2 / CR1");
        Check(As100201PanelCommand.TryParse("VALVE", out c) && c.Kind == As100201PanelCommandKind.Valve && c.DisplayName == "バルブを動かす", "VALVE");
        Check(As100201PanelCommand.TryParse("REMEASURE", out c) && c.Kind == As100201PanelCommandKind.Remeasure && c.DisplayName == "測り直す", "REMEASURE");
        Check(As100201PanelCommand.TryParse("STOP", out c) && c.Kind == As100201PanelCommandKind.Stop && c.DisplayName == "測定停止", "STOP");
        // スペーサーは向き・厚さ(箱の表示)・表示との差(μm。NA は測っていない)。差は「程度」の列の言葉にする。
        Check(As100201PanelCommand.TryParse("ACT:SPACER:P:UP:0.450:+2", out c) && c.Operation == As100201Operation.SpacerP && c.Direction == "上げる" &&
            c.Thickness == "0.450" && c.Degree == "差+2μm" && c.DisplayName == "スペーサー P 側（上げる・0.450・差+2μm）", "SPACER P UP +2");
        Check(As100201PanelCommand.TryParse("ACT:SPACER:R:DOWN:0.271:-3", out c) && c.Operation == As100201Operation.SpacerR && c.Direction == "下げる" && c.Degree == "差-3μm", "SPACER R DOWN -3");
        Check(As100201PanelCommand.TryParse("ACT:SPACER:R:DOWN:0.593:0", out c) && c.Degree == "差0μm", "SPACER diff 0");
        Check(As100201PanelCommand.TryParse("ACT:SPACER:P:UP:0.400:NA", out c) && c.Degree == "差未測定" && c.Thickness == "0.400", "SPACER not measured");
        Check(As100201PanelCommand.TryParse("ACT:SPACER:P:UP:0.400:+50", out _) && !As100201PanelCommand.TryParse("ACT:SPACER:P:UP:0.400:+51", out _), "SPACER diff limit");
        Check(As100201PanelCommand.TryParse("DONE", out c) && c.Kind == As100201PanelCommandKind.Done && c.Operation is null && c.DisplayName == "完了", "DONE");
        Check(As100201PanelCommand.TryParse("UNDO", out c) && c.Kind == As100201PanelCommandKind.Undo && c.DisplayName == "取消", "UNDO");
        // 版 5 までの行(ノズルの方向・程度、パネルに触れた HOLD、厚さのないスペーサー)は受け付けない。
        foreach (string bad in new[] { "ACT", "ACT:", "ACT:MAG:1", "ACT:DEMAG", "ACT:DEMAG:HALF", "ACT:NOZZLE:P:IN", "ACT:NOZZLE:R:OUT", "ACT:NOZZLE:P:IN:2",
            "ACT:NOZZLE:P", "ACT:NOZZLE:X:START", "ACT:NOZZLE:P:start", "ACT:NOZZLE:P:START:1",
            "ACT:NOZZLE:P:UP", "ACT:SPACER:P", "ACT:SPACER:P:UP", "ACT:SPACER:R:DOWN", "ACT:SPACER:P:IN:0.450:0", "ACT:SPACER:P:UP:0.45:0", "ACT:SPACER:P:UP:.4500:0",
            "ACT:SPACER:P:UP:0.450", "ACT:SPACER:P:UP:0.450:2", "ACT:SPACER:P:UP:0.450:+0", "ACT:SPACER:P:UP:0.450:na", "ACT:SPACER:P:UP:0.450:+2:1",
            "act:mag", "DONE ", "UNDO:1", "HOLD", "HOLD:1", "PC_go", "PHASE:", "PHASE:PC3", "PHASE:pc1", "PHASE:PC1:NOZZLE", "VALVE:1", "STOP ", "REMEASURE:PC1",
            "DIAG_FRAME:1:250", "AS100201_PANEL_READY:1", "" })
            Check(!As100201PanelCommand.TryParse(bad, out _), "accepted bad panel line: " + bad);
        Check(As100201PanelProtocol.Ack(As100201PanelCommand.Parse("ACT:NOZZLE:P:START")) == "ACK:ACT:NOZZLE:P:START", "ACK format");
        Check(As100201PanelProtocol.Nak("理由") == "NAK:理由", "NAK format");
        // 代わりのパネル: スペーサーだけ厚さと差を差し替える。
        Check(As100201VirtualPanelLink.WithSpacer(As100201PanelCommand.Parse("ACT:SPACER:R:UP:0.450:NA"), "0.510", "-1").Line == "ACT:SPACER:R:UP:0.510:-1" &&
            As100201VirtualPanelLink.WithSpacer(As100201PanelCommand.Parse("ACT:MAG"), "0.510", "-1").Line == "ACT:MAG", "virtual spacer");
        Check(As100201SpacerCatalog.Thicknesses.Count == 49 && As100201SpacerCatalog.Thicknesses.Distinct().Count() == 49 &&
            As100201SpacerCatalog.Thicknesses.All(t => As100201PanelCommand.TryParse($"ACT:SPACER:P:UP:{t}:NA", out _)) &&
            As100201SpacerCatalog.Differences.All(d => As100201PanelCommand.TryParse($"ACT:SPACER:P:UP:0.450:{d}", out _)), "spacer catalog");
        // 版 5(2026-11-26): 操作の行の後ろの :MOVED(測定中に動かした)。どの操作にも付く。台帳の言葉は変えず、印だけ足す。
        Check(As100201PanelCommand.TryParse("ACT:MAG:MOVED", out c) && c.Moved && c.Operation == As100201Operation.Magnetize && c.Line == "ACT:MAG:MOVED" &&
            c.DisplayName == "着磁（測定中に動かした）", "MAG MOVED");
        Check(As100201PanelCommand.TryParse("ACT:NOZZLE:R:START:MOVED", out c) && c.Moved && c.NozzleStep == As100201NozzleStep.Start &&
            c.DisplayName == "ノズル R 側（合わせ始め・測定中に動かした）", "NOZZLE MOVED");
        Check(As100201PanelCommand.TryParse("ACT:DEMAG:FULL:MOVED", out c) && c.Moved && c.Degree == "しっかり", "DEMAG MOVED");
        Check(As100201PanelCommand.TryParse("ACT:SPACER:P:UP:0.450:+2:MOVED", out c) && c.Moved && c.Thickness == "0.450" && c.Degree == "差+2μm", "SPACER MOVED");
        Check(As100201PanelCommand.TryParse("ACT:MAG", out c) && !c.Moved, "MAG marked as moved");
        Check(As100201PanelProtocol.Ack(As100201PanelCommand.Parse("ACT:MAG:MOVED")) == "ACK:ACT:MAG:MOVED", "ACK keeps the mark");
        foreach (string bad in new[] { "ACT:MOVED", "ACT::MOVED", "ACT:MAG:MOVED:MOVED", "ACT:MAG:moved", "DONE:MOVED", "UNDO:MOVED", "HOLD:MOVED", "ACT:DEMAG:MOVED",
            "ACT:NOZZLE:P:MOVED", "ACT:NOZZLE:P:CANCEL:MOVED", "PHASE:PC1:MOVED", "VALVE:MOVED" })
            Check(!As100201PanelCommand.TryParse(bad, out _), "accepted bad moved line: " + bad);
        // 直前の記録の知らせ(版 5)。
        Check(As100201PanelProtocol.Last(null) == "LAST:", "LAST none");
        // 版 6(2026-11-27、Arduino のスイッチを Tab5 にまとめた)。版 5 までのスケッチは書き込み直しを求める。
        Check(As100201PanelProtocol.CheckHello("AS100201_PANEL_READY:7") is null, "panel version 7 rejected");
        Check(As100201PanelProtocol.CheckHello("AS100201_PANEL_READY:6")?.Contains("版 6") == true, "panel version 6 accepted");
        Check(As100201PanelProtocol.CheckHello("AS100201_PANEL_READY:5")?.Contains("版 5") == true, "panel version 5 accepted");
        Check(As100201PanelProtocol.CheckHello("AS100201_PANEL_READY:4")?.Contains("版 4") == true, "panel version 4 accepted");
        Check(As100201PanelProtocol.CheckHello("AS100201_PANEL_READY:3")?.Contains("版 3") == true, "panel version 3 accepted");
        Check(!As100201PanelCommand.TryParse("SELECT", out _) && !As100201PanelCommand.TryParse("HOME", out _) &&
            !As100201PanelCommand.TryParse("NOZZLE:P", out _), "screen lines parsed as commands");
        // 画面の位置(版 6 で NOZZLE:P / NOZZLE:R を足した)と、PC から送る行(STATE: / GOTO: / MSG:)。
        Check(As100201PanelProtocol.TryParseScreen("HOME", out var screen) && screen == As100201PanelScreen.Home &&
            As100201PanelProtocol.TryParseScreen("SELECT", out screen) && screen == As100201PanelScreen.Selecting &&
            As100201PanelProtocol.TryParseScreen("NOZZLE:P", out screen) && screen == As100201PanelScreen.NozzleP &&
            As100201PanelProtocol.TryParseScreen("NOZZLE:R", out screen) && screen == As100201PanelScreen.NozzleR &&
            !As100201PanelProtocol.TryParseScreen("NOZZLE:X", out _) && !As100201PanelProtocol.TryParseScreen("home", out _), "screen lines");
        Check(As100201PanelProtocol.State(true, "PC2", false, As100201SwitchSettings.Defaults) == "STATE:L:PC2:0:0U:255U:136D:-238D" &&
            As100201PanelProtocol.State(false, null, true, As100201SwitchSettings.Defaults with { CrSinglePhaseDegrees = 145 }) == "STATE:N:-:1:0U:255U:119D:-238D" &&
            As100201PanelProtocol.State(true, "M", false, As100201SwitchSettings.Defaults).StartsWith("STATE:L:M:0:", StringComparison.Ordinal), "STATE line");
        // 位相の点の電流(版 7。三角波 10.2Vpp・5V = 30mA。0° は 0 から上り、90° が山、270° が谷)。
        static bool Near((double Milliamps, bool Rising) actual, double milliamps, bool rising) =>
            Math.Abs(actual.Milliamps - milliamps) < 1e-6 && actual.Rising == rising;
        Check(Near(As100201WaveformSettings.CurrentAtPhase(0), 0, true) && Near(As100201WaveformSettings.CurrentAtPhase(90), 30.6, false) &&
            Near(As100201WaveformSettings.CurrentAtPhase(180), 0, false) && Near(As100201WaveformSettings.CurrentAtPhase(270), -30.6, true) &&
            Near(As100201WaveformSettings.CurrentAtPhase(359), -0.34, true) && Near(As100201WaveformSettings.CurrentAtPhase(-90), -30.6, true), "current at phase");
        Check(As100201PanelProtocol.GotoNozzle('R') == "GOTO:NOZZLE:R" && As100201PanelProtocol.Message('E', "x") == "MSG:E:x", "GOTO / MSG lines");
        Check(As100201PanelProtocol.CheckHello("AS100201_PANEL_READY:2")?.Contains("版 2") == true, "panel version 2 accepted");
        Check(As100201PanelProtocol.CheckHello("AS100201_PANEL_READY:1")?.Contains("版 1") == true, "panel version 1 accepted");
        Check(As100201PanelProtocol.CheckHello("AS100201_READY:2") is not null && As100201PanelProtocol.CheckHello(null)?.Contains("不明") == true, "Arduino reply accepted as panel");
        Check(As100201PanelProtocol.HelloRequest != "AS100201_PING" && !As100201PanelProtocol.HelloPrefix.StartsWith(As100201ArduinoProtocol.HelloPrefix) &&
            !As100201ArduinoProtocol.HelloPrefix.StartsWith(As100201PanelProtocol.HelloPrefix), "panel and Arduino hello strings collide");
        Pass("panel lines (v6): PHASE / VALVE / REMEASURE / STOP, ACT:MAG / DEMAG / NOZZLE START+DONE+CANCEL / SPACER thickness+diff / DONE / UNDO parsed with ledger words; :MOVED mark on operations; v5 lines (nozzle direction, HOLD) and malformed lines rejected; ACK/NAK; LAST:; STATE / GOTO / MSG; panel version 6 only; screen lines incl. NOZZLE:P/R are not commands");
    }

    // 2. 台帳の行の組み立て(CSV の形)
    private static void LedgerRowChecks()
    {
        var time = new DateTime(2026, 11, 9, 13, 5, 7);
        // 2026-11-26: 「日時」の次に「シリアル番号」の列。
        Check(As100201LedgerRow.HeaderLine == "日時,シリアル番号,種類,側,位相,停止のきっかけ,操作,方向,程度,厚さ,ファイル,備考", "header columns");
        var measurement = new As100201LedgerRow(time, As100201LedgerEventKind.Measurement) { SerialNumber = "SN145", Side = "PC（2回押し）", Phase = "75", StopTrigger = "sw4", File = @"SN145\20261109_130507_SN145_PC2.xdt" };
        Check(measurement.ToCsvLine() == @"2026-11-09 13:05:07,SN145,測定,PC（2回押し）,75,sw4,,,,,SN145\20261109_130507_SN145_PC2.xdt,", "measurement row");
        var operation = new As100201LedgerRow(time, As100201LedgerEventKind.Operation) { SerialNumber = "SN145", Operation = "ノズル P", Direction = "近づける", Degree = "", Note = "完了の取消、直前の測定なし" };
        Check(operation.ToCsvLine() == "2026-11-09 13:05:07,SN145,操作,,,,ノズル P,近づける,,,,完了の取消、直前の測定なし", "operation row");
        Check(new As100201LedgerRow(time, As100201LedgerEventKind.Done).KindText == "完了" && new As100201LedgerRow(time, As100201LedgerEventKind.Undo).KindText == "取消" &&
            new As100201LedgerRow(time, As100201LedgerEventKind.SerialSet).KindText == "番号", "kind text");
        // 保存先は exe の隣の「学習データ」(2026-11-10。雛形のフォルダーではない)。台帳の「ファイル」列は番号のフォルダー付き(2026-11-26)。
        Check(MainForm.LearningDataDirectory(@"C:\apps\AS100201") == @"C:\apps\AS100201\学習データ" && MainForm.LedgerFileName == "調整記録.csv" &&
            MainForm.TemplateFolderName == "雛形", "save directory is not next to the exe");
        Check(As100201LearningSession.LedgerFileText(@"C:\apps\学習データ\SN145\20261110_133028_SN145_PC.xdt") == @"SN145\20261110_133028_SN145_PC.xdt", "ledger file column without serial folder");
        // 読み戻し: 書いた行と同じになる(引用符・カンマ・改行を含む備考も)。列の数が違う・種類が読めない行は null。
        var quoted = operation with { Note = "a,\"b\"\r\nc" };
        foreach (var row in new[] { measurement, operation, quoted, new As100201LedgerRow(time, As100201LedgerEventKind.SerialSet) { SerialNumber = "SN001", Note = "新規" } })
        {
            var parsed = As100201LedgerRow.ParseCsv(row.ToCsvLine() + "\r\n");
            Check(parsed.Count == 1 && As100201LedgerRow.FromFields(parsed[0]) == row, "ledger row round trip: " + row.ToCsvLine());
        }
        Check(As100201LedgerRow.FromFields(As100201LedgerRow.ParseCsv("2026-11-09 13:05:07,測定,PC,0,sw4,,,,,a.xdt,")[0]) is null &&
            As100201LedgerRow.FromFields(As100201LedgerRow.ParseCsv("2026-11-09 13:05:07,SN1,なにか,,,,,,,,,")[0]) is null, "bad ledger rows parsed");
        // シリアル番号の形: 数字だけ(SN は付けても付けなくても)、3 桁にそろえる。0・6 桁以上・数字以外は受け付けない。
        foreach (var (input, expected) in new[] { ("145", "SN145"), ("7", "SN007"), (" 045 ", "SN045"), ("SN122", "SN122"), ("sn 9", "SN009"), ("1000", "SN1000"), ("99999", "SN99999") })
            Check(As100201LearningSession.TryFormatSerial(input, out string serial, out _) && serial == expected, $"serial {input} -> {expected}");
        foreach (string bad in new[] { "", " ", "SN", "0", "000", "12a", "１２３", "-1", "1.5", "100000", "SN-1" })
            Check(!As100201LearningSession.TryFormatSerial(bad, out _, out string error) && error.Length > 0, "serial accepted: " + bad);
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
            string ledgerPath = Path.Combine(data, "調整記録.csv");
            var real = new As100201LearningSession(new As100201CsvLedger(ledgerPath), _ => 0) { SaveDirectory = data };
            Check(real.CheckLedger() && real.FindSerial("SN145") is null, "missing ledger not readable as empty");
            Check(real.SelectSerial("SN145", null) && File.Exists(ledgerPath) && !real.Blocked, "save folder not created on first row");
            Check(real.HandleCommand(As100201PanelCommand.Parse("ACT:MAG")) == "ACK:ACT:MAG", "operation after serial");
            // 書いた台帳を読み戻して、番号の記録を数える(起動し直したあとの続き)。
            var reread = new As100201LearningSession(new As100201CsvLedger(ledgerPath), _ => 0) { SaveDirectory = data };
            Check(reread.CheckLedger() && reread.FindSerial("SN145") is { MeasurementCount: 0, OperationCount: 1, Completed: false } && reread.FindSerial("SN146") is null,
                "ledger history after restart");

            // 列の形が違う台帳(番号の列を足す前): 読めない・書き足さない。起動時に止め、台帳を移して「再試行」で再開する(2026-11-26)。
            string oldData = Path.Combine(root, "旧");
            Directory.CreateDirectory(oldData);
            string oldLedger = Path.Combine(oldData, "調整記録.csv");
            File.WriteAllText(oldLedger, "日時,種類,側,位相,停止のきっかけ,操作,方向,程度,厚さ,ファイル,備考\r\n2026-11-10 13:30:28,測定,PC,0,sw4,,,,,a.xdt,\r\n", new UTF8Encoding(true));
            var old = new As100201LearningSession(new As100201CsvLedger(oldLedger), _ => 0) { SaveDirectory = oldData };
            var oldLogs = new List<string>();
            old.Log += oldLogs.Add;
            Check(!old.CheckLedger() && old.Blocked && old.BlockReason!.Contains("列の形が今の作りと違います"), "old ledger accepted");
            try { new As100201CsvLedger(oldLedger).Append(measurement); throw new Exception("appended to old ledger"); }
            catch (As100201LedgerFormatException) { }
            Check(!old.Retry() && old.Blocked && File.ReadAllLines(oldLedger).Length == 2, "retry passed with old ledger");
            File.Move(oldLedger, Path.Combine(oldData, "調整記録_旧.csv"));
            Check(old.Retry() && !old.Blocked && old.SelectSerial("SN001", null) && File.ReadAllLines(oldLedger)[0].TrimStart('﻿') == As100201LedgerRow.HeaderLine,
                "not resumed after the old ledger was moved");

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

            // 参照波形(2026-11-26): 同じ「雛形」フォルダーの .xyb がちょうど 1 つのときだけ。xdt は数えない。
            Check(MainForm.FindLearningReference(Path.Combine(root, "無い"), out reason) is null && reason.Contains("雛形フォルダーがありません"), "missing reference folder");
            Check(MainForm.FindLearningReference(templates, out reason) is null && reason.Contains(".xyb）がありません"), "no reference");
            string reference = Path.Combine(templates, "参照ﾃﾞｰﾀ_AS100-201_盲栓PC+CR_SN149.xyb");
            File.WriteAllText(reference, "");
            Check(MainForm.FindLearningReference(templates, out reason) == reference && reason == "", "single reference not found");
            File.WriteAllText(Path.Combine(templates, "別の参照.xyb"), "");
            Check(MainForm.FindLearningReference(templates, out reason) is null && reason.Contains("2 つ"), "two references accepted");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        Pass("ledger rows: 12 columns (serial after time), RFC4180 quoting + read back, serial format SN + 3 digits, CSV file with BOM + header once, append-only, locked/missing folder -> exception; save folder next to exe, created on first row; history read back after restart; old-format ledger blocks until moved; template folder needs exactly one xdt and one xyb (reference)");
    }

    private sealed class FakeLedger : IAs100201LearningLedger
    {
        public List<As100201LedgerRow> Rows { get; } = new();
        public bool Fail;
        public string Location => @"C:\fake\調整記録.csv";
        public void Append(As100201LedgerRow row) { if (Fail) throw new IOException("fake ledger failure"); Rows.Add(row); }
        public void EnsureWritable() { if (Fail) throw new IOException("fake ledger failure"); }
        public IReadOnlyList<As100201LedgerRow> ReadRows() { if (Fail) throw new IOException("fake ledger failure"); return Rows.ToList(); }
    }

    // シリアル番号 SN001 を決めた状態で始める(2026-11-26 から、番号が無いと記録しない)。番号の行は消して、行の数を今までと同じに数える。
    // 時計は番号の行で 1 回進むので、1 秒前から始めて、そのあとの時刻を今までと同じにする。
    private static (As100201LearningSession Session, FakeLedger Ledger, List<string> Saved, List<string> Logs, Func<long> SaveResult) NewSession(DateTime start, List<string>? created = null)
    {
        var ledger = new FakeLedger();
        var saved = new List<string>();
        var logs = new List<string>();
        long saveResult = 0;
        DateTime now = start.AddSeconds(-1);
        var session = new As100201LearningSession(ledger, path => { if (saveResult != 0) return saveResult; saved.Add(path); return 0; },
            () => now = now.AddSeconds(1), path => saved.Contains(path), path => created?.Add(path))
        { SaveDirectory = @"C:\fake" };
        session.Log += logs.Add;
        StartSerial(session, ledger);
        return (session, ledger, saved, logs, () => saveResult);
    }

    private static void StartSerial(As100201LearningSession session, FakeLedger ledger, string serial = "SN001")
    {
        Check(session.SelectSerial(serial, null) && session.SerialNumber == serial && ledger.Rows[^1] is { Kind: As100201LedgerEventKind.SerialSet, Note: "新規" },
            "serial not selected: " + serial);
        ledger.Rows.Clear();
    }

    // 3. 完了・取消・完了の取消(1 個体の流れ)
    private static void SessionFlowChecks()
    {
        var created = new List<string>();
        var (s, ledger, saved, logs, _) = NewSession(new DateTime(2026, 11, 9, 9, 0, 0), created);
        var mag = As100201PanelCommand.Parse("ACT:MAG");
        // 版 6 でノズルは合わせ始め・合わせた(NozzleFlowChecks)になったので、ここでは減磁を使う。
        var nozzle = As100201PanelCommand.Parse("ACT:DEMAG:FULL");
        var done = As100201PanelCommand.Parse("DONE");
        var undo = As100201PanelCommand.Parse("UNDO");

        // 起動直後に操作: 「直前の測定なし」。測定: sw4 で止めて行、停止に戻ってから xdt。
        Check(s.HandleCommand(mag) == "ACK:ACT:MAG" && ledger.Rows[^1] is { Kind: As100201LedgerEventKind.Operation, SerialNumber: "SN001", Note: "直前の測定なし" }, "first operation without measurement");
        s.MeasurementStopped("PC", "PC", "0", As100201LearningSession.StopBySwitch);
        Check(ledger.Rows[^1] is { Kind: As100201LedgerEventKind.Measurement, SerialNumber: "SN001", Side: "PC", Phase: "0", StopTrigger: "sw4", File: @"SN001\20261109_090002_SN001_PC.xdt", Note: "" } && s.HasArmedWaveform && saved.Count == 0,
            "measurement row at stop, xdt not yet saved");
        s.MeasurementCompleted("PC", "PC", "0");
        Check(!s.HasArmedWaveform && saved.SequenceEqual(new[] { @"C:\fake\SN001\20261109_090002_SN001_PC.xdt" }) && ledger.Rows.Count == 2, "xdt saved at completion, no extra row");
        // 台帳の前に保存先を、xdt の前に番号のフォルダーを作る(2026-11-10。番号のフォルダーは 2026-11-26 から)。
        Check(created.Contains(@"C:\fake") && created[^1] == @"C:\fake\SN001", "save folder / serial folder not created before writing");
        // 操作 → 測定(操作ボタンで止めた) → 操作 → 測定(LOGGER が自動で止まった: 行と xdt を一緒に)
        Check(s.HandleCommand(nozzle) == "ACK:ACT:DEMAG:FULL" && ledger.Rows[^1] is { Operation: "減磁", Direction: "", Degree: "しっかり", Thickness: "", Note: "" }, "operation after measurement");
        s.MeasurementStopped("CR（2回押し）", "CR2", "250", As100201LearningSession.StopByPanel);
        s.MeasurementCompleted("CR（2回押し）", "CR2", "250");
        Check(ledger.Rows[^1] is { Kind: As100201LedgerEventKind.Measurement, Side: "CR（2回押し）", Phase: "250", StopTrigger: "操作ボタン", File: @"SN001\20261109_090004_SN001_CR2.xdt" }, "measurement stopped by panel");
        Check(s.HandleCommand(As100201PanelCommand.Parse("ACT:DEMAG:TAP")) == "ACK:ACT:DEMAG:TAP", "demag");
        s.MeasurementCompleted("CR", "CR", "140");
        Check(ledger.Rows[^1] is { Kind: As100201LedgerEventKind.Measurement, StopTrigger: "LOGGER の停止", File: @"SN001\20261109_090006_SN001_CR.xdt" } && saved[^1].EndsWith("20261109_090006_SN001_CR.xdt"), "auto-stopped measurement: row and xdt together");
        Check(s.MeasurementCount == 3 && s.OperationCount == 3 && !s.Completed, "counts");

        // 完了: 件数を備考に。番号は空になり(2026-11-26)、入れるまで操作・完了・取消は NAK(行なし)。
        int before = ledger.Rows.Count;
        Check(s.HandleCommand(done) == "ACK:DONE" && ledger.Rows[^1] is { Kind: As100201LedgerEventKind.Done, SerialNumber: "SN001", Note: "この個体の測定 3 回・操作 3 回" } &&
            s.Completed && s.SerialNumber is null && s.StateText.Contains("シリアル番号を入れてください"), "done row clears the serial");
        Check(s.HandleCommand(mag) == "NAK:" + As100201LearningSession.NakNoSerial && s.HandleCommand(done).StartsWith("NAK:") && s.HandleCommand(undo).StartsWith("NAK:") &&
            s.HandleCommand(As100201PanelCommand.Parse("PHASE:PC1")) == "ACK:PHASE:PC1" && s.HandleCommand(As100201PanelCommand.Parse("VALVE")) == "ACK:VALVE" &&
            ledger.Rows.Count == before + 1, "commands recorded without a serial");
        // 番号が無い間は、取消を押しても確認を開かず、バルブを動かすも断る(同じ文言。2026-11-27 人の指摘)。測っていればバルブは受け付ける。
        Check(s.LastLine() == "LAST:X:" + As100201LearningSession.NakNoSerial, "LAST without a serial: " + s.LastLine());
        Check(s.ValveRefusal(measuring: false) == As100201LearningSession.NakNoSerial && s.ValveRefusal(measuring: true) is null, "valve without a serial");
        // 同じ番号を続きとして入れ直す(カバーを付けて NG など): 台帳から数え直し、完了済みのまま。
        var history = s.FindSerial("SN001");
        Check(history is { MeasurementCount: 3, OperationCount: 3, Completed: true, LastIsMeasurement: false }, "history of the completed serial");
        Check(s.SelectSerial("SN001", history) && ledger.Rows[^1] is { Kind: As100201LedgerEventKind.SerialSet, SerialNumber: "SN001", Note: "続き（完了済み）" } &&
            s.Completed && s.MeasurementCount == 3 && s.OperationCount == 3 && s.SerialNumber == "SN001", "continue the completed serial");
        before = ledger.Rows.Count;
        Check(s.HandleCommand(done).StartsWith("NAK:完了は記録済み") && ledger.Rows.Count == before, "second done recorded");
        // 完了の後の取消: 取り消せる操作がない。
        Check(s.HandleCommand(undo).StartsWith("NAK:") && ledger.Rows.Count == before && logs[^1].Contains("取り消せる記録がありません") && s.UndoTarget is null, "undo after done");
        Check(s.LastLine() == "LAST:" && s.ValveRefusal(measuring: false) is null, "LAST / valve with a serial again: " + s.LastLine());
        // 完了のあとに操作: 「完了の取消」+「直前の測定なし」、同じ個体の続き。
        Check(s.HandleCommand(mag) == "ACK:ACT:MAG" && ledger.Rows[^1].Note == "完了の取消、直前の測定なし" && !s.Completed && s.OperationCount == 4, "operation after done cancels done");
        // 取消: 直前の操作 1 件だけ。2 回目は NAK。取消のあと操作は「直前の測定なし」のまま(取り消した操作の前の状態に戻る)。
        // 版 5: 操作の取消の行には、対象の操作を「操作」などの列にも書く。
        Check(As100201PanelProtocol.Last(s.UndoTarget) == "LAST:O:09:00:09 着磁", "LAST for the operation: " + As100201PanelProtocol.Last(s.UndoTarget));
        Check(s.HandleCommand(undo) == "ACK:UNDO" && ledger.Rows[^1] is { Kind: As100201LedgerEventKind.Undo, SerialNumber: "SN001", Operation: "着磁", File: "" } &&
            ledger.Rows[^1].Note == "取消の対象: 09:00:09 着磁" && s.OperationCount == 3 && s.UndoTarget is null, "undo row");
        Check(s.HandleCommand(undo).StartsWith("NAK:取り消せる記録がありません"), "second undo");
        Check(s.HandleCommand(nozzle) == "ACK:ACT:DEMAG:FULL" && ledger.Rows[^1].Note == "直前の測定なし", "state restored after undo");
        // 版 5: 測定のあとの取消は、その測定を「使わない」にする(取消の行の「ファイル」列にその xdt)。xdt は残る。もう一度は NAK。
        s.MeasurementStopped("PC", "PC", "0", As100201LearningSession.StopBySwitch);
        s.MeasurementCompleted("PC", "PC", "0");
        Check(s.MeasurementCount == 4 && As100201PanelProtocol.Last(s.UndoTarget) == "LAST:M:09:00:12 PC", "LAST for the measurement: " + As100201PanelProtocol.Last(s.UndoTarget));
        int savedBefore = saved.Count;
        Check(s.HandleCommand(undo) == "ACK:UNDO" && ledger.Rows[^1] is { Kind: As100201LedgerEventKind.Undo, Operation: "", File: @"SN001\20261109_090012_SN001_PC.xdt" } &&
            ledger.Rows[^1].Note == "取消の対象: 測定 09:00:12 PC（使わない）" && s.MeasurementCount == 3 && saved.Count == savedBefore, "undo measurement: " + ledger.Rows[^1].Note);
        Check(s.HandleCommand(undo).StartsWith("NAK:") && logs.Any(l => l.Contains("を使わないにしました")), "second undo after measurement");
        // 測定を取り消したら、その前(ノズルの操作)に戻るので、次の操作は「直前の測定なし」。
        Check(s.HandleCommand(As100201PanelCommand.Parse("ACT:SPACER:R:DOWN:0.450:+2")) == "ACK:ACT:SPACER:R:DOWN:0.450:+2" &&
            ledger.Rows[^1] is { Operation: "スペーサー R", Direction: "下げる", Thickness: "0.450", Degree: "差+2μm", Note: "直前の測定なし" }, "spacer after undone measurement: " + ledger.Rows[^1].Note);
        foreach (string line in new[] { "PHASE:CR2", "VALVE", "REMEASURE", "STOP" })
            Check(s.HandleCommand(As100201PanelCommand.Parse(line)) == "ACK:" + line && ledger.Rows[^1].Operation == "スペーサー R", line + " wrote a ledger row");
        Check(s.HandleCommand(undo) == "ACK:UNDO" && ledger.Rows[^1] is { Operation: "スペーサー R", Direction: "下げる", Thickness: "0.450", Degree: "差+2μm" } &&
            ledger.Rows[^1].Note.EndsWith(" スペーサー R（下げる・0.450・差+2μm）"), "undo spacer");
        // 押した行は取消の対象を変えない: 測定 → 押した → 取消 で、その測定を取り消す。
        s.MeasurementStopped("CR", "CR", "140", As100201LearningSession.StopByPanel);
        s.MeasurementCompleted("CR", "CR", "140");
        s.WaveformRestarted("PC", "0", null, null);
        Check(s.UndoTarget is { IsMeasurement: true } && s.HandleCommand(undo) == "ACK:UNDO" && ledger.Rows[^1].File == @"SN001\20261109_090016_SN001_CR.xdt", "pressed row changed the undo target");
        Check(s.HandleCommand(done) == "ACK:DONE" && s.SerialNumber is null && As100201PanelProtocol.Last(s.UndoTarget) == "LAST:", "done 2");
        // 台帳からの数え直し: 取消は直前の操作・測定を消す(ファイル列のあるのが測定の取消)。番号の行・押した行は数えない。
        Check(s.FindSerial("SN001") is { MeasurementCount: 3, OperationCount: 4, Completed: true, LastIsMeasurement: false }, "history with undo rows");
        Check(As100201LearningSession.BuildHistory(new[]
        {
            new As100201LedgerRow(DateTime.MinValue, As100201LedgerEventKind.Operation) { SerialNumber = "SN9" },
            new As100201LedgerRow(DateTime.MinValue, As100201LedgerEventKind.Measurement) { SerialNumber = "SN9", File = "a.xdt" },
            new As100201LedgerRow(DateTime.MinValue, As100201LedgerEventKind.Undo) { SerialNumber = "SN9", File = "a.xdt" }
        }, "SN9") is { MeasurementCount: 0, OperationCount: 1, LastIsMeasurement: false }, "history: measurement undo");
        // 新しい番号: 件数を 0 から。xdt は番号のフォルダーに、名前にも番号が入る。
        Check(s.SelectSerial("SN002", null) && ledger.Rows[^1].Note == "新規" && s.UndoTarget is null, "new serial after done");
        s.MeasurementStopped("", "", "", As100201LearningSession.StopBySwitch);
        Check(ledger.Rows[^1] is { SerialNumber: "SN002", Side: "", Phase: "", Note: "位相の出し直しなし", File: @"SN002\20261109_090021_SN002.xdt" } && s.MeasurementCount == 1 && s.OperationCount == 0 && !s.Completed,
            "new individual: " + ledger.Rows[^1].File);
        Check(As100201PanelProtocol.Last(s.UndoTarget) == "LAST:M:09:00:21 側なし", "LAST without side: " + As100201PanelProtocol.Last(s.UndoTarget));
        // 同じ秒に 2 つ目の xdt は _2。日をまたいでも同じ番号のフォルダー。
        s.MeasurementCompleted("", "", "");
        Check(s.BuildXdtPath(new DateTime(2026, 11, 9, 9, 0, 21), "") == @"C:\fake\SN002\20261109_090021_SN002_2.xdt", "duplicate name suffix");
        Check(s.BuildXdtPath(new DateTime(2026, 11, 10, 0, 0, 1), "PC") == @"C:\fake\SN002\20261110_000001_SN002_PC.xdt", "serial folder after midnight");
        // 完了せずに次の番号へ: そのまま切り替え、番号の行とログに残す。
        Check(s.SelectSerial("SN003", null) && ledger.Rows[^1] is { SerialNumber: "SN003", Note: "新規、前の番号 SN002 は完了せず切り替え" } &&
            logs.Any(l => l.Contains("SN002 は完了せずに")), "switch without done");
        // 未完了の番号の続き: 最後が測定なら、次の操作に「直前の測定なし」は付かない。
        var unfinished = s.FindSerial("SN002");
        Check(unfinished is { MeasurementCount: 1, OperationCount: 0, Completed: false, LastIsMeasurement: true }, "history of the unfinished serial");
        Check(s.SelectSerial("SN002", unfinished) && ledger.Rows[^1].Note == "続き（未完了）、前の番号 SN003 は完了せず切り替え" &&
            s.HandleCommand(mag) == "ACK:ACT:MAG" && ledger.Rows[^1] is { SerialNumber: "SN002", Note: "" }, "continue the unfinished serial");
        // 版 5: 「測定中に動かした」の印は操作の行の備考に残る(ほかの備考と「、」でつなぐ)。
        Check(s.HandleCommand(As100201PanelCommand.Parse("ACT:DEMAG:TAP:MOVED")) == "ACK:ACT:DEMAG:TAP:MOVED" &&
            ledger.Rows[^1] is { Operation: "減磁", Degree: "ちょんちょん", Note: "直前の測定なし、測定中に動かした" }, "moved mark note: " + ledger.Rows[^1].Note);
        s.MeasurementStopped("PC", "PC", "0", As100201LearningSession.StopByPanel);
        Check(s.HandleCommand(As100201PanelCommand.Parse("ACT:MAG:MOVED")) == "ACK:ACT:MAG:MOVED" && ledger.Rows[^1].Note == "測定中に動かした", "moved mark after measurement");
        Check(ledger.Rows.All(r => r.KindText is "測定" or "操作" or "完了" or "取消" or "番号" or "押した") && !s.Blocked, "unexpected kinds or blocked");
        Pass("learning flow: serial on every row, first-op note, row at stop + xdt at completion in the serial folder, panel/auto stop triggers, done counts and clears the serial, NAK without serial, continue a completed/unfinished serial from the ledger, switch without done noted, single undo of the last operation or measurement (v5) with state restore, LAST: text, moved mark note");
    }

    // 3b. ノズルの立ち上がりを合わせる(版 6、2026-11-26 承認・2026-11-27 版 6 で作った)
    private static void NozzleFlowChecks()
    {
        var (s, ledger, _, _, _) = NewSession(new DateTime(2026, 11, 27, 10, 0, 0), []);
        var startP = As100201PanelCommand.Parse("ACT:NOZZLE:P:START");
        var doneP = As100201PanelCommand.Parse("ACT:NOZZLE:P:DONE");
        var undo = As100201PanelCommand.Parse("UNDO");
        s.MeasurementStopped("PC", "PC", "0", As100201LearningSession.StopByValve);
        s.MeasurementCompleted("PC", "PC", "0");
        Check(ledger.Rows[^1] is { Kind: As100201LedgerEventKind.Measurement, StopTrigger: "バルブを動かす" }, "valve stop trigger");
        // 合わせ始め: 操作の行(方向は空、備考「合わせ始め」)。合わせている間に何回測っても、その作業の中の測定。
        Check(s.HandleCommand(startP) == "ACK:ACT:NOZZLE:P:START" &&
            ledger.Rows[^1] is { Kind: As100201LedgerEventKind.Operation, Operation: "ノズル P", Direction: "", Note: "合わせ始め" } &&
            s.AligningNozzleSide == 'P' && s.OperationCount == 1, "nozzle start row: " + ledger.Rows[^1].Note);
        s.MeasurementStopped("PC", "PC", "0", As100201LearningSession.StopByRemeasure);
        s.MeasurementCompleted("PC", "PC", "0");
        s.MeasurementStopped("PC（位相2）", "PC2", "75", As100201LearningSession.StopByAligned);
        s.MeasurementCompleted("PC（位相2）", "PC2", "75");
        Check(ledger.Rows[^1] is { StopTrigger: "合わせた", Side: "PC（位相2）" } && ledger.Rows[^2].StopTrigger == "測り直す" && s.AligningNozzleSide == 'P', "measurements while aligning");
        // 合わせた: 同じ操作の行(備考「合わせた」)。時間は 2 行の日時の差(列は増やさない)。
        Check(s.HandleCommand(doneP) == "ACK:ACT:NOZZLE:P:DONE" && ledger.Rows[^1] is { Operation: "ノズル P", Note: "合わせた" } &&
            s.AligningNozzleSide is null && s.OperationCount == 2 && As100201PanelProtocol.Last(s.UndoTarget).EndsWith(" ノズル P（合わせた）"),
            "aligned row: " + ledger.Rows[^1].Note + " / " + As100201PanelProtocol.Last(s.UndoTarget));
        // 「合わせた」を取り消すと、合わせている状態に戻る(パネルは合わせている画面へ)。
        Check(s.HandleCommand(undo) == "ACK:UNDO" && ledger.Rows[^1] is { Kind: As100201LedgerEventKind.Undo, Operation: "ノズル P" } &&
            ledger.Rows[^1].Note.EndsWith(" ノズル P（合わせた）") && s.AligningNozzleSide == 'P' && s.OperationCount == 1, "undo aligned reopens: " + ledger.Rows[^1].Note);
        Check(s.HandleCommand(doneP) == "ACK:ACT:NOZZLE:P:DONE" && ledger.Rows[^1].Note == "合わせた" && s.AligningNozzleSide is null, "aligned again: " + ledger.Rows[^1].Note);
        // 戻る(押し間違い): 合わせ始めを取り消す行。測っていなければ合わせ始めの前の状態に戻る(次の操作は「直前の測定なし」)。
        Check(s.HandleCommand(As100201PanelCommand.Parse("ACT:NOZZLE:R:START")) == "ACK:ACT:NOZZLE:R:START" && s.AligningNozzleSide == 'R' && s.OperationCount == 3,
            "nozzle R start");
        Check(s.HandleCommand(As100201PanelCommand.Parse("ACT:NOZZLE:P:CANCEL")) == "ACK:ACT:NOZZLE:P:CANCEL" && s.AligningNozzleSide == 'R' &&
            ledger.Rows[^1].Operation == "ノズル R" && ledger.Rows[^1].Kind == As100201LedgerEventKind.Operation, "cancel of the other side changed something");
        Check(s.HandleCommand(As100201PanelCommand.Parse("ACT:NOZZLE:R:CANCEL")) == "ACK:ACT:NOZZLE:R:CANCEL" &&
            ledger.Rows[^1] is { Kind: As100201LedgerEventKind.Undo, Operation: "ノズル R", File: "" } && ledger.Rows[^1].Note.EndsWith(" ノズル R（合わせ始め）（戻る）") &&
            s.AligningNozzleSide is null && s.OperationCount == 2 && s.UndoTarget is null, "nozzle back: " + ledger.Rows[^1].Note);
        Check(s.HandleCommand(As100201PanelCommand.Parse("ACT:MAG")) == "ACK:ACT:MAG" && ledger.Rows[^1].Note == "直前の測定なし", "state after nozzle back: " + ledger.Rows[^1].Note);
        // 「合わせた」が来ないまま別の操作・完了(Tab5 の電源が切れたなど): その行の備考に「ノズル P の合わせた なし」。
        Check(s.HandleCommand(startP) == "ACK:ACT:NOZZLE:P:START" && s.HandleCommand(As100201PanelCommand.Parse("ACT:DEMAG:TAP")) == "ACK:ACT:DEMAG:TAP" &&
            ledger.Rows[^1].Note == "ノズル P の合わせた なし、直前の測定なし" && s.AligningNozzleSide is null, "aligned missing: " + ledger.Rows[^1].Note);
        // 合わせ始めの記録が無い「合わせた」は記録して印を付ける。合わせ始めの無い「戻る」は何も書かない。
        Check(s.HandleCommand(doneP) == "ACK:ACT:NOZZLE:P:DONE" && ledger.Rows[^1].Note == "合わせた、合わせ始め なし、直前の測定なし", "aligned without start: " + ledger.Rows[^1].Note);
        int rows = ledger.Rows.Count;
        Check(s.HandleCommand(As100201PanelCommand.Parse("ACT:NOZZLE:P:CANCEL")) == "ACK:ACT:NOZZLE:P:CANCEL" && ledger.Rows.Count == rows, "back without start wrote a row");
        Check(s.HandleCommand(startP) == "ACK:ACT:NOZZLE:P:START" && s.OperationCount == 7, "counts before done: " + s.OperationCount);
        Check(s.HandleCommand(As100201PanelCommand.Parse("DONE")) == "ACK:DONE" &&
            ledger.Rows[^1] is { Kind: As100201LedgerEventKind.Done } && ledger.Rows[^1].Note == "この個体の測定 3 回・操作 7 回、ノズル P の合わせた なし", "done while aligning: " + ledger.Rows[^1].Note);
        // 台帳からの数え直し(合わせ始め・合わせたはどちらも操作の行、戻るは操作の取消)と、続きにしたら合わせている状態は残らない。
        var history = s.FindSerial("SN001");
        Check(history is { MeasurementCount: 3, OperationCount: 7, Completed: true }, "history with nozzle rows");
        Check(s.SelectSerial("SN001", history) && s.AligningNozzleSide is null, "aligning kept across serial");
        Pass("nozzle (v6): START / DONE rows with notes, measurements inside the work, undo of DONE reopens, CANCEL undoes START (other side ignored), missing DONE / START noted, done while aligning, ledger history");
    }

    // 4. 書けないときの停止と再試行
    private static void WriteFailureChecks()
    {
        var ledger = new FakeLedger();
        var saved = new List<string>();
        var logs = new List<string>();
        long saveResult = 0;
        // 番号の行で時計が 1 回進むので 1 秒前から(そのあとの時刻は今までと同じ)。
        DateTime now = new(2026, 11, 9, 9, 59, 59);
        bool folderFails = false;
        var s = new As100201LearningSession(ledger, path => { if (saveResult != 0) return saveResult; saved.Add(path); return 0; }, () => now = now.AddSeconds(1),
            createDirectory: _ => { if (folderFails) throw new UnauthorizedAccessException("fake folder failure"); })
        { SaveDirectory = @"C:\fake" };
        s.Log += logs.Add;
        StartSerial(s, ledger);
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
        Check(s.Retry() && !s.Blocked && s.PendingCount == 0 && ledger.Rows.Count == 1 && ledger.Rows[0] is { Kind: As100201LedgerEventKind.Measurement, File: @"SN001\20261109_100002_SN001_PC.xdt" } &&
            saved.SequenceEqual(new[] { @"C:\fake\SN001\20261109_100002_SN001_PC.xdt" }), "retry writes pending in order");
        Check(s.HandleCommand(mag) == "ACK:ACT:MAG" && ledger.Rows[^1].Note == "", "resumed after retry");

        // xdt が保存できない(LOGGER の戻り値が 0 以外): 行は書けていて、xdt だけ待つ。再試行で SaveXdt し直す。
        saveResult = -5;
        s.MeasurementStopped("CR", "CR", "140", As100201LearningSession.StopByPanel);
        Check(!s.Blocked && ledger.Rows[^1].Kind == As100201LedgerEventKind.Measurement, "row written before save");
        s.MeasurementCompleted("CR", "CR", "140");
        Check(s.Blocked && s.PendingCount == 1 && s.BlockReason!.Contains("戻り値 -5") && saved.Count == 1, "xdt save failure blocks");
        Check(s.HandleCommand(mag).StartsWith("NAK:"), "operation NAK while xdt pending");
        saveResult = 0;
        Check(s.Retry() && !s.Blocked && saved[^1].EndsWith("20261109_100004_SN001_CR.xdt") && saved.Count == 2, "retry re-saves xdt");
        // 測定の行の書き込みそのものが失敗: 行を覚えて止める。xdt も後ろに並ぶ。再試行で行 → xdt の順。
        ledger.Fail = true;
        s.MeasurementStopped("PC", "PC", "0", As100201LearningSession.StopBySwitch);
        Check(s.Blocked && s.PendingCount == 1, "measurement row failure queued");
        s.MeasurementCompleted("PC", "PC", "0");
        Check(s.PendingCount == 2, "xdt queued behind row");
        ledger.Fail = false;
        int rows = ledger.Rows.Count;
        Check(s.Retry() && ledger.Rows.Count == rows + 1 && saved.Count == 3 && saved[^1].EndsWith(ledger.Rows[^1].File), "row then xdt on retry");
        // 保存先・番号のフォルダーを作れない(2026-11-10): 台帳の行も xdt も書かずに止め、作れるようになったら再試行で書く。
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
        Check(ledger.Rows[^1].ToCsvLine() == "2026-11-12 09:00:02,SN001,押した,CR（2回押し）,250,,,,,,,09:00:02.000、F8 から 12.346 秒、点数 1234（xdt の 1235 点目から後）",
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
        public string HelloReply = "AS100201_PANEL_READY:7";
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

        var screens = new List<As100201PanelScreen>();
        panel.ScreenChanged += screens.Add;

        await panel.VerifyAsync();
        Check(link.Requests.SequenceEqual(new[] { "AS100201_PANEL_PING" }) && panel.IsConnected && !panel.InputReady, "verified but not ready before frames");
        // 画面の位置(版 4・6)は定期応答の安定を待たずに知らせ、返事は送らない(つなぎ直した直後に送られるため)。
        link.Receive("SELECT");
        link.Receive("NOZZLE:R");
        link.Receive("HOME");
        Check(screens.SequenceEqual(new[] { As100201PanelScreen.Selecting, As100201PanelScreen.NozzleR, As100201PanelScreen.Home }) &&
            received.Count == 0 && link.Sent.Count == 0, "screen lines before warm-up");
        // 定期応答が安定する前の操作は捨てて NAK。版 5 の HOLD は知らない行(返事なし)。
        link.Receive("HOLD");
        Check(received.Count == 0 && link.Sent.Count == 0, "HOLD before warm-up answered");
        link.Receive("ACT:MAG");
        Check(received.Count == 0 && link.Sent.Count == 1 && link.Sent[0].StartsWith("NAK:"), "command before warm-up not dropped with NAK");
        uint seq = 1;
        for (int i = 0; i < 6; i++) { link.Receive($"DIAG_FRAME:{seq}:{seq * 250}"); seq++; await Task.Delay(250); }
        Check(panel.InputReady, "not ready after 1 s of frames");
        long version = panel.RequestVersion;
        link.Receive("PHASE:CR1");
        Check(received.Count == 1 && received[0].Line == "PHASE:CR1" && link.Sent.Count == 1, "command while ready not delivered");
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
        link.HelloReply = "AS100201_PANEL_READY:7";
        await Task.Delay(2100);
        await panel.MaintainAsync();
        for (int i = 0; i < 6; i++) { link.Receive($"DIAG_FRAME:{seq}:{seq * 250}"); seq++; await Task.Delay(250); }
        Check(panel.InputReady, "not ready after version fix");
        link.Receive("DIAG_BOOT:1");
        Check(!panel.InputReady, "boot did not reset");

        // スイッチの受け付けの決まり: sw4 だけは途切れ中・保存の停止中でも受け付ける。
        string refusal;
        Check(As100201LearningGate.Allows("sw4", baseReady: true, panelReady: false, hasSession: true, saveBlocked: true, readyLedOn: false, out _), "sw4 refused during outage");
        Check(As100201LearningGate.Allows("sw4", true, true, true, false, false, out _, panelSelecting: true), "sw4 refused while selecting");
        Check(!As100201LearningGate.Allows("sw4", baseReady: false, panelReady: true, hasSession: true, saveBlocked: false, readyLedOn: true, out refusal) && refusal == As100201LearningGate.NotReady, "sw4 without base");
        foreach (string button in new[] { "PC_go", "CR_go", "F8" })
        {
            Check(!As100201LearningGate.Allows(button, true, panelReady: false, true, false, true, out refusal) && refusal == As100201LearningGate.PanelLost, button + " allowed during outage");
            Check(!As100201LearningGate.Allows(button, true, true, hasSession: false, false, true, out refusal) && refusal == As100201LearningGate.NoSaveDirectory, button + " allowed without save directory");
            Check(!As100201LearningGate.Allows(button, true, true, true, saveBlocked: true, true, out refusal) && refusal == As100201LearningGate.SaveBlocked, button + " allowed while blocked");
            Check(!As100201LearningGate.Allows(button, true, true, true, false, readyLedOn: false, out _), button + " allowed with LED off");
            Check(!As100201LearningGate.Allows(button, true, true, true, false, true, out refusal, panelSelecting: true) && refusal == As100201LearningGate.PanelSelecting, button + " allowed while selecting on the panel");
            // シリアル番号が入っていない間は測定を始めない(2026-11-26)。
            Check(!As100201LearningGate.Allows(button, true, true, true, false, true, out refusal, serialMissing: true) && refusal == As100201LearningGate.NoSerial, button + " allowed without a serial");
            Check(As100201LearningGate.Allows(button, true, true, true, false, true, out _), button + " refused when all ready");
        }
        Check(As100201LearningGate.Allows("sw4", true, true, true, false, false, out _, serialMissing: true), "sw4 refused without a serial");
        Pass("panel outage: warm-up 1 s, stale after 1 s -> NAK + status, recovery 1 s, reconnect after drop, version check, DIAG_BOOT reset, SELECT / NOZZLE:R / HOME before warm-up without reply, v5 HOLD ignored; gate lets only sw4 through (also while selecting / without a serial)");
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

        // USB でパネルが見つからないときの文は「Arduino」でなく「タッチパネル」と出す(共通部品の文を言い換える)
        Check(As100201SerialLink.ToPanelMessage("Arduino を確認できません。\nCOM8: FileNotFoundException — x") ==
            "タッチパネルを COM ポートで確認できません。\nCOM8: FileNotFoundException — x" &&
            As100201SerialLink.ToPanelMessage("other") == "other", "USB not-found message not reworded");

        // BATT: の読み取り
        Check(As100201PanelBattery.TryParse("BATT:40:7600:1", out var b) && b.Level == 40 && b.Millivolts == 7600 && b.Charging && b.Present && b.DisplayText == "充電中 40%", "BATT charging");
        Check(As100201PanelBattery.TryParse("BATT:75:7900:0", out b) && b.DisplayText == "電池 75%", "BATT text");
        Check(As100201PanelBattery.TryParse("BATT:-1:0:0", out b) && !b.Present && b.DisplayText == "電池なし", "BATT none");
        // 電池を付けていない Tab5 は残量 0・約 1100mV を送ってきた(2026-11-13 実機)。古いスケッチでも電池なしと出し、充電の催促もしない
        Check(As100201PanelBattery.TryParse("BATT:0:1133:0", out b) && !b.Present && b.DisplayText == "電池なし" &&
            new As100201BatteryWatch().Update(b) is null, "BATT low voltage not treated as no battery");
        Check(As100201PanelBattery.TryParse("BATT:3:5600:0", out b) && b.Present && b.DisplayText == "電池 3%", "BATT nearly empty treated as no battery");
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
        Pass("panel paths: USB preferred, inactive path ignored, failover to Wi-Fi on drop (also via protocol reconnect), stays on Wi-Fi until re-verify then back to USB, both-missing reasons, USB not-found message says panel; BATT lines (below 4000 mV = no battery); low battery log once at 20% (rearm above 30%, not while charging)");
    }

    // 8b. Wi-Fi 中に Tab5 の USB が挿されたら、空いているときに USB へ戻す(2026-11-18 人の決定)
    private static async Task UsbReturnChecks()
    {
        const As100201PanelPath wifi = As100201PanelPath.Wifi;
        const As100201UsbState absent = As100201UsbState.Absent, present = As100201UsbState.Present, ready = As100201UsbState.PortReady;
        var watch = new As100201UsbReturnWatch();
        // 挿さっていない・分からないときは戻さない
        Check(!watch.Update(wifi, absent, true, 0) && !watch.Update(wifi, As100201UsbState.Unknown, true, 100), "returned without USB");
        // COM ポートができたらすぐ戻す(②)。空いていなければ待つ
        Check(!watch.Update(wifi, present, true, 1000) && !watch.Update(wifi, ready, false, 1100), "returned before port / while busy");
        Check(watch.Update(wifi, ready, true, 1200), "not returned as soon as port is ready");
        // 試すのは挿すたびに 1 回。選び直しの途中(道なし)でも覚えたまま
        Check(!watch.Update(wifi, ready, true, 1500) && !watch.Update(As100201PanelPath.None, ready, true, 2000) &&
            !watch.Update(wifi, ready, true, 9000), "retried without replug");
        // 抜いて挿し直せばまた試す
        Check(!watch.Update(wifi, absent, true, 10000) && watch.Update(wifi, ready, true, 10500), "not rearmed by replug");
        // USB でつながったら、次に Wi-Fi に移ったときにまた試せる
        Check(!watch.Update(As100201PanelPath.Usb, ready, true, 14000) && watch.Update(wifi, ready, true, 15000), "not rearmed after USB connected");
        // COM ポートが分からないときだけ、挿されてから 2 秒待つ
        var fallback = new As100201UsbReturnWatch();
        Check(!fallback.Update(wifi, present, true, 0) && !fallback.Update(wifi, present, true, 1999) &&
            fallback.Update(wifi, present, true, 2000), "port-unknown fallback not after settle time");

        // ①: USB では Tab5 の COM ポートだけを、今あるものを 2 回ずつ試す。分からないときは全部
        string[] names = { "COM9", "COM4", "COM8", "COM8" };
        Check(As100201SerialLink.SelectPanelPorts(names, new[] { "COM8" }).SequenceEqual(new[] { "COM8", "COM8" }), "panel port not selected twice");
        Check(As100201SerialLink.SelectPanelPorts(names, new[] { "com8", "COM12" }).SequenceEqual(new[] { "com8", "com8" }), "missing panel port not skipped");
        Check(As100201SerialLink.SelectPanelPorts(names, Array.Empty<string>()).Count == 0, "non-panel ports tried");
        Check(As100201SerialLink.SelectPanelPorts(names, null).SequenceEqual(names), "unknown panel port did not fall back to all");
        // Windows に聞ける(機器は開かない)。無い ID は空
        Check(As100201UsbDevices.FindPortNames(@"USB\VID_FFFF&PID_FFFE&NOSUCH") is { Count: 0 }, "port name query failed");

        // ④: 子機のつなぎ直しで頼み直す間隔。切れた状態なら 1.5 秒、つなごうとしている最中は 4 秒
        Check(As100201WlanAdapterControl.ShouldRequest(-1, TimeSpan.MaxValue), "first request not sent");
        Check(!As100201WlanAdapterControl.ShouldRequest(4, TimeSpan.FromSeconds(1.4)) && As100201WlanAdapterControl.ShouldRequest(4, TimeSpan.FromSeconds(1.5)),
            "disconnected retry not 1.5 s");
        Check(!As100201WlanAdapterControl.ShouldRequest(6, TimeSpan.FromSeconds(3.9)) && As100201WlanAdapterControl.ShouldRequest(6, TimeSpan.FromSeconds(4)),
            "busy retry not 4 s");

        // 見張りと組み合わせ: Wi-Fi で使っている最中に USB が戻ったら、選び直しで USB に移り Wi-Fi を閉じる
        const string ping = As100201PanelProtocol.HelloRequest;
        var usb = new FakePanelLink { Fail = true };
        var wifiLink = new FakePanelLink();
        var paths = new As100201PanelPathLink(usb, wifiLink);
        using (var panel = new As100201PanelProtocol(paths))
        {
            await panel.VerifyAsync();
            Check(paths.ActivePath == wifi && panel.IsConnected, "not on Wi-Fi before return");
            usb.Fail = false;
            Check(await panel.ReselectAsync() && paths.ActivePath == As100201PanelPath.Usb && panel.IsConnected &&
                !wifiLink.IsConnected && usb.Requests.Count == 2 && usb.Requests[^1] == ping, "reselect did not move to USB");
            // USB でも Wi-Fi でも確かめられなければ false で、見張りの再接続に任せる
            usb.Fail = wifiLink.Fail = true;
            Check(!await panel.ReselectAsync() && paths.ActivePath == As100201PanelPath.None && !panel.IsConnected, "failed reselect reported as connected");
            usb.Fail = wifiLink.Fail = false;
            await Task.Delay(2100);
            await panel.MaintainAsync();
            Check(paths.ActivePath == As100201PanelPath.Usb && panel.IsConnected, "maintain did not reconnect after failed reselect");
        }
        Pass("USB return: as soon as the panel COM port exists and idle (2 s only when the port is unknown), tries once per plug-in (rearmed by replug or USB connection); only the panel COM port, twice; Wi-Fi adapter retry 1.5 s when disconnected / 4 s while connecting; reselect moves Wi-Fi to USB and closes Wi-Fi, failure left to maintain");
    }

    // 9. Wi-Fi の子機のつなぎ直し(2026-11-17 人の決定 B)。2 回続けて失敗したら裏でつなぎ直し、終わったあとの確認で Wi-Fi に移る。30 秒に 1 回まで
    private static async Task WifiRefreshChecks()
    {
        const string ping = As100201PanelProtocol.HelloRequest;
        static bool IsHello(string line) => line.StartsWith(As100201PanelProtocol.HelloPrefix, StringComparison.Ordinal);
        static async Task<string?> Failure(Func<Task> action)
        {
            try { await action(); return null; } catch (InvalidOperationException ex) { return ex.Message; }
        }
        var wifi = new FakePanelLink { Fail = true };
        var adapter = new FakeWifiAdapter();
        long now = 0;
        var notices = new List<string>();
        using var link = new As100201WifiRefreshLink(wifi, adapter, "AS100201_PANEL", () => now, TimeSpan.FromMilliseconds(50));
        link.Notice += notices.Add;

        // 1 回目の失敗ではつなぎ直さない
        Check(await Failure(() => link.VerifyAsync(ping, IsHello)) is not null && adapter.Calls == 0, "refreshed after first failure");
        // 2 回目: 失敗を返し、裏でつなぎ直しを始める(子機が古いつながりから戻る)
        adapter.OnReconnect = () => wifi.Fail = false;
        now = 5000;
        string? second = await Failure(() => link.VerifyAsync(ping, IsHello));
        await link.RefreshTask;
        Check(second?.Contains("つなぎ直します") == true && adapter.Calls == 1 && adapter.Profiles.SequenceEqual(new[] { "AS100201_PANEL" }), "refresh not started on second failure: " + second);
        Check(notices.Count == 1 && notices[0].Contains("つなぎ直しました") && notices[0].Contains("FAKE-ADAPTER"), "refresh not reported");
        // 終わったあとの確認で通る
        Check(await Failure(() => link.VerifyAsync(ping, IsHello)) is null && wifi.Requests.Count == 3 && link.IsConnected, "not connected after refresh");
        // つなぎ直している間の確認は、Wi-Fi を試さずにすぐ失敗にする
        wifi.Fail = true;
        adapter.OnReconnect = null;
        now = 60000;
        Check(await Failure(() => link.VerifyAsync(ping, IsHello)) is not null && adapter.Calls == 1, "count not reset by success");
        adapter.Hold = new TaskCompletionSource();
        now = 65000;
        await Failure(() => link.VerifyAsync(ping, IsHello));
        int requests = wifi.Requests.Count;
        string? busy = await Failure(() => link.VerifyAsync(ping, IsHello));
        Check(adapter.Calls == 2 && busy?.Contains("つなぎ直しています") == true && wifi.Requests.Count == requests, "verify not short-circuited while refreshing: " + busy);
        adapter.Hold.SetResult();
        await link.RefreshTask;
        adapter.Hold = null;
        // 30 秒以内は何回失敗してもつなぎ直さない(Tab5 が無いとき繰り返さない)
        now = 70000;
        Check(await Failure(() => link.VerifyAsync(ping, IsHello)) is not null && await Failure(() => link.VerifyAsync(ping, IsHello)) is not null &&
            await Failure(() => link.VerifyAsync(ping, IsHello)) is not null && adapter.Calls == 2, "refreshed within 30 s");
        // 30 秒を過ぎたらまたつなぎ直す
        now = 95001;
        await Failure(() => link.VerifyAsync(ping, IsHello));
        await link.RefreshTask;
        Check(adapter.Calls == 3, "no refresh after 30 s");
        // つなぎ直せなかったら、その理由を出す(例外にしない)
        adapter.Error = "子機が見つかりません";
        now = 200000;
        await Failure(() => link.VerifyAsync(ping, IsHello));
        await Failure(() => link.VerifyAsync(ping, IsHello));
        await link.RefreshTask;
        Check(adapter.Calls == 4 && notices[^1].Contains("つなぎ直せませんでした") && notices[^1].Contains("子機が見つかりません"), "refresh failure not reported: " + notices[^1]);
        // 行・切断はそのまま流す
        var lines = new List<string>();
        int dropped = 0;
        link.LineReceived += lines.Add;
        link.Disconnected += () => dropped++;
        wifi.Receive("DIAG_FRAME:1:250");
        wifi.Drop();
        Check(lines.SequenceEqual(new[] { "DIAG_FRAME:1:250" }) && dropped == 1, "lines / drop not forwarded");

        // 使っていた USB が切れたとき(RefreshNow、人の決定 C): Wi-Fi の失敗を待たずにすぐつなぎ直す。
        // 確認はつなぎ直しの終わりを待ち(最大 waitForRefresh)、終われば同じ確認の中で TCP につなぐ
        var wifi3 = new FakePanelLink { Fail = true };
        var adapter3 = new FakeWifiAdapter { Hold = new TaskCompletionSource(), OnReconnect = () => wifi3.Fail = false };
        var notices3 = new List<string>();
        using (var link3 = new As100201WifiRefreshLink(wifi3, adapter3, "AS100201_PANEL", () => 0, TimeSpan.FromSeconds(5)))
        {
            link3.Notice += notices3.Add;
            link3.RefreshNow("使っていた USB が切れたため");
            link3.RefreshNow("使っていた USB が切れたため");   // 進めている最中は増やさない
            var verify = link3.VerifyAsync(ping, IsHello);
            await Task.Delay(50);
            Check(!verify.IsCompleted && wifi3.Requests.Count == 0 && adapter3.Calls == 1, "verify did not wait for refresh");
            adapter3.Hold.SetResult();
            Check(await Failure(() => verify) is null && adapter3.Calls == 1 && wifi3.Requests.Count == 1 &&
                notices3.Count == 1 && notices3[0].StartsWith("使っていた USB が切れたため"), "verify not connected right after refresh");
        }

        // 道が切れたことの知らせ(PathLost): 使っていた道が切れたときだけ。アプリが閉じたとき・使っていない道では知らせない
        var usb4 = new FakePanelLink();
        var wifi4 = new FakePanelLink();
        var lost = new List<As100201PanelPath>();
        using (var paths4 = new As100201PanelPathLink(usb4, wifi4))
        {
            paths4.PathLost += lost.Add;
            await paths4.VerifyAsync(ping, IsHello);
            await paths4.DisconnectAsync();
            await paths4.VerifyAsync(ping, IsHello);
            wifi4.Drop();
            usb4.Drop();
            Check(lost.SequenceEqual(new[] { As100201PanelPath.Usb }), "PathLost not only for the active path");
            // 見張りが先に閉じて切断の知らせが届かなかったとき(実機であった): USB の確認が失敗した時点で 1 回だけ知らせる
            await paths4.VerifyAsync(ping, IsHello);
            await paths4.DisconnectAsync();
            usb4.Fail = true;
            await paths4.VerifyAsync(ping, IsHello);   // Wi-Fi でつながる
            usb4.Fail = false;
            await paths4.VerifyAsync(ping, IsHello);   // USB に戻る
            await paths4.DisconnectAsync();
            usb4.Fail = wifi4.Fail = true;
            try { await paths4.VerifyAsync(ping, IsHello); } catch (InvalidOperationException) { }
            try { await paths4.VerifyAsync(ping, IsHello); } catch (InvalidOperationException) { }
            Check(lost.SequenceEqual(new[] { As100201PanelPath.Usb, As100201PanelPath.Usb, As100201PanelPath.Usb }),
                "PathLost not reported once on failed verify of the last path: " + string.Join(",", lost));
        }

        // 挿さっている USB 機器を Windows に聞ける(機器は開かない)。無い ID は false
        Check(As100201UsbDevices.IsPresent(@"USB\VID_FFFF&PID_FFFE&NOSUCH") == false, "USB device query failed");

        // 見張りと組み合わせ(人の決定 C): USB で使っていて Wi-Fi が古いつながりのとき、USB が切れたら 1 回の再接続で Wi-Fi に移る
        var usb5 = new FakePanelLink();
        var wifi5 = new FakePanelLink { Fail = true };
        var adapter5 = new FakeWifiAdapter { OnReconnect = () => wifi5.Fail = false };
        var refresh5 = new As100201WifiRefreshLink(wifi5, adapter5, "AS100201_PANEL", () => 0);
        var paths5 = new As100201PanelPathLink(usb5, refresh5);
        paths5.PathLost += path => { if (path == As100201PanelPath.Usb) refresh5.RefreshNow("使っていた USB が切れたため"); };
        using (var panel5 = new As100201PanelProtocol(paths5))
        {
            await panel5.VerifyAsync();
            Check(paths5.ActivePath == As100201PanelPath.Usb && adapter5.Calls == 0, "refreshed while on USB");
            usb5.Fail = true;
            usb5.Drop();
            await panel5.MaintainAsync();
            Check(adapter5.Calls == 1 && paths5.ActivePath == As100201PanelPath.Wifi && panel5.IsConnected && wifi5.Requests.Count == 1,
                "USB drop did not move to Wi-Fi in one reconnect");
        }

        // 見張り(As100201PanelProtocol)と組み合わせ: USB が無く Wi-Fi が古いつながりのとき、2 回目でつなぎ直し、次の確認で Wi-Fi に移る
        var usb2 = new FakePanelLink { Fail = true };
        var wifi2 = new FakePanelLink { Fail = true };
        var adapter2 = new FakeWifiAdapter { OnReconnect = () => wifi2.Fail = false };
        var refresh2 = new As100201WifiRefreshLink(wifi2, adapter2, "AS100201_PANEL", () => 0);
        var paths2 = new As100201PanelPathLink(usb2, refresh2);
        using (var panel = new As100201PanelProtocol(paths2))
        {
            Check(await Failure(() => panel.VerifyAsync()) is not null && adapter2.Calls == 0, "protocol refreshed on first failure");
            Check(await Failure(() => panel.VerifyAsync()) is not null, "protocol second verify passed");
            await refresh2.RefreshTask;
            Check(adapter2.Calls == 1, "protocol did not start refresh");
            await panel.VerifyAsync();
            Check(paths2.ActivePath == As100201PanelPath.Wifi && panel.IsConnected, "protocol not on Wi-Fi after refresh");
        }
        Pass("Wi-Fi refresh: not on first failure, background refresh on second (verify waits briefly, then short-circuits), connects on next verify, count reset by success, at most once per 30 s, failure reason reported without exception, lines/drop forwarded; on USB drop refresh at once and connect in the same verify; PathLost only for the active path; works through path link and protocol");
    }

    private sealed class FakeWifiAdapter : IAs100201WifiAdapterControl
    {
        public int Calls;
        public List<string> Profiles { get; } = new();
        public Action? OnReconnect;
        public string? Error;
        public TaskCompletionSource? Hold;
        public async Task<As100201WifiRefreshResult> ReconnectAsync(string profileName, CancellationToken cancellationToken = default)
        {
            Calls++;
            Profiles.Add(profileName);
            if (Hold is not null) await Hold.Task;
            if (Error is not null) return new(false, Error);
            OnReconnect?.Invoke();
            return new(true, "FAKE-ADAPTER");
        }
    }

    // 6. 通常モードを選んだとき今までと同じ
    private static void NormalModeChecks()
    {
        Check(default(As100201StartupMode) == As100201StartupMode.Normal, "default mode is not Normal");
        using (var dialog = new StartupModeDialog())
            Check(dialog.SelectedMode == As100201StartupMode.Normal && FindControls(dialog).OfType<RadioButton>().Count() == 2, "dialog initial selection is not Normal (or the developer item is still shown)");
        Check(As100201Application.GetWindowTitle(As100201StartupMode.Normal) == As100201Application.WindowTitle &&
            As100201Application.WindowTitle == "AS100-201 スイッチ制御" &&
            As100201Application.GetWindowTitle(As100201StartupMode.Learning).Contains("学習モード"), "window titles");
        // 2026-11-26 から、どちらのモードも画面上部に「次にすること」が 1 つ増え、今までの画面はスクロールの枠に入った。
        using (var normal = new MainForm(As100201StartupMode.Normal))
        {
            Check(!normal.IsLearningMode && normal.Text == As100201Application.WindowTitle, "normal form marked as learning");
            Check(normal.Controls.Count == 2, "normal form gained learning controls");
            Check(FindLabels(normal).All(l => !l.Text.Contains("学習モード")), "learning text shown in normal mode");
            // 版 6(2026-11-27): 通常モードでも Tab5 を使うので、「マイコン」の行は「タッチパネル」の行になった。
            Check(FindLabels(normal).Any(l => l.Text == "タッチパネル：") && FindLabels(normal).All(l => l.Text != "マイコン："), "normal form panel row");
            Check(FindControls(normal).OfType<Button>().Any(b => b.Text == "測定停止"), "normal form has no stop button");
            Check(FindLabels(normal).Any(l => l.Text.StartsWith(As100201NextStep.Prefix)), "normal form has no next step");
        }
        using (var learning = new MainForm(As100201StartupMode.Learning))
        {
            Check(learning.IsLearningMode && learning.Text == As100201Application.LearningWindowTitle, "learning form title");
            Check(learning.Controls.Count == 5 && FindLabels(learning).Any(l => l.Text.StartsWith("学習モード")) && FindLabels(learning).Any(l => l.Text == "タッチパネル："), "learning controls missing");
            // 帯のすぐ下に「次にすること」。起動直後(接続前)は赤で直し方を出す。
            var next = FindLabels(learning).Single(l => l.Text.StartsWith(As100201NextStep.Prefix));
            Check(learning.Controls.IndexOf(next) == learning.Controls.Count - 2, "next step is not right below the banner");
            // シリアル番号の入力欄が記録の状態の枠の中に見えている(2026-11-26: 表を Fill にしていて枠が伸びず、欄が枠の外に切れていた)。
            learning.ClientSize = new Size(850, 820);
            learning.CreateControl();
            learning.PerformLayout();
            var serialBox = FindControls(learning).OfType<TextBox>().Single(t => t.MaxLength == 8);
            // 窓を出さないので画面の座標は使わず、親までの位置を足していく。
            Control group = serialBox;
            var inGroup = new Rectangle(Point.Empty, serialBox.Size);
            while (group is not GroupBox)
            {
                inGroup.Offset(group.Location);
                group = group.Parent!;
            }
            Check(group.ClientRectangle.Contains(inGroup), $"serial box is cut off by the group ({inGroup} / {group.ClientRectangle})");
            // 入りきらない分は縦にだけスクロールし、ログには最低の高さを残す。
            var viewport = FindControls(learning).OfType<Panel>().Single(p => p.AutoScroll && p.Dock == DockStyle.Fill);
            var log = FindControls(viewport).OfType<TextBox>().Single(t => t.Multiline);
            Check(!viewport.HorizontalScroll.Visible && viewport.Controls[0].Width == viewport.ClientSize.Width && log.Height >= 150,
                $"scroll layout (horizontal {viewport.HorizontalScroll.Visible}, width {viewport.Controls[0].Width}/{viewport.ClientSize.Width}, log {log.Height})");
            if (Environment.GetEnvironmentVariable("AS100201_SNAPSHOT") is string snapshot && snapshot.Length > 0)
            {
                // 画面の見た目を画像に出す(開発用。窓は出さないので、並べた部品を 1 つずつ描く)。
                using var bitmap = new Bitmap(learning.ClientSize.Width, learning.ClientSize.Height);
                void Draw(Control parent, Point origin, Rectangle clip)
                {
                    for (int i = parent.Controls.Count - 1; i >= 0; i--)
                    {
                        Control part = parent.Controls[i];
                        var at = new Rectangle(origin.X + part.Left, origin.Y + part.Top, part.Width, part.Height);
                        var visible = Rectangle.Intersect(at, clip);
                        if (visible.Width <= 0 || visible.Height <= 0) continue;
                        using (var piece = new Bitmap(part.Width, part.Height))
                        {
                            part.DrawToBitmap(piece, new Rectangle(Point.Empty, part.Size));
                            using var g = Graphics.FromImage(bitmap);
                            g.SetClip(visible);
                            g.DrawImage(piece, at.Location);
                        }
                        var inner = part.DisplayRectangle;
                        Draw(part, new Point(at.X + (part is ScrollableControl ? inner.X : 0), at.Y + (part is ScrollableControl ? inner.Y : 0)),
                            Rectangle.Intersect(visible, new Rectangle(at.Location, part.ClientSize)));
                    }
                }
                Draw(learning, Point.Empty, new Rectangle(Point.Empty, learning.ClientSize));
                bitmap.Save(snapshot);
            }
        }
        // 異常ログ: 通常モードの異常種別はそのまま。パネルの種別は学習モードだけで起きる。
        foreach (var kind in new[] { "HEARTBEAT_STALE", "FRAME_GAP", "PROTOCOL_RECOVERY", "PANEL_HEARTBEAT_STALE", "PANEL_FRAME_GAP", "PANEL_RECOVERY" })
            Check(PSCAutomation.Wrappers.Arduino.ArduinoDiagnosticLog.IsIncident(kind), "incident kind missing: " + kind);
        Pass("normal mode: default/× -> Normal, same title, no learning controls or text, panel row and stop button (v6); learning mode adds banner and title");
    }

    // 11. 画面上部の「次にすること」(2026-11-26、人の要望)。準備完了 LED が消えている理由のうち、先に片付けるものを 1 つ出す
    private static void NextStepChecks()
    {
        var ready = new As100201NextStepState
        {
            Learning = true, LoggerReady = true, Wf1973Ready = true, TemplateLoaded = true,
            PanelReady = true, ReferenceLoaded = true, ReadyToMeasure = true
        };
        (string Text, As100201NextStepKind Kind) Next(As100201NextStepState s) => As100201NextStep.Describe(s);

        // 版 6(2026-11-27): Arduino をなくした。始め方はタッチパネルの PC / CR、学習モードは手を動かす前に「バルブを動かす」。
        Check(Next(ready) is { Kind: As100201NextStepKind.Ready } r && r.Text.Contains("PC / CR") && r.Text.Contains("バルブを動かす") && !r.Text.Contains("F8"), "learning ready");
        Check(Next(ready with { Learning = false }) is { Kind: As100201NextStepKind.Ready } n && n.Text.Contains("PC / CR") && !n.Text.Contains("バルブ"), "normal ready");
        // 今回の人の画面: 接続も雛形もそろい、番号だけが無い → 番号を入れるよう出す
        Check(Next(ready with { SerialMissing = true, ReadyToMeasure = false }) is { Kind: As100201NextStepKind.Action } s && s.Text.Contains("シリアル番号"), "serial missing");
        // 片付ける順: 接続確認中 → LOGGER → 発振器 → パネル → 保存 → 雛形 → 番号の確認の窓 → 番号 → 選んでいる途中 → 処理中
        var all = ready with
        {
            LoggerReady = false, Wf1973Ready = false, PanelReady = false, SaveBlocked = true, TemplateLoaded = false,
            ReferenceLoaded = false, ReferenceProblem = "雛形フォルダーに参照波形（.xyb）がありません", SerialDialogOpen = true, SerialMissing = true, PanelSelecting = true, Busy = true, ReadyToMeasure = false
        };
        Check(Next(all with { CheckingConnections = true }).Kind == As100201NextStepKind.Wait, "checking connections");
        Check(Next(all with { LoggerAppMissing = true }).Text.Contains("WAVE LOGGER がこの PC に入っていません"), "logger app missing");
        string[] expected = ["NR-500", "WF1973", "Tab5", "再試行", "雛形", "参照波形を読み込めません", "参照波形を読み込んでいます", "確認の窓", "シリアル番号を入れて", "戻る", "処理中"];
        var state = all;
        foreach (string word in expected)
        {
            var (text, kind) = Next(state);
            Check(text.Contains(word), $"expected '{word}' but got '{text}'");
            Check(kind == (word is "処理中" or "参照波形を読み込んでいます" ? As100201NextStepKind.Wait : As100201NextStepKind.Action), "kind for " + word);
            state = word switch
            {
                "NR-500" => state with { LoggerReady = true },
                "WF1973" => state with { Wf1973Ready = true },
                "Tab5" => state with { PanelReady = true },
                "再試行" => state with { SaveBlocked = false },
                "雛形" => state with { TemplateLoaded = true },
                "参照波形を読み込めません" => state with { ReferenceProblem = null },
                "参照波形を読み込んでいます" => state with { ReferenceLoaded = true },
                "確認の窓" => state with { SerialDialogOpen = false },
                "シリアル番号を入れて" => state with { SerialMissing = false },
                "戻る" => state with { PanelSelecting = false },
                _ => state with { Busy = false }
            };
        }
        Check(Next(state).Kind == As100201NextStepKind.Wait && Next(state).Text.Contains("測定の準備"), "not ready yet");
        // 通常モードでは学習モードだけの理由(保存・番号・選んでいる途中)を出さない。タッチパネルは通常モードでも要る(版 6)。
        var normalBlocked = all with { Learning = false, LoggerReady = true, Wf1973Ready = true, TemplateLoaded = true, Busy = false };
        Check(Next(normalBlocked).Kind == As100201NextStepKind.Action && Next(normalBlocked).Text.Contains("Tab5"), "normal mode without the panel");
        Check(Next(normalBlocked with { PanelReady = true }).Kind == As100201NextStepKind.Wait && Next(normalBlocked with { PanelReady = true }).Text.Contains("測定の準備"),
            "normal mode shows learning reasons");
        // 測定中は止め方(学習モードはバルブを動かす、通常モードは測定停止)
        Check(Next(all with { Measuring = true }) is { Kind: As100201NextStepKind.Ready } m && m.Text.Contains("バルブを動かす"), "measuring");
        Check(Next(all with { Measuring = true, Learning = false }).Text.Contains("測定停止"), "measuring in normal mode");
        Pass("next step: one reason at a time in fixing order (connections, panel, save, template, serial, selecting), measuring shows how to stop, normal mode needs the panel but has no learning reasons");
    }

    private static IEnumerable<Control> FindControls(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in FindControls(child)) yield return nested;
        }
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
