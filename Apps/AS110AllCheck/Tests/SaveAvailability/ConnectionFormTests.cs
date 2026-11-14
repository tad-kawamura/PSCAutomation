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
        // 測定結果が残っていても、終了の確認は出さずに閉じる(確認そのものは Tests/AppShutdown で確かめる)。
        form.GetType().GetField("_confirmClose", Private)!.SetValue(form, (Func<string, bool>)(_ => true));
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
            if ((bool)rig.Field("_arduinoDisconnectHandling")! || !rig.Log.Contains("M5Stack の通信が途切れました"))
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

            // 6. 状態監視の案内は原因に合わせる(2026-11-14、区切り 3 の残り)。以前は発振器の応答なしでも「LOGGER／NR本体 が未接続」と出た。
            // 6-1. 応答があれば何も変えない(雛形を開いていないので NR 本体は確かめない)。
            rig.Set("_templateLoaded", false);
            rig.Call("ConfirmDevicesAlive");
            if (!(bool)rig.Field("_hardwareDetected")! || rig.Field("_monitorFailureNotice") is not null) throw new Exception("A responding generator must not raise a monitor notice");
            // 6-2. 発振器の応答なし: 校正を外し、発振器の案内を出す。
            Loaded("前の測定データ");
            rig.Generator.FailIdentity = true;
            rig.Call("ConfirmDevicesAlive");
            rig.Generator.FailIdentity = false;
            if ((bool)rig.Field("_calibrationApplied")! || !rig.NextStep.Contains("発振器の応答がありません") || rig.NextStep.Contains("LOGGER") || !rig.NextStep.Contains("「確認」"))
                throw new Exception("A silent generator must be reported as the generator: " + rig.NextStep);
            // 6-3. NR 本体を確かめられない(模擬の LOGGER は本体の問い合わせに答えない): NR 本体の案内を出す。
            rig.Set("_hardwareDetected", true); rig.Set("_monitorFailureNotice", null);
            Loaded("前の測定データ");
            rig.Call("ConfirmDevicesAlive");
            if ((bool)rig.Field("_calibrationApplied")! || !rig.NextStep.Contains("NR 本体を確認できません") || rig.NextStep.Contains("発振器"))
                throw new Exception("A silent NR unit must be reported as the NR unit: " + rig.NextStep);
            Console.WriteLine("PASS status monitor: the next-step guide names the device that stopped responding (generator / NR unit)");

            // 7. 測定開始の後、LOGGER が「停止」以外になるのを見るまでは、「停止」を測定の終わりとみなさない(2026-11-14、区切り 3 の残り)。
            // 以前は開始から 0.5 秒後に「停止」と読めると一時保存し、前回のデータが今回の結果になった。
            object state = rig.Field("_measurementState")!;
            var failures = new List<string>();
            rig.Set("_showLoggerStartFailure", (Action<string>)failures.Add);
            void Started(long deadline)
            {
                Loaded("前回の測定データ");
                rig.Set("_hardwareDetected", true);
                rig.FlowCall("BeginMeasurement", DateTime.Now);
                state.GetType().GetProperty("MeasurementStarted")!.SetValue(state, true);
                state.GetType().GetProperty("ReadAfter")!.SetValue(state, 0L);
                rig.Set("_awaitingLoggerStart", true); rig.Set("_loggerStartDeadline", deadline);
            }
            // 7-1. 期限の前に「停止」: まだ待つ(保存しない)。
            int saves = rig.Logger.Saves;
            Started(Environment.TickCount64 + 60000);
            rig.Logger.State = 1;
            rig.Call("RefreshLoggerState");
            if (!rig.FlowIs("Measuring") || rig.Logger.Saves != saves || failures.Count != 0) throw new Exception("A stopped LOGGER before the deadline must be waited for, not captured");
            // 7-2. 「測定中」を見た後の「停止」: 今までどおり一時保存する。
            rig.Logger.State = 4;
            rig.Call("RefreshLoggerState");
            if (!rig.FlowIs("Measuring") || (bool)rig.Field("_awaitingLoggerStart")!) throw new Exception("A running LOGGER must end the wait for the start");
            rig.Logger.State = 1;
            rig.Call("RefreshLoggerState");
            if (!rig.FlowIs("HasCapture") || rig.Logger.Saves != saves + 1 || failures.Count != 0) throw new Exception("After the LOGGER ran, the stop must capture the measurement");
            // 7-3. 期限を過ぎても「停止」: 保存せず、出力 OFF・LOGGER の Stop・測定の破棄を行い、知らせる。
            saves = rig.Logger.Saves; int loggerStops = rig.Logger.Stops, outputStops = rig.Generator.Stops;
            Started(0);
            rig.Call("RefreshLoggerState");
            if (rig.FlowIs("Measuring") || rig.FlowIs("HasCapture") || rig.Logger.Saves != saves) throw new Exception("A LOGGER that never started must not be captured");
            if (rig.Logger.Stops != loggerStops + 1 || rig.Generator.Stops != outputStops + 1) throw new Exception("A LOGGER that never started must be stopped with the output off");
            if (failures.Count != 1 || !failures[0].Contains("LOGGER が測定を始めませんでした") || !rig.NextStep.Contains("LOGGER が測定を始めませんでした") || !rig.Log.Contains("測定開始エラー：LOGGER が測定を始めませんでした"))
                throw new Exception("A LOGGER that never started must be reported once: " + rig.NextStep);
            Console.WriteLine("PASS LOGGER start: a stopped LOGGER is not taken as the end of the measurement until it has been seen running; if it never starts nothing is captured");

            // 8. 動き検査と、データ調整・データ取りの間でモードを替えても、校正は外さず、同じ校正値のまま雛形を読み直す(2026-11-14、人の決定)。
            // 以前は校正が外れ、校正画面で 8 項目を打ち直した。
            var calibrationType = assembly.GetType("PSCAutomation.Apps.AS110AllCheck.As110Calibration")!;
            var fromTemplate = calibrationType.GetMethod("FromTemplate", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
            object calibration = fromTemplate.Invoke(null, [Activator.CreateInstance(fromTemplate.GetParameters()[0].ParameterType, template)])!;
            rig.Logger.State = 1;
            rig.Call("LoadWorkingTemplate", template, (string)rig.Call("CreateWorkingTemplate", template, calibration, null)!, calibration, "NR-500", (Action<string>)(_ => { }), null, null);
            if (!(bool)rig.Field("_calibrationApplied")! || !(bool)rig.Field("_templateLoaded")! || (string?)rig.Field("_workingTemplateSource") != template)
                throw new Exception("Setup: the calibration was not applied");
            var testMode = assembly.GetType("PSCAutomation.Apps.AS110AllCheck.TestMode")!;
            var modeBox = (ComboBox)rig.Field("_modeBox")!;
            void Switch(string mode)
            {
                modeBox.SelectedIndex = (int)Enum.Parse(testMode, mode);
                rig.Call("ChangeTestMode");
                if (rig.Flow.GetType().GetProperty("Mode")!.GetValue(rig.Flow)!.ToString() != mode) throw new Exception("The mode did not change to " + mode);
            }
            void Kept8(string scenario, string templateName)
            {
                if (!(bool)rig.Field("_calibrationApplied")! || !(bool)rig.Field("_templateLoaded")! || !ReferenceEquals(rig.Field("_appliedCalibration"), calibration))
                    throw new Exception(scenario + ": the calibration must stay applied with the same values");
                if (Path.GetFileName((string?)rig.Field("_workingTemplateSource")) != templateName)
                    throw new Exception($"{scenario}: the LOGGER template must be {templateName}, not {rig.Field("_workingTemplateSource")}");
            }
            Switch("Motion");
            Kept8("to motion", "AS110動き検査用.xdt");
            if (!rig.Log.Contains("モードで雛形が替わるため、同じ校正値で読み込み")) throw new Exception("The reload for the mode must be logged");
            Switch("Adjustment");
            Kept8("back to adjustment", "AS110データ調整・データ取り用.xdt");
            Switch("Production");
            Kept8("adjustment to data acquisition", "AS110データ調整・データ取り用.xdt");
            // 読み直せないとき(LOGGER が止まっていない)は、前のモードの雛形のまま測らないよう、校正を外す。
            rig.Flow.GetType().GetProperty("Mode")!.SetValue(rig.Flow, Enum.Parse(testMode, "Motion"));
            rig.Logger.State = 4;
            try { rig.Call("ReloadTemplateForMode", true); throw new Exception("The reload must fail while the LOGGER is running"); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
            finally { rig.Logger.State = 1; }
            if ((bool)rig.Field("_calibrationApplied")! || (bool)rig.Field("_templateLoaded")!) throw new Exception("A failed reload must release the calibration");
            Console.WriteLine("PASS mode change between motion and adjustment/data acquisition keeps the calibration and reloads the template with the same values; a failed reload releases it");
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
    public bool FailPrepare, FailIdentity;
    public string Identity => "fake,WF1973,0,0";
    public string PreparationSummary => "fake";
    public bool IsConnected => true;
    public string QueryIdentity() => FailIdentity ? throw new IOException("mock identity failure") : Identity;
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
    public long State = 1;
    public long GetState() => State;
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
