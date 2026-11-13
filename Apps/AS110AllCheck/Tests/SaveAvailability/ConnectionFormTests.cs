using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;
using PSCAutomation.Wrappers.Instruments;

// 実 MainForm で、接続のやり直し・測定開始・M5Stack の切断まわりを通す(2026-11-13、全体見直し・区切り 3。機器・Excel なし)。
// 発振器と LOGGER は模擬に差し替える(LeakCheckFormTests と同じやり方)。M5Stack は無いので、測定開始は「LOGGER を始める前」まで。
// エラーのポップアップ(本物のメッセージボックス)が出る経路は呼ばない。万一出て止まったら、見張りが検査を失敗で終わらせる。
internal static class ConnectionFormTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(Assembly assembly)
    {
        using var guard = new System.Threading.Timer(_ =>
        {
            Console.WriteLine("FAIL connection form tests: blocked (a message box is probably open)");
            Environment.Exit(1);
        }, null, 60000, Timeout.Infinite);
        CaptureAndStart(assembly);
        ModalDialog(assembly);
    }

    private sealed class Rig
    {
        public required Form Form; public required Type Type; public required object Flow;
        public required ConnectionFakeLogger Logger; public required ConnectionFakeGenerator Generator;
        public object? Field(string name) => Type.GetField(name, Private)!.GetValue(Form);
        public void Set(string name, object? value) => Type.GetField(name, Private)!.SetValue(Form, value);
        public object? Call(string name, params object?[] arguments) => Type.GetMethod(name, Private)!.Invoke(Form, arguments);
        public bool FlowIs(string name) => (bool)Flow.GetType().GetProperty(name)!.GetValue(Flow)!;
        public void FlowCall(string name, params object?[] arguments) => Flow.GetType().GetMethod(name)!.Invoke(Flow, arguments);
        public string Log => ((TextBox)Field("_logTextBox")!).Text;
        public string NextStep => ((Label)Field("_nextStepLabel")!).Text;
    }

    private static Rig Create(Assembly assembly, string template)
    {
        var type = assembly.GetType("PSCAutomation.Apps.AS110AllCheck.MainForm")!;
        var form = (Form)Activator.CreateInstance(type)!;
        var rig = new Rig { Form = form, Type = type, Flow = type.GetField("_flow", Private)!.GetValue(form)!,
            Logger = new ConnectionFakeLogger(template), Generator = new ConnectionFakeGenerator() };
        ((System.Windows.Forms.Timer)rig.Field("_statusHeartbeat")!).Stop();
        rig.Set("_generator", rig.Generator);
        object session = rig.Field("_logger")!;
        session.GetType().GetField("_application", Private)!.SetValue(session, rig.Logger);
        return rig;
    }

    private static void Close(Form form)
    {
        // 終了処理を通して閉じる(先に起動を始めた LOGGER の参照もここで手放す)。
        form.Close();
        var closing = Stopwatch.StartNew();
        while (!form.IsDisposed && closing.Elapsed < TimeSpan.FromSeconds(10)) { Application.DoEvents(); Thread.Sleep(5); }
        if (!form.IsDisposed) throw new Exception("Connection test form did not close");
    }

    // 指摘 1(一時保存に失敗した測定)・指摘 3(切断の処理)・指摘 8(測定開始の前の失敗)。
    private static void CaptureAndStart(Assembly assembly)
    {
        string template = Path.Combine(AppContext.BaseDirectory, "雛形", "AS110データ調整・データ取り用.xdt");
        string reference = Path.Combine(AppContext.BaseDirectory, "雛形", "参照ﾃﾞｰﾀ_AS110-035.xyb");
        if (!File.Exists(template) || !File.Exists(reference)) throw new Exception("Template files not found: " + template);
        string folder = Path.Combine(Path.GetTempPath(), "AS110AllCheck-connection-" + Guid.NewGuid().ToString("N"));
        string rootInRange = Path.Combine(folder, "SN100-200"), rootOutOfRange = Path.Combine(folder, "SN1-10");
        Directory.CreateDirectory(rootInRange); Directory.CreateDirectory(rootOutOfRange);
        var rig = Create(assembly, template);
        _ = rig.Form.Handle;
        try
        {
            // NR-500 を確認済み・データ取り・S/N 123・雛形読み込み済み・校正済みにする(機器なしなので欄は反射で入れる)。設定ファイルは書き換えない。
            rig.Set("_hardwareDetected", true); rig.Set("_detectedHardwareName", "NR-500"); rig.Set("_modeSelected", true);
            object settings = rig.Field("_transferSettings")!;
            settings.GetType().GetProperty("ExpectedSamples")!.SetValue(settings, 6500);
            rig.Flow.GetType().GetProperty("Mode")!.SetValue(rig.Flow, Enum.Parse(assembly.GetType("PSCAutomation.Apps.AS110AllCheck.TestMode")!, "Production"));
            object product = ((Array)assembly.GetType("PSCAutomation.Apps.AS110AllCheck.ProductModel")!.GetField("All")!.GetValue(null)!).GetValue(0)!;
            rig.FlowCall("Configure", "123", product);
            ((TextBox)rig.Field("_serialBox")!).Text = "123";
            void Loaded(string document) { rig.Logger.OpenDocument = document; rig.Set("_templateLoaded", true); rig.Set("_calibrationApplied", true); }
            void FailCapture()
            {
                // 測定の終わりの一時保存が失敗した状態(OnMeasurementCompleted の catch と同じく CaptureError にする)。
                rig.FlowCall("BeginMeasurement", DateTime.Now);
                rig.Logger.SaveResult = 1;
                try { rig.Call("CaptureLatest"); throw new Exception("The capture was expected to fail"); }
                catch (TargetInvocationException ex) when (ex.InnerException is IOException) { rig.FlowCall("CaptureError"); }
                finally { rig.Logger.SaveResult = 0; }
                if (!rig.FlowIs("CaptureFailed")) throw new Exception("Setup: capture failure was not recorded");
            }

            // 1. 一時保存に失敗しても、LOGGER がそのままなら「一時保存を再試行」で今回の測定データを取り込める(今までどおり)。
            Loaded("今回の測定データ");
            FailCapture();
            rig.Call("RetryCapture");
            if (!rig.FlowIs("HasCapture") || rig.FlowIs("CaptureFailed") || rig.Logger.LastSavedFrom != "今回の測定データ")
                throw new Exception("Retry with the LOGGER untouched must capture the measurement: " + rig.Logger.LastSavedFrom);
            Console.WriteLine("PASS capture retry: with the LOGGER untouched, the retry captures this measurement");

            // 2. 一時保存に失敗した後で校正を入れ直す(雛形を読み直す)と、測定は破棄される。再試行は雛形の波形を取り込まない。
            // 以前は一時保存失敗が残り、再試行が読み直した雛形(6500 点入っている)を今回の測定結果として取り込んだ。
            Loaded("今回の測定データ");
            FailCapture();
            rig.Logger.LastSavedFrom = null;
            int savesBefore = rig.Logger.Saves;
            string working = (string)rig.Call("CreateWorkingTemplate", template, null, null)!;
            rig.Call("LoadWorkingTemplate", template, working, null, "NR-500", (Action<string>)(_ => { }), null, null);
            if (rig.FlowIs("CaptureFailed") || rig.FlowIs("HasCapture") || rig.FlowIs("Measuring"))
                throw new Exception("Reloading the template must discard a measurement whose capture failed");
            if (!rig.Log.Contains("一時保存できなかった測定を破棄しました") || !rig.NextStep.Contains("※一時保存できなかった測定は破棄しました。測り直してください"))
                throw new Exception("The discarded measurement must be reported in the log and the next-step guide: " + rig.NextStep);
            rig.Call("RetryCapture");
            if (rig.FlowIs("HasCapture") || rig.Logger.Saves != savesBefore || rig.Logger.LastSavedFrom is not null)
                throw new Exception("After the reload the retry must not capture the template: " + rig.Logger.LastSavedFrom);
            Console.WriteLine("PASS capture retry: after the template is reloaded the failed measurement is discarded and the retry does not capture the template waveform");

            // 3. 測定中の中断は今までどおり(破棄するが、「一時保存できなかった」の知らせは出さない)。
            Loaded("今回の測定データ");
            rig.Set("_captureDiscardedNotice", null);
            rig.FlowCall("BeginMeasurement", DateTime.Now);
            rig.Call("ResetTemplate");
            if (rig.FlowIs("Measuring") || rig.Field("_captureDiscardedNotice") is not null) throw new Exception("An interrupted measurement is discarded without the capture notice");
            Console.WriteLine("PASS a measurement interrupted by a reset is discarded as before");

            // 4. M5Stack の切断の処理: 処理中は重ねて入らない。「確認」の途中は通知を重ねない(出力 OFF・校正の解除・一時保存失敗の破棄は行う)。
            Loaded("今回の測定データ");
            rig.Set("_arduinoDisconnectHandling", true);
            rig.Call("ArduinoDisconnected");
            if (!(bool)rig.Field("_calibrationApplied")! || !(bool)rig.Field("_arduinoDisconnectHandling")!) throw new Exception("A nested disconnect notification must be ignored");
            rig.Set("_arduinoDisconnectHandling", false);
            FailCapture();
            rig.Set("_leakOutputReady", true);
            int stopsBefore = rig.Generator.Stops;
            rig.Set("_checkingConnections", true);
            try { rig.Call("ArduinoDisconnected"); }
            finally { rig.Set("_checkingConnections", false); }
            if ((bool)rig.Field("_calibrationApplied")! || (bool)rig.Field("_templateLoaded")! || rig.FlowIs("CaptureFailed") || (bool)rig.Field("_leakOutputReady")! || rig.Generator.Stops == stopsBefore)
                throw new Exception("A disconnect must stop the output, release the calibration and discard a failed capture");
            if ((bool)rig.Field("_arduinoDisconnectHandling")! || !rig.Log.Contains("Arduino の通信が途切れました"))
                throw new Exception("The disconnect handling must finish and be logged");
            Console.WriteLine("PASS M5Stack disconnect: a nested notification is ignored; during a connection check it is handled without a second popup");

            // 5. 測定開始の本体(BeginMeasurementAsync): LOGGER を始める前の確かめで止まったときは、残っていた測定結果を破棄せず、LOGGER に Stop を送らない。
            Loaded("前の測定データ");
            rig.FlowCall("BeginMeasurement", DateTime.Now);
            foreach (string step in new[] { "CaptureSucceeded", "TransferSucceeded" }) rig.FlowCall(step);
            Exception? Start()
            {
                var task = (Task)rig.Call("BeginMeasurementAsync", null, null, Stopwatch.GetTimestamp())!;
                var watch = Stopwatch.StartNew();
                while (!task.IsCompleted) { Application.DoEvents(); Thread.Sleep(2); if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Measurement start did not return"); }
                return task.Exception?.InnerException;
            }
            void Kept(string scenario, int generatorStops)
            {
                if (!rig.FlowIs("HasTransfer") || rig.FlowIs("Measuring")) throw new Exception(scenario + ": a failure before the start must keep the previous result");
                if (rig.Logger.Stops != 0 || rig.Logger.Starts != 0) throw new Exception(scenario + ": a failure before the start must not touch the LOGGER");
                if (rig.Generator.Stops != generatorStops) throw new Exception($"{scenario}: output OFF count {rig.Generator.Stops}, expected {generatorStops}");
            }
            rig.Logger.Starts = rig.Logger.Stops = 0; rig.Generator.Stops = 0;
            // 5-1. 保存先フォルダー名の範囲外(ConfigureProduct で止まる)。発振器にはまだ触っていないので、出力 OFF も送らない。
            ((TextBox)rig.Field("_rootBox")!).Text = rootOutOfRange;
            var outOfRange = Start();
            if (outOfRange is not InvalidOperationException || !outOfRange.Message.Contains("範囲 1〜10 の外")) throw new Exception("The start must stop for a serial outside the folder range: " + outOfRange);
            Kept("out of range", 0);
            // 5-2. 参照波形を読み込んでいない。
            ((TextBox)rig.Field("_rootBox")!).Text = rootInRange;
            rig.Set("_selectedReferencePath", null);
            var noReference = Start();
            if (noReference is not InvalidOperationException || !noReference.Message.Contains("参照波形")) throw new Exception("The start must stop without the reference waveform: " + noReference);
            Kept("no reference", 0);
            // 5-3. 発振器の準備で失敗: 出力 OFF は試す(発振器に触った後のため)。測定結果は残し、LOGGER には触らない。
            rig.Set("_selectedReferencePath", reference);
            rig.Generator.FailPrepare = true;
            var prepare = Start();
            rig.Generator.FailPrepare = false;
            if (prepare is null || !prepare.Message.Contains("mock prepare failure")) throw new Exception("The start must report the generator failure: " + prepare);
            Kept("generator preparation", 1);
            // 5-4. M5Stack の通信が無い: 始めずに戻る(エラーにしない)。測定結果は残る。
            var notReady = Start();
            if (notReady is not null || !rig.Log.Contains("測定要求を取り消しました")) throw new Exception("Without M5Stack the start must return without starting: " + notReady);
            Kept("M5Stack not ready", 1);
            Console.WriteLine("PASS measurement start: failures before the LOGGER start (folder range, reference, generator preparation, M5Stack not ready) keep the previous result and do not stop the LOGGER");
        }
        finally
        {
            Close(rig.Form);
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    // 指摘 9: PC でモーダルの画面を開いている間は、M5Stack のボタンを押せない表示にし、押されても受け付けない。
    private static void ModalDialog(Assembly assembly)
    {
        var rig = Create(assembly, "");
        var form = rig.Form;
        // 画面を表示すると接続確認が始まるので、処理中にして始めさせない(CheckConnectionsAsync は処理中なら何もしない)。画面の外に出す。
        rig.Set("_operationInProgress", true);
        form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-4000, -4000); form.ShowInTaskbar = false;
        bool shown = false;
        form.Shown += (_, _) => shown = true;
        try
        {
            form.Show();
            var wait = Stopwatch.StartNew();
            while (!shown || wait.ElapsedMilliseconds < 300) { Application.DoEvents(); Thread.Sleep(5); if (wait.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Main form was not shown"); }
            if ((bool)rig.Field("_checkingConnections")!) throw new Exception("The connection check must not start in this test");
            var modalOpen = rig.Type.GetProperty("ModalDialogOpen", Private)!;
            string Mask() => ((string)rig.Call("BuildBoardDisplaySignature")!).Split(':')[1];
            // データ取りの保存後の工程(A=+30mA・B=漏れ試験実施・C=合格)で、3 つとも押せる表示にしておく。
            rig.Flow.GetType().GetProperty("Mode")!.SetValue(rig.Flow, Enum.Parse(assembly.GetType("PSCAutomation.Apps.AS110AllCheck.TestMode")!, "Production"));
            rig.Set("_reviewStage", true);
            foreach (string name in new[] { "_leakOutputButton", "_leakButton", "_completeButton" }) ((Button)rig.Field(name)!).Enabled = true;
            if ((bool)modalOpen.GetValue(form)! || Mask() != "111") throw new Exception("Without a dialog the M5Stack buttons follow the PC buttons: " + Mask());
            string during = "", log = "";
            bool duringOpen = false;
            using (var dialog = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false })
            {
                int tick = 0;
                var timer = new System.Windows.Forms.Timer { Interval = 150 };
                timer.Tick += (_, _) =>
                {
                    if (++tick == 1)
                    {
                        duringOpen = (bool)modalOpen.GetValue(form)!;
                        during = Mask();
                        // M5Stack の C を押したのと同じ通知。表示の版・合言葉が合っていても受け付けない。
                        rig.Call("BoardButtonRequested", 3, (long)rig.Field("_displayEpoch")!, (string)rig.Field("_boardToken")!);
                        return;
                    }
                    timer.Stop(); log = rig.Log; dialog.Close();
                };
                timer.Start();
                dialog.ShowDialog(form);
                timer.Dispose();
            }
            if (!duringOpen || during != "000") throw new Exception("While a dialog is open the M5Stack buttons must be shown disabled: " + during);
            if (!log.Contains("M5Stack操作を受け付けません：C")) throw new Exception("A button pressed while a dialog is open must be refused and logged");
            if ((bool)modalOpen.GetValue(form)! || Mask() != "111") throw new Exception("After the dialog closes the M5Stack buttons must come back: " + Mask());
            Console.WriteLine("PASS M5Stack buttons are shown disabled and refused while a dialog is open on the PC, and come back when it closes");
        }
        finally
        {
            rig.Set("_operationInProgress", false);
            Close(form);
        }
    }
}

public sealed class ConnectionFakeGenerator : IFunctionGeneratorSession
{
    public int Stops;
    public bool FailPrepare;
    public string Identity => "fake,WF1973,0,0";
    public string PreparationSummary => "fake";
    public bool IsConnected => true;
    public string QueryIdentity() => Identity;
    public void PrepareBurst(GeneratorBurstSettings settings) { if (FailPrepare) throw new IOException("mock prepare failure"); }
    public void TriggerBurst() => throw new Exception("Unexpected trigger");
    public void SetDcOutput(double volts) => throw new Exception("Unexpected output ON");
    public void StopOutput() => Stops++;
    public void Dispose() { }
}

// LOGGER 本体ソフトの模擬(部品は dynamic で呼ぶので、同じ名前のメソッドを持つだけでよい)。開いているファイルを保存すると、雛形 xdt の中身(6500 点)が書かれる。
public sealed class ConnectionFakeLogger(string dataSource)
{
    public int Starts, Stops, Saves;
    public string? OpenDocument, LastSavedFrom;
    public long SaveResult;
    public bool Visible { get; private set; } = true;
    public long GetIdentifier() => 0;
    public long GetFileCount() => !Visible || OpenDocument is null ? 0 : 1;
    public long GetState() => 1;
    public long Start() { Starts++; return 0; }
    public void Stop() => Stops++;
    public void Quit() { Visible = false; OpenDocument = null; }
    public long OpenFile(string path) { OpenDocument = "読み直した雛形"; return 0; }
    public long CloseActiveFile() { OpenDocument = null; return 0; }
    public object? GetActiveFile() => OpenDocument is null ? null : new ConnectionFakeDocument(this, dataSource);
}

public sealed class ConnectionFakeDocument(ConnectionFakeLogger logger, string dataSource)
{
    public long SaveFile(string path)
    {
        if (logger.SaveResult != 0) return logger.SaveResult;
        logger.Saves++;
        File.Copy(dataSource, path, true);
        logger.LastSavedFrom = logger.OpenDocument;
        return 0;
    }
    public long GetDataCount() => 6500;
}
