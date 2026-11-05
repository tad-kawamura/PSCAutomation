using System.Diagnostics;
using System.Globalization;
using PSCAutomation.Apps.AS110NoiseTest.Analysis;
using PSCAutomation.Apps.AS110NoiseTest.Control;
using PSCAutomation.Apps.AS110NoiseTest.Excel;
using PSCAutomation.Wrappers.LoggerFiles;

namespace PSCAutomation.Apps.AS110NoiseTest.Measurement;

// 1 つの電流の結果。
internal sealed record MeasurementStepResult(
    int CommandMilliamps,
    CurrentControlResult Current,     // 合わせ込みの結果(実測 mA・電圧・補正回数)
    string XdtPath,                   // 保存した xdt
    DateTime MeasuredAt,              // xdt のヘッダの日時(読めなければ記録開始時刻)
    XdtNoiseAnalysis Analysis,        // 全 CH の統計と判定
    int ExcelRow,                     // 書いた PP値シートの行(Excel 以外は 0)
    TimeSpan Elapsed);                // OpenFile から CloseActiveFile まで(1 電流の所要時間)

internal sealed class MeasurementException(string message, Exception? inner = null) : Exception(message, inner);

// 測定シーケンス(設定 → 自動ループ)。CLAUDE.md「アプリの動作フロー」2 をそのまま実装する。
// 1 つの電流につき:
//   OpenFile(雛形 xdt) → CurrentController で WF1973 を 7351E 実測 ±許容幅に合わせる → 待ち(SettleSeconds)
//   → StartSingleAcquisition → GetState で停止待ち → SaveXdt(保存先\規則名) → XdtNoiseAnalyzer.Analyze
//   → INoiseResultWriter.Write(Excel)→ Save → 次の電流
// 全部終わったら FinalMilliamps に合わせる(null なら 0V・出力 OFF)。途中で失敗・中断したら
// LOGGER を止め、WF1973 を 0V・出力 OFF にしてから例外を投げる。
// 機器は呼び出し側が接続・解放する。全部同じ STA スレッドから使う(VISA COM・LOGGER COM のため)。
// 待ちは Wait を差し替えられる(既定はそのスレッドを止める。UI スレッドで使うなら専用スレッドで動かす)。
internal sealed class MeasurementSequence
{
    private readonly ILoggerDevice _logger;
    private readonly CurrentController _controller;
    private readonly CurrentControlSettings _controlSettings;
    private readonly INoiseResultWriter _writer;
    private readonly MeasurementSettings _settings;
    private readonly NoiseJudgeSettings _judge;
    private readonly LoggerChannelMap _channels;
    private bool _acquiring;

    public MeasurementSequence(
        ILoggerDevice logger,
        ICurrentMeter meter,
        IOffsetVoltageSource source,
        CurrentControlSettings controlSettings,
        INoiseResultWriter writer,
        MeasurementSettings settings,
        NoiseJudgeSettings? judgeSettings = null,
        LoggerChannelMap? channels = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        _logger = logger;
        _controlSettings = controlSettings;
        _controller = new CurrentController(meter, source, controlSettings) { Log = m => WriteLog("  電流: " + m) };
        _writer = writer;
        _settings = settings;
        _judge = judgeSettings ?? new NoiseJudgeSettings();
        _channels = channels ?? new LoggerChannelMap();
        _judge.Validate();
    }

    // 経過(画面・ログ用)。
    public Action<string>? Log { get; set; }
    // 1 つの電流を始めるたびに呼ぶ(何番目 0 始まり、全部の数、指令 mA)。画面の「12mA 測定中」用。
    public Action<int, int, int>? StepStarting { get; set; }
    // 1 つの電流が終わるたびに呼ぶ(画面の表更新用)。
    public Action<MeasurementStepResult>? StepCompleted { get; set; }
    // 待ち方。既定は呼び出しスレッドを止める(中断要求で OperationCanceledException)。
    public Action<TimeSpan, CancellationToken> Wait { get; set; } = (span, token) =>
    {
        if (token.WaitHandle.WaitOne(span)) token.ThrowIfCancellationRequested();
    };

    public double MaxMilliamps => _controlSettings.MaxMilliamps;

    public IReadOnlyList<MeasurementStepResult> Run(MeasurementPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate(_controlSettings.MaxMilliamps);
        IReadOnlyList<int> currents = plan.Currents();
        Directory.CreateDirectory(plan.OutputFolder);
        WriteLog($"測定開始: 型式 {plan.Model} / SN {plan.SerialNumber} / 備忘録「{plan.Memo}」/ {string.Join(", ", currents)} mA({currents.Count} 点)");
        WriteLog($"  LOGGER: {_logger.Description} / 雛形 {Path.GetFileName(_settings.TemplateXdtPath)} / 保存先 {plan.OutputFolder}");

        var results = new List<MeasurementStepResult>(currents.Count);
        var total = Stopwatch.StartNew();
        try
        {
            EnsureLoggerReady("測定開始前");
            _writer.WriteSettings(_judge);
            for (int i = 0; i < currents.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteLog($"[{i + 1}/{currents.Count}] {currents[i]} mA");
                StepStarting?.Invoke(i, currents.Count, currents[i]);
                MeasurementStepResult result = RunStep(plan, currents[i], cancellationToken);
                WriteLog($"  所要 {result.Elapsed.TotalSeconds:F1} 秒");
                results.Add(result);
                StepCompleted?.Invoke(result);
            }
            if (!_settings.SaveExcelAfterEachStep) _writer.Save();
            FinishCurrent(cancellationToken);
            WriteLog($"測定終了: {results.Count} 点 / {total.Elapsed.TotalSeconds:F0} 秒");
            return results;
        }
        catch (Exception ex)
        {
            WriteLog(ex is OperationCanceledException ? "中断されました。後始末をします。" : "異常終了: " + ex.Message);
            Cleanup();
            throw;
        }
    }

