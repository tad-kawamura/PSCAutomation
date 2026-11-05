using System.Globalization;
using System.Text;
using PSCAutomation.Apps.AS110NoiseTest.Control;
using PSCAutomation.Apps.AS110NoiseTest.Excel;
using PSCAutomation.Apps.AS110NoiseTest.Measurement;
using PSCAutomation.Apps.AS110NoiseTest.Simulation;
using PSCAutomation.Wrappers.Instruments;
using PSCAutomation.Wrappers.WaveLogger.Automation;

namespace PSCAutomation.Apps.AS110NoiseTest.Ui;

// 画面の「開始」で渡す内容。
internal sealed record RunRequest(MeasurementPlan Plan, TemplateFiles Templates, bool Simulation);

internal enum RunResult
{
    Finished,   // 最後の電流まで測った
    Declined,   // 「測定済みの行がある」の確認で、人がやめた
    Canceled,   // 人が中断した
    Failed,     // 機器・Excel の異常などで止まった
}

// 終わったときに画面へ返す内容。Message は人に見せる文。ExcelWindow は人に渡した結果 Excel の窓(前に出す用)。
internal sealed record RunOutcome(RunResult Result, string Message, IntPtr ExcelWindow);

// 測定を専用の STA スレッドで動かす(specs/AS110NoiseTest/SPEC.md「画面」、docs/測定シーケンス実機確認.md「スレッド」)。
// VISA・LOGGER・Excel の COM は「接続したスレッドから使う」前提なので、Excel を開く・機器をつなぐ・測る・後始末を全部このスレッドで行う。
// LOGGER の終了(ShutdownAsync)が await をはさむため、スレッドでメッセージループ(Application.Run)を回す。
// 順番: 結果 Excel を開く → 測定済みの行があれば人に確かめる → 機器をつなぐ → 測る → Excel を人に渡す → 機器の後始末。
// 画面への知らせ(ReportStatus / AskToContinue / Completed)はこのスレッドから呼ぶので、画面側で Invoke する。
internal sealed class MeasurementRunner
{
    // 模擬(--sim)の記録時間 [s]。実機の雛形は 40 秒。
    private const double SimulatedRecordSeconds = 2.0;

    // 状態欄の文(「12mA 測定中」など)。
    public required Action<string> ReportStatus { get; init; }
    // はい / いいえ の確認。true なら続ける。
    public required Func<string, bool> AskToContinue { get; init; }
    // 終わったとき(成功・中断・異常とも 1 回)。
    public required Action<RunOutcome> Completed { get; init; }

