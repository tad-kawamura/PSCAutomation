using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using PSCAutomation.Wrappers.Instruments;

// 実 MainForm で漏れ試験の本体(RunLeakCheckAsync)を通す(2026-11-13、全体見直し・区切り 2。機器・Excel なし)。
// 発振器と LOGGER は模擬に差し替える(Tests/AppShutdown と同じやり方)。M5Stack は無いので、試験の後の「通信を確認できません」で終わる経路まで。
// 合格で終わって「合格」に進む経路は M5Stack が要るので、ここでは通せない(実機で確かめる)。
// 本体を直接呼ぶので、エラーのポップアップ(RunOperationAsync が出す)は出ない。設定ファイルは書き換えない(欄と設定を同じ値にしておく)。
internal static class LeakCheckFormTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    // 雛形の圧力 CH2 の換算(-0.001 V = 0 kPa、5.524 V = 540 kPa)で 540 kPa と 528 kPa になる電圧。
    private const float Raw540 = 5.524f, Raw528 = 5.40f;

    public static void Run(Assembly assembly)
    {
        OverrideDialog(assembly);
        var type = assembly.GetType("PSCAutomation.Apps.AS110AllCheck.MainForm")!;
        var form = (Form)Activator.CreateInstance(type)!;
        object Field(string name) => type.GetField(name, Private)!.GetValue(form)!;
        object? FieldOrNull(string name) => type.GetField(name, Private)!.GetValue(form);
        void SetField(string name, object? value) => type.GetField(name, Private)!.SetValue(form, value);
        ((System.Windows.Forms.Timer)Field("_statusHeartbeat")).Stop();
        var generator = new LeakFakeGenerator();
        var logger = new LeakFakeLogger();
        SetField("_generator", generator);
        object session = Field("_logger");
        session.GetType().GetField("_application", Private)!.SetValue(session, logger);
        _ = form.Handle;

        string root = Path.Combine(Path.GetTempPath(), "AS110AllCheck-leakform-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string record = Path.Combine(root, "漏れ試験記録.txt");
        string template = Path.Combine(AppContext.BaseDirectory, "雛形", "AS110データ調整・データ取り用.xdt");
        if (!File.Exists(template)) throw new Exception("LOGGER template not found: " + template);
        try
        {
            // データ取りで一式保存まで済んだ状態にする。
            var flow = Field("_flow"); var flowType = flow.GetType();
            object? FlowValue(string name) => flowType.GetProperty(name)!.GetValue(flow);
            flowType.GetProperty("Mode")!.SetValue(flow, Enum.Parse(assembly.GetType("PSCAutomation.Apps.AS110AllCheck.TestMode")!, "Production"));
            object product = ((Array)assembly.GetType("PSCAutomation.Apps.AS110AllCheck.ProductModel")!.GetField("All")!.GetValue(null)!).GetValue(0)!;
            flowType.GetMethod("Configure")!.Invoke(flow, ["123", product]);
            flowType.GetMethod("BeginMeasurement")!.Invoke(flow, [DateTime.Now]);
            foreach (string step in new[] { "CaptureSucceeded", "TransferSucceeded", "SaveSucceeded" }) flowType.GetMethod(step)!.Invoke(flow, null);
            SetField("_reviewStage", true);
            SetField("_workingTemplatePath", template);
            ((TextBox)Field("_rootBox")).Text = root;
            // 欄と設定(メモリー上)を同じ値にする。違うと「前回の値」として設定ファイルへ保存されるため。
            object settings = Field("_transferSettings");
            void Setting(string name, object value) => settings.GetType().GetProperty(name)!.SetValue(settings, value);
            void Seconds(int seconds) { Setting("LeakTestSeconds", seconds); ((TextBox)Field("_leakSecondsBox")).Text = seconds.ToString(); }
            Setting("PressureChannel", 2); Setting("ExpectedSamples", 6500);
            Setting("LeakThresholdKpa", 1.0); ((TextBox)Field("_leakThresholdBox")).Text = "1.0";
            Setting("LeakFailDisplaySeconds", 0.5); ((TextBox)Field("_leakFailDisplayBox")).Text = "0.5";
            ((Dictionary<string, double>)settings.GetType().GetProperty("LeakMaximumLowerLimitKpa")!.GetValue(settings)!)["AS110-035"] = 530.0;
            type.GetMethod("RefreshLeakLowerLimitBox", Private)!.Invoke(form, null);
            ((TextBox)Field("_leakLowerLimitBox")).Text = "530.0";
            Seconds(1);

            // 本体を呼び、終わるまで画面のメッセージを回す。例外は返す。during は試験中に 1 回だけ呼ぶ。
            Exception? RunLeak(Action? during = null)
            {
                SetField("_leakOutputReady", true);
                var task = (Task<string>)type.GetMethod("RunLeakCheckAsync", Private)!.Invoke(form, null)!;
                var watch = Stopwatch.StartNew();
                while (!task.IsCompleted)
                {
                    Application.DoEvents();
                    if (during is not null && (bool)Field("_leakRunning") && logger.Running && watch.ElapsedMilliseconds >= 200) { during(); during = null; }
                    if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Leak check did not finish");
                    Thread.Sleep(2);
                }
                if ((bool)Field("_leakRunning") || FieldOrNull("_leakCancel") is not null) throw new Exception("Leak check left the running state behind");
                return task.Exception?.InnerException;
            }
            string[] Lines() => File.Exists(record) ? File.ReadAllLines(record, System.Text.Encoding.UTF8) : [];
            void NotPassed(string scenario)
            {
                if ((bool)FlowValue("CanComplete")! || FlowValue("LastLeakOutcome") is not null) throw new Exception(scenario + ": an interrupted leak test must not allow completion");
            }

            // 1. 記録をほかで開いている: 試験を始めない(LOGGER を始めず、+30mA の出力はそのまま)。
            File.WriteAllBytes(record, []);
            using (var other = new FileStream(record, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var error = RunLeak();
                if (error is not IOException || !error.Message.Contains("漏れ試験は始めていません") || !error.Message.Contains("開いていたら閉じてください"))
                    throw new Exception("A record file opened elsewhere must stop the test before it starts: " + error);
                if (logger.Starts != 0 || generator.Stops != 0 || !(bool)Field("_leakOutputReady")) throw new Exception("A blocked start must not touch the LOGGER or the output");
            }
            if (Lines().Length != 0) throw new Exception("A blocked start must not write a record line");
            Console.WriteLine("PASS leak check: a record file opened elsewhere stops the test before it starts (LOGGER not started, output kept)");

            // 2. 試験秒数が LOGGER の収集の長さ(13 秒)を超える: 試験を始めない。NR-X(ydt。雛形を読めないので測定点数 × 2 ms)も同じ。
            Seconds(14);
            var tooLong = RunLeak();
            if (tooLong is not InvalidOperationException || !tooLong.Message.Contains("13 秒までにしてください") || !tooLong.Message.Contains("漏れ試験は始めていません") || logger.Starts != 0)
                throw new Exception("Test seconds over the template length must stop the test before it starts: " + tooLong);
            SetField("_workingTemplatePath", Path.ChangeExtension(template, ".ydt"));
            var tooLongNrx = RunLeak();
            if (tooLongNrx is not InvalidOperationException || !tooLongNrx.Message.Contains("13 秒までにしてください") || logger.Starts != 0)
                throw new Exception("NR-X: test seconds over the template length must stop the test before it starts: " + tooLongNrx);
            SetField("_workingTemplatePath", template);
            Seconds(1);
            Console.WriteLine("PASS leak check: test seconds over the LOGGER template length (13 s) are rejected before the start, for xdt and NR-X");

            // 3. 最後まで測ったが、通信を確かめられない(M5Stack なし): 出力 OFF・LOGGER 停止をして、記録に「中断」を 1 行残す。合格にはできない。
            logger.Value = _ => Raw540;
            var unconfirmed = RunLeak();
            if (unconfirmed is not IOException || !unconfirmed.Message.Contains("通信を確認できません")) throw new Exception("Without M5Stack the test must end with the communication error: " + unconfirmed);
            if (logger.Starts != 1 || logger.Stops != 1 || generator.Stops != 1 || (bool)Field("_leakOutputReady")) throw new Exception("The test must stop the output and the LOGGER once");
            NotPassed("unconfirmed");
            var lines = Lines();
            string[] aborted = lines.Length == 2 ? lines[1].Split('\t') : [];
            if (lines.Length != 2 || !lines[0].StartsWith("日時\t") || aborted.Length != 12 || aborted[1] != "123" || aborted[2] != "AS110-035" || aborted[3] != "中断"
                || aborted[6] != "540.000" || int.Parse(aborted[9]) < 100 || !aborted[10].Contains("通信を確認できません") || aborted[11] != "530.0")
                throw new Exception("An interrupted test must leave one 中断 line with the values read so far: " + string.Join(" | ", lines));
            Console.WriteLine($"PASS leak check: a test that ends without confirmed communication stops output and LOGGER and is recorded as 中断 ({aborted[9]} points read)");

            // 4. 不合格が決まった後で通信を確かめられない: 記録には「不合格」を残す(以前は記録に残らなかった)。
            // 3 秒の試験が、0.2 秒で下がった値で打ち切られること(判定画面の描き替えとログの分、止まるまでに少しかかる)。
            Seconds(3);
            logger.Value = t => t < 0.2 ? Raw540 : Raw528;
            var watchFail = Stopwatch.StartNew();
            var afterFail = RunLeak();
            long failMilliseconds = watchFail.ElapsedMilliseconds;
            lines = Lines();
            if (afterFail is not IOException || failMilliseconds > 2000) throw new Exception($"A failure must cut the 3-second test short ({failMilliseconds} ms): {string.Join(" | ", lines)} / " + afterFail);
            NotPassed("failed");
            Seconds(1);
            string[] failed = lines.Length == 3 ? lines[2].Split('\t') : [];
            if (lines.Length != 3 || failed[3] != "不合格" || !failed[10].Contains("しきい値") || !failed[10].Contains("判定のあとで異常：通信を確認できません") || generator.Stops != 2)
                throw new Exception("A decided failure must be recorded even when the test ends abnormally: " + string.Join(" | ", lines));
            Console.WriteLine($"PASS leak check: a decided failure cuts the 3-second test short ({failMilliseconds} ms) and, followed by an error, is still recorded as 不合格 with the later error in the note");

            // 5. 試験中に M5Stack の通信が切れたときと同じ中断(終了要求ではない): 分かる文で知らせ、記録に「中断」を残す(以前は「A task was canceled.」)。
            logger.Value = _ => Raw540;
            var canceled = RunLeak(() => ((CancellationTokenSource)Field("_leakCancel")).Cancel());
            if (canceled is not IOException || !canceled.Message.Contains("M5Stack の通信が途切れたため、漏れ試験を中断しました") || canceled.Message.Contains("canceled"))
                throw new Exception("A cancellation that is not a close request must be explained: " + canceled);
            NotPassed("canceled");
            lines = Lines();
            if (lines.Length != 4 || lines[3].Split('\t')[3] != "中断" || !lines[3].Split('\t')[10].Contains("M5Stack の通信が途切れた") || generator.Stops != 3 || logger.Stops != 3)
                throw new Exception("A canceled test must stop both and leave a 中断 line: " + string.Join(" | ", lines));
            Console.WriteLine("PASS leak check: a cancellation during the test (M5Stack lost) stops output and LOGGER, shows a clear message and is recorded as 中断");

            // 6. NR-X(自動判定なし): 途中で終わったら「中断」を残し、値と点数は「-」。
            SetField("_workingTemplatePath", Path.ChangeExtension(template, ".ydt"));
            var nrx = RunLeak();
            lines = Lines();
            string[] nrxLine = lines.Length == 5 ? lines[4].Split('\t') : [];
            if (nrx is not IOException || lines.Length != 5 || nrxLine[3] != "中断" || nrxLine[6] != "-" || nrxLine[9] != "-" || !nrxLine[10].StartsWith("NR-X は自動判定なし / "))
                throw new Exception("NR-X: an interrupted test must leave a 中断 line without values: " + string.Join(" | ", lines));
            NotPassed("NR-X");
            Console.WriteLine("PASS leak check: NR-X (no automatic judgment) records an interrupted test as 中断 without values");
            // 判定画面(不合格の表示 0.5 秒)が閉じるのを待つ。
            var settle = Stopwatch.StartNew();
            while (settle.ElapsedMilliseconds < 800) { Application.DoEvents(); Thread.Sleep(10); }
        }
        finally
        {
            // 模擬の発振器と LOGGER のまま終了処理を通して閉じる(先に起動を始めた LOGGER の参照もここで手放す)。
            form.Close();
            var closing = Stopwatch.StartNew();
            while (!form.IsDisposed && closing.Elapsed < TimeSpan.FromSeconds(10)) { Application.DoEvents(); Thread.Sleep(5); }
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
        if (!form.IsDisposed) throw new Exception("Leak check form did not close");
    }

    // 「人の判断で合格」の確認画面: 既定は「いいえ」(Enter・Esc で合格にならない)。画像も残す。
    private static void OverrideDialog(Assembly assembly)
    {
        var dialogType = assembly.GetType("PSCAutomation.Apps.AS110AllCheck.LeakOverrideDialog")!;
        string message = "漏れ判定は不合格です。\r\n不合格（差 1.114 kPa がしきい値 1.0 kPa を超えました）\r\n\r\nそれでも合格にしますか？\r\n「はい」：人の判断で合格にする（漏れ試験記録に残します）\r\n「いいえ」：合格にしない（「+30mA」から漏れ試験をやり直せます）";
        using var dialog = (Form)Activator.CreateInstance(dialogType, message, SystemFonts.DefaultFont)!;
        var yes = (Button)dialogType.GetProperty("YesButton")!.GetValue(dialog)!;
        var no = (Button)dialogType.GetProperty("NoButton")!.GetValue(dialog)!;
        if (!ReferenceEquals(dialog.AcceptButton, no) || !ReferenceEquals(dialog.CancelButton, no) || !ReferenceEquals(dialog.ActiveControl, no))
            throw new Exception("Leak override dialog must default to いいえ for Enter, Esc and the first focus");
        if (no.DialogResult != DialogResult.No || yes.DialogResult != DialogResult.Yes || no.Text != "いいえ" || yes.Text != "はい" || no.TabIndex >= yes.TabIndex)
            throw new Exception("Leak override dialog buttons are wrong");
        if (!dialog.TopMost) throw new Exception("Leak override dialog must be topmost");
        static void CreateHandles(Control control) { _ = control.Handle; foreach (Control child in control.Controls) CreateHandles(child); }
        CreateHandles(dialog); dialog.PerformLayout();
        foreach (Control control in dialog.Controls)
            if (control.Left < 0 || control.Top < 0 || control.Right > dialog.ClientSize.Width || control.Bottom > dialog.ClientSize.Height)
                throw new Exception($"Leak override dialog clips {control.Text}: {control.Bounds} in {dialog.ClientSize}");
        using var bitmap = new Bitmap(dialog.Width, dialog.Height);
        dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
        bitmap.Save(Path.Combine(AppContext.BaseDirectory, "leak-override-dialog.png"));
        Console.WriteLine("PASS leak override dialog defaults to いいえ (Enter / Esc / first focus), is topmost and fits its text; image: leak-override-dialog.png");
    }
}

public sealed class LeakFakeGenerator : IFunctionGeneratorSession
{
    public int Stops;
    public string Identity => "fake";
    public string PreparationSummary => "fake";
    public bool IsConnected => true;
    public string QueryIdentity() => Identity;
    public void PrepareBurst(GeneratorBurstSettings settings) => throw new Exception("Unexpected hardware preparation");
    public void TriggerBurst() => throw new Exception("Unexpected trigger");
    public void SetDcOutput(double volts) => throw new Exception("Unexpected output ON");
    public void StopOutput() => Stops++;
    public void Dispose() { }
}

// LOGGER 本体ソフトの模擬(部品は dynamic で呼ぶので、同じ名前のメソッドを持つだけでよい)。Start からの時間に合わせて 2 ms ごとに 1 点ずつ増える。
public sealed class LeakFakeLogger
{
    private Stopwatch? _since;
    private long _frozen;
    public int Starts, Stops;
    public bool Running { get; private set; }
    // 収集開始からの秒数 → 換算前の値(V)。
    public Func<double, float> Value = _ => 5.524f;
    public bool Visible { get; private set; } = true;
    public long GetIdentifier() => 0;
    public long GetFileCount() => Visible ? 1 : 0;
    public long GetState() => Running ? 2 : 1;
    public long Start() { Starts++; Running = true; _since = Stopwatch.StartNew(); return 0; }
    public void Stop() { if (Running) { _frozen = Count(); Stops++; } Running = false; }
    public void Quit() => Visible = false;
    public object GetActiveFile() => new LeakFakeDocument(this);
    public long Count() => !Running ? _frozen : (long)(_since!.Elapsed.TotalMilliseconds / 2);
}

public sealed class LeakFakeDocument(LeakFakeLogger logger)
{
    public long GetDataCount() => logger.Count();
    public long GetArrayData(int unit, int channel, int start, int count, ref float[] buffer, int offset)
    {
        for (int i = 0; i < count; i++) buffer[i] = logger.Value((start + i) * 0.002);
        return count;
    }
}