    private MeasurementStepResult RunStep(MeasurementPlan plan, int milliamps, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // 1. 雛形を開く(記録設定を読み込む)
        long open = _logger.OpenFile(_settings.TemplateXdtPath);
        if (open != 0)
            throw new MeasurementException($"LOGGER が雛形 xdt を開けませんでした(戻り値 {open})。{_settings.TemplateXdtPath}");
        WriteLog("  雛形を開きました");

        // 2. 電流を合わせる
        CurrentControlResult current = _controller.Regulate(milliamps, cancellationToken);

        // 3. 待ち
        if (_settings.SettleSeconds > 0)
        {
            WriteLog($"  {_settings.SettleSeconds:0.##} 秒待ち");
            Wait(TimeSpan.FromSeconds(_settings.SettleSeconds), cancellationToken);
        }

        // 4. 記録開始 → 停止待ち
        EnsureLoggerReady("記録開始前");
        DateTime startedAt = DateTime.Now;
        long start = _logger.StartSingleAcquisition();
        if (start != 0)
            throw new MeasurementException($"LOGGER の記録を開始できませんでした(戻り値 {start})。");
        _acquiring = true;
        WriteLog("  記録開始");
        WaitForAcquisition(cancellationToken);
        _acquiring = false;

        // 5. 保存
        string xdtPath = ResolveXdtPath(plan, milliamps);
        long save = _logger.SaveXdt(xdtPath);
        if (save != 0 || !File.Exists(xdtPath) || new FileInfo(xdtPath).Length == 0)
            throw new MeasurementException($"xdt を保存できませんでした(戻り値 {save})。{xdtPath}");
        if (_settings.ExpectedSamples > 0)
        {
            XdtInfo info = XdtReader.Inspect(xdtPath);
            if (info.Samples != _settings.ExpectedSamples)
                throw new MeasurementException(
                    $"保存した xdt のサンプル数が {info.Samples} 点です({_settings.ExpectedSamples} 点ではない)。雛形の記録設定を確認してください。{xdtPath}");
        }
        WriteLog($"  保存: {Path.GetFileName(xdtPath)}");

        // 6. 集計 → Excel
        XdtNoiseAnalysis analysis = XdtNoiseAnalyzer.Analyze(xdtPath, _judge, _channels);
        DateTime measuredAt = XdtTimestamp.TryRead(xdtPath) ?? startedAt;
        var record = new NoiseResultRecord(
            measuredAt, plan.Model, plan.SerialNumber, plan.Memo ?? "", milliamps, current.MeasuredMilliamps, analysis, Path.GetFileName(xdtPath));
        int row = _writer.Write(record);
        if (_settings.SaveExcelAfterEachStep) _writer.Save();
        NoiseMetrics m = analysis.Metrics;
        WriteLog($"  結果: p-p {m.PeakToPeakKpa:F1} kPa / 平均 {analysis.ControlPressure.Mean:F1} kPa / 判定 {Grade(m.Grade)}" +
                 (row > 0 ? $" / Excel 行 {row}" : ""));

        // 7. 雛形を閉じる(次の電流でまた開く)
        if (_settings.CloseFileAfterSave)
        {
            long close = _logger.CloseActiveFile();
            if (close != 0) throw new MeasurementException($"LOGGER のファイルを閉じられませんでした(戻り値 {close})。");
        }

        // 8. 保存した xdt の制御圧力の表示レンジを平均値中心にする(LOGGER がファイルを手放した後に書く)
        if (_settings.CenterControlPressureDisplay) CenterDisplayRange(xdtPath, analysis.ControlPressure.Mean);

        return new MeasurementStepResult(milliamps, current, xdtPath, measuredAt, analysis, row, stopwatch.Elapsed);
    }