    public void Start(RunRequest request, CancellationToken cancellationToken)
    {
        var thread = new Thread(() => ThreadMain(request, cancellationToken)) { Name = "AS110NoiseTest 測定" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private void ThreadMain(RunRequest request, CancellationToken cancellationToken)
    {
        RunOutcome? outcome = null;
        var context = new ApplicationContext();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        SynchronizationContext.Current!.Post(async _ =>
        {
            try { outcome = await RunAsync(request, cancellationToken); }
            catch (Exception ex) { outcome = new RunOutcome(RunResult.Failed, "予期しない異常で止まりました: " + ex.Message, IntPtr.Zero); }
            finally { context.ExitThread(); }
        }, null);
        Application.Run(context);
        Completed(outcome ?? new RunOutcome(RunResult.Failed, "測定の処理が結果を返さずに終わりました。", IntPtr.Zero));
    }

    private async Task<RunOutcome> RunAsync(RunRequest request, CancellationToken cancellationToken)
    {
        MeasurementPlan plan = request.Plan;
        IReadOnlyList<int> currents = plan.Currents();

        // 細かい記録は画面に出さず、xdt の保存先のログファイルに残す(止まったときの調査用)。
        Directory.CreateDirectory(plan.OutputFolder);
        string logPath = Path.Combine(plan.OutputFolder, $"AS110NoiseTest_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        using var logWriter = new StreamWriter(logPath, append: true, new UTF8Encoding(true)) { AutoFlush = true };
        void Log(string message)
        {
            try { logWriter.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}"); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
        Log($"開始: {(request.Simulation ? "模擬(機器を使わない)" : "実機")} / 型式 {plan.Model} / SN {plan.SerialNumber} / 備忘録「{plan.Memo}」/ " +
            $"{plan.StartMilliamps}〜{plan.EndMilliamps}mA 刻み {plan.StepMilliamps}mA / xdt {plan.OutputFolder} / Excel {plan.ExcelPath}");

        var done = new List<int>();
        bool measuring = false, handedOver = false;
        NoiseResultWorkbook? workbook = null;
        Adcmt7351ESession? meter = null;
        Wf1973DcSource? source = null;
        WaveLoggerDevice? realLogger = null;
        try
        {
            // 1. 結果 Excel(新規なら雛形から作る)。ほかで開かれていて読み取り専用になったら止める。
            ReportStatus("準備中(結果 Excel を開いています)");
            try { workbook = NoiseResultWorkbook.Open(plan.ExcelPath, request.Templates.Excel, visible: false); }
            catch (IOException ex)
            {
                Log("結果 Excel を開けません: " + ex.Message);
                return new RunOutcome(RunResult.Failed,
                    $"結果 Excel を開けません。ほかで開いていれば閉じてから、もう一度「開始」してください。\n{plan.ExcelPath}\n\n{ex.Message}", IntPtr.Zero);
            }
            Log($"結果 Excel: {workbook.Path}{(workbook.CreatedFromTemplate ? "(雛形から新規作成)" : "(追記)")}");

            // 2. 同じ型式・SN・備忘録の行がもうあれば、上書きしてよいか確かめる(SN の打ち間違いで別の弁の結果を消さないため)。
            IReadOnlyList<int> measured = workbook.FindMeasuredMilliamps(plan.Model, plan.SerialNumber, plan.Memo);
            if (measured.Count > 0)
            {
                List<int> overlap = currents.Intersect(measured).Order().ToList();
                string text = $"{Describe(plan)} は {FormatMilliamps(measured)} mA を測定済みです。\n" +
                              (overlap.Count > 0
                                  ? $"重なる {FormatMilliamps(overlap)} mA は上書きします。"
                                  : "今回の電流とは重なりません(同じ行に足します)。") +
                              "\n\n続けますか?";
                Log("測定済みの行あり: " + FormatMilliamps(measured) + " mA");
                if (!AskToContinue(text))
                {
                    Log("人が開始をやめました");
                    return new RunOutcome(RunResult.Declined, "開始をやめました。", IntPtr.Zero);
                }
            }

            // 3. 機器をつなぐ(見つからなければ、測定を始めずに止める)。
            ReportStatus("準備中(機器を確認しています)");
            var settings = new MeasurementSettings
            {
                TemplateXdtPath = request.Templates.Xdt,
                TemplateExcelPath = request.Templates.Excel,
                PreviewTemplateXdtPath = request.Templates.PreviewXdtIfExists,
            };
            if (settings.PreviewTemplateXdtPath is null)
                Log($"試し取り用の雛形 {TemplateFiles.PreviewXdtName} が無いので試し取りしません(測定中の画面の制御圧力は雛形の表示レンジのまま)");
            ICurrentMeter currentMeter;
            IOffsetVoltageSource voltageSource;
            ILoggerDevice logger;
            if (request.Simulation)
            {
                // Tools/MeasurementCheck --sim と同じ模擬。10mA が黄、20mA が赤になる波形にする。
                var plant = new SimulatedPlant(milliampsPerVolt: 5.85, offsetMilliamps: 0.04, noiseMilliamps: 0.004);
                currentMeter = plant;
                voltageSource = plant;
                logger = new SimulatedLogger(TimeSpan.FromSeconds(SimulatedRecordSeconds), () => plant.TrueMilliamps,
                    ma => ma == 20 ? WaveProfile.Red : ma == 10 ? WaveProfile.Yellow : WaveProfile.Normal, Log)
                {
                    PreviewTemplatePath = settings.PreviewTemplateXdtPath,
                    ControlKpaOf = SimulatedPlant.TypicalControlKpa,
                };
                settings = settings with { AcquisitionTimeoutSeconds = SimulatedRecordSeconds * 2 + 5 };
                Log("機器: 模擬(WF1973・7351E・LOGGER)");
            }
            else
            {
                meter = Adcmt7351ESession.Connect();
                Log($"7351E : {meter.Identity} ({meter.Address})");
                var meterSettings = new Adcmt7351ESettings();
                IReadOnlyList<string> oldErrors = meter.ApplySettings(meterSettings);
                if (oldErrors.Count > 0) Log("7351E 設定前のエラー・ログ: " + string.Join(" / ", oldErrors));
                Log($"7351E 設定: {string.Join(" ", meterSettings.ToCommands())}");
                source = Wf1973DcSource.Connect();
                Log($"WF1973: {source.Identity} ({source.Address})");
                currentMeter = new Adcmt7351ECurrentMeter(meter);
                voltageSource = new Wf1973OffsetVoltageSource(source);
                realLogger = WaveLoggerDevice.Connect(DeviceSettings.LoggerEdition, DeviceSettings.LoggerUsbId, Log);
                Log($"LOGGER: {realLogger.Description}");
                logger = realLogger;
                // 測定中は LOGGER の画面を前に出しておく(人が波形を見られるように)。出せなくても測定は続ける。
                try
                {
                    if (!realLogger.Session.BringToFront()) Log("LOGGER の画面を前に出せませんでした(Windows が切り替えを拒否)");
                }
                catch (Exception ex) { Log("LOGGER の画面を前に出せませんでした: " + ex.Message); }
            }

            // 4. 測る(1 電流ごとに Excel を保存する)。
            var sequence = new MeasurementSequence(logger, currentMeter, voltageSource, new CurrentControlSettings(), workbook, settings)
            {
                Log = Log,
                StepStarting = (_, _, ma) => ReportStatus($"{ma}mA 測定中"),
                StepCompleted = r => done.Add(r.CommandMilliamps),
            };
            measuring = true;
            IReadOnlyList<MeasurementStepResult> results = sequence.Run(plan, cancellationToken);
            measuring = false;

            // 5. 結果 Excel を画面に出して人に渡す(開いたまま)。
            ReportStatus("後始末をしています");
            string summary = "";
            int row = results.Select(r => r.ExcelRow).FirstOrDefault(r => r > 0);
            if (row > 0)
            {
                var s = workbook.ReadRowSummary(row);
                summary = $"判定 {s.Judge}(最大 p-p {s.MaxPp?.ToString("F1", CultureInfo.InvariantCulture) ?? "-"} kPa、黄 {s.Yellow?.ToString() ?? "-"}、赤 {s.Red?.ToString() ?? "-"})";
                Log($"Excel 行 {row}: {summary}");
            }
            IntPtr excelWindow = IntPtr.Zero;
            try
            {
                excelWindow = workbook.ShowToUser();
                handedOver = true;
            }
            catch (Exception ex) { Log("結果 Excel を画面に出せませんでした: " + ex.Message); }
            Log("終了");
            return new RunOutcome(RunResult.Finished,
                $"終了しました。\n{Describe(plan)}\n{summary}\n\n結果 Excel: {workbook.Path}" +
                (handedOver ? "" : "\n(Excel を画面に出せませんでした。保存はしてあるので、自分で開いてください)"),
                excelWindow);
        }
        catch (Exception ex)
        {
            bool canceled = ex is OperationCanceledException;
            Log(canceled ? "中断しました" : "止まりました: " + ex);
            string reason = canceled ? "中断しました。" : "止まりました。\n" + ex.Message;
            string resume = "";
            if (measuring)
            {
                int next = currents.FirstOrDefault(c => !done.Contains(c));
                resume = next > 0
                    ? $"\n\n続きは {next}mA からです(開始 mA を {next} にして、もう一度「開始」してください)。" +
                      (done.Count > 0 ? $"\n{FormatMilliamps(done)} mA の結果は結果 Excel に保存してあります。" : "")
                    : "";
            }
            return new RunOutcome(canceled ? RunResult.Canceled : RunResult.Failed,
                reason + resume + $"\n\n記録: {logPath}", IntPtr.Zero);
        }
        finally
        {
            // Excel・LOGGER を閉じるのに数秒かかる(中断中は画面が「中断しています…」を出したままにする)。
            if (!handedOver) ReportStatus("後始末をしています");
            if (realLogger is not null)
            {
                try { await realLogger.ShutdownAsync(Log); }
                catch (Exception ex) { Log("LOGGER の終了に失敗: " + ex.Message + "(LOGGER の画面を確認してください)"); }
                realLogger.Dispose();
            }
            // 新規に作ったのに 1 つも書かずに終わったら、空のブックを残さない(次に同じ名前で「新規」を選べるように)。
            string? createdPath = workbook is { CreatedFromTemplate: true } && done.Count == 0 && !handedOver ? workbook.Path : null;
            workbook?.Dispose();
            if (createdPath is not null)
            {
                try
                {
                    File.Delete(createdPath);
                    Log("何も書かなかったので、新規に作った結果 Excel を消しました: " + createdPath);
                }
                catch (Exception ex) { Log("新規に作った結果 Excel を消せませんでした: " + ex.Message); }
            }
            source?.Dispose();
            meter?.Dispose();
        }
    }

    private static string Describe(MeasurementPlan plan) =>
        $"{plan.Model} {plan.SerialNumber}" + (string.IsNullOrWhiteSpace(plan.Memo) ? "" : $" {plan.Memo}");

    // 7,8,9,10,15 → "7〜10, 15"
    internal static string FormatMilliamps(IEnumerable<int> milliamps)
    {
        var sorted = milliamps.Distinct().Order().ToList();
        var parts = new List<string>();
        for (int i = 0; i < sorted.Count;)
        {
            int j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            parts.Add(j > i ? $"{sorted[i]}〜{sorted[j]}" : $"{sorted[i]}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }
}

// 実機の LOGGER の設定(アプリ固有の値。wrapper には置かない)。
internal static class DeviceSettings
{
    // NR-500 は旧 WAVE LOGGER で測る(実機 PC には WAVE LOGGER X も入っているため、自動選択にしない)。
    public const WaveLoggerEdition LoggerEdition = WaveLoggerEdition.Legacy;
    public const int LoggerUsbId = 0;
}