    // Start の後、停止(1)から抜けたことを確かめ、停止(1)に戻るまで待つ。
    private void WaitForAcquisition(CancellationToken cancellationToken)
    {
        Wait(TimeSpan.FromMilliseconds(_settings.StateReadDelayMilliseconds), cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        bool started = false;
        long? lastState = null;
        while (true)
        {
            long state = _logger.GetState();
            if (state != lastState)
            {
                WriteLog($"  LOGGER: {LoggerState.Describe(state)}({stopwatch.Elapsed.TotalSeconds:F1} 秒)");
                lastState = state;
            }
            if (state <= 0)
                throw new MeasurementException($"LOGGER の状態を取得できません(GetState={state})。");
            if (state == LoggerState.ErrorStopped)
                throw new MeasurementException("LOGGER がエラー停止しました(GetState=11)。LOGGER 画面のエラー内容を確認してください。");
            if (!started)
            {
                if (state != LoggerState.Stopped) started = true;
                else if (stopwatch.Elapsed.TotalSeconds >= _settings.StartTimeoutSeconds)
                    throw new MeasurementException(
                        $"記録が始まりません({_settings.StartTimeoutSeconds:0.#} 秒待っても停止のまま)。LOGGER 画面と本体を確認してください。");
            }
            else if (state == LoggerState.Stopped)
            {
                WriteLog($"  記録終了({stopwatch.Elapsed.TotalSeconds:F1} 秒)");
                return;
            }
            if (stopwatch.Elapsed.TotalSeconds >= _settings.AcquisitionTimeoutSeconds)
                throw new MeasurementException(
                    $"記録が {_settings.AcquisitionTimeoutSeconds:0.#} 秒たっても終わりません(状態: {LoggerState.Describe(state)})。雛形の記録時間・トリガ設定を確認してください。");
            Wait(TimeSpan.FromMilliseconds(_settings.StatePollIntervalMilliseconds), cancellationToken);
        }
    }

    private void EnsureLoggerReady(string when)
    {
        long state = _logger.GetState();
        if (!LoggerState.IsReadyToStart(state))
            throw new MeasurementException($"{when}: LOGGER が停止していません(状態: {LoggerState.Describe(state)})。");
    }

    private string ResolveXdtPath(MeasurementPlan plan, int milliamps)
    {
        string name = _settings.BuildXdtFileName(plan.Model, plan.SerialNumber, plan.Memo ?? "", milliamps);
        string path = Path.Combine(plan.OutputFolder, name);
        if (!File.Exists(path)) return path;
        if (_settings.OverwriteExistingXdt)
        {
            File.Delete(path);
            return path;
        }
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int n = 2; ; n++)
        {
            string candidate = Path.Combine(plan.OutputFolder, $"{stem}_{n.ToString(CultureInfo.InvariantCulture)}{ext}");
            if (!File.Exists(candidate))
            {
                WriteLog($"  同名の xdt があるため {Path.GetFileName(candidate)} に保存します");
                return candidate;
            }
        }
    }

    // 全電流が終わった後の電流。
    private void FinishCurrent(CancellationToken cancellationToken)
    {
        if (_settings.FinalMilliamps is double final && final > 0)
        {
            WriteLog($"終了時の電流 {final:0.##} mA に合わせます(出力 ON のまま)");
            _controller.Regulate(final, cancellationToken);
        }
        else
        {
            _controller.TurnOff();
        }
    }

    // 失敗・中断時: 記録中なら LOGGER を止め、電流を切る。ここでの失敗は元の例外を隠さずログだけにする。
    private void Cleanup()
    {
        if (_acquiring)
        {
            try
            {
                _logger.Stop();
                var stopwatch = Stopwatch.StartNew();
                while (stopwatch.Elapsed.TotalSeconds < _settings.StopTimeoutSeconds)
                {
                    long state = _logger.GetState();
                    if (LoggerState.IsReadyToStart(state)) break;
                    Thread.Sleep(_settings.StatePollIntervalMilliseconds);
                }
                WriteLog("LOGGER を停止しました");
            }
            catch (Exception ex) { WriteLog("LOGGER の停止に失敗: " + ex.Message); }
            _acquiring = false;
        }
        try { _controller.TurnOff(); }
        catch (Exception ex) { WriteLog("WF1973 の出力 OFF に失敗: " + ex.Message); }
    }

    private static string Grade(NoiseGrade grade) => grade switch
    {
        NoiseGrade.Red => "赤",
        NoiseGrade.Yellow => "黄",
        _ => "無色",
    };

    // 表示レンジの書き換えは見た目のためなので、失敗しても測定は続ける(警告を出す)。
    private void CenterDisplayRange(string xdtPath, double controlMeanKpa)
    {
        try
        {
            XdtDisplayRange.Range before = XdtDisplayRange.Read(xdtPath, _channels.ControlPressure);
            bool changed = XdtDisplayRange.Center(xdtPath, _channels.ControlPressure, controlMeanKpa, _settings.DisplayCenterStepKpa);
            XdtDisplayRange.Range after = XdtDisplayRange.Read(xdtPath, _channels.ControlPressure);
            WriteLog(changed
                ? $"  表示レンジ: 制御圧力 {before.Lower:0.#}〜{before.Upper:0.#} → {after.Lower:0.#}〜{after.Upper:0.#} kPa"
                : $"  表示レンジ: 制御圧力 {after.Lower:0.#}〜{after.Upper:0.#} kPa(そのまま)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            WriteLog($"  警告: 表示レンジを直せませんでした({ex.Message})。LOGGER で開くと制御圧力の線が画面の外に出ることがあります。{Path.GetFileName(xdtPath)}");
        }
    }

    private void WriteLog(string message) => Log?.Invoke(message);
}
