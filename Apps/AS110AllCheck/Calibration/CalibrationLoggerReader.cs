namespace PSCAutomation.Apps.AS110AllCheck;

// NR-500 の電圧を、LOGGER に校正用の雛形(`雛形\AS110校正用.xdt`)で測定させて読む(2026-11-18、SPEC「段階 1 の直しと 1 日 1 回の流れ」)。
// LOGGER の今の値(GetCurrentData)は測定しているあいだしか読めない(2026-11-17 の実機確認で ① が無効値で止まった)ため。
// 最初の読み取りで、開いているファイルを閉じて校正用の雛形を開き、Start する。校正画面を閉じるまで測定を続け、Close で Stop して閉じる。
// 電圧は、読むと決めた時点の点数を覚え、それよりあとに記録された VoltageAverageWindow(1 秒)分の点を平均する(換算前の値 = V)。
// 記録の長さを使い切って止まっていたら Start し直す。LOGGER の操作は画面のスレッドから行う(COM)。
internal sealed class CalibrationLoggerReader(ICalibrationLoggerSource logger, string templatePath, ICalibrationClock clock,
    Action<string> log, AutoCalibrationSettings? settings = null) : IChannelVoltageMeter
{
    public const string TemplateFileName = "AS110校正用.xdt";
    private readonly AutoCalibrationSettings _settings = settings ?? AutoCalibrationSettings.Default;

    // 校正用の雛形を LOGGER で開いたか(Close で閉じる)。
    public bool Opened { get; private set; }

    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        if (!Opened)
        {
            if (!File.Exists(templatePath))
                throw new FileNotFoundException($"校正用の LOGGER 雛形「{Path.GetFileName(templatePath)}」が「雛形」フォルダーにありません。", templatePath);
            if (logger.HasActiveFile())
            {
                long closed = logger.CloseActiveFile();
                if (closed != 0) throw new InvalidOperationException($"LOGGER のファイルを閉じられません（戻り値 {closed}）。");
            }
            long opened = logger.OpenFile(templatePath);
            if (opened != 0)
                throw new InvalidOperationException($"LOGGER で校正用の雛形を開けません（戻り値 {opened}）。{Path.GetFileName(templatePath)}");
            Opened = true;
            log($"校正用の雛形を開きました：{Path.GetFileName(templatePath)}");
        }
        await EnsureMeasuringAsync(cancellationToken);
    }

    public async Task<double> ReadVoltsAsync(int channel, CancellationToken cancellationToken)
    {
        if (channel < 1) throw new ArgumentOutOfRangeException(nameof(channel));
        for (int attempt = 1; ; attempt++)
        {
            await PrepareAsync(cancellationToken);
            long first = logger.GetDataCount();
            DateTimeOffset from = clock.Now;
            await clock.Delay(_settings.VoltageAverageWindow + _settings.VoltageDeliveryMargin, cancellationToken);
            DateTimeOffset deadline = clock.Now + _settings.VoltageReadTimeout;
            long last = logger.GetDataCount();
            // 点が少なければ、上限まで待つ(LOGGER から PC に届く分)。
            while (last - first < _settings.MinVoltagePoints && IsRecording(logger.GetState()) && clock.Now < deadline)
            {
                await clock.Delay(_settings.LoggerPollInterval, cancellationToken);
                last = logger.GetDataCount();
            }
            if (last < first)
            {
                // 点数が減った(記録をやり直した)。もう一度読む。
                if (attempt < 3) continue;
                throw new InvalidDataException("LOGGER の記録がやり直され、電圧を読めません。");
            }
            if (last - first < _settings.MinVoltagePoints)
            {
                // 記録の長さを使い切って止まっていた。Start し直して読む。
                if (!IsRecording(logger.GetState()) && attempt < 3) continue;
                throw new InvalidDataException(
                    $"LOGGER の記録が {_settings.VoltageReadTimeout.TotalSeconds:0} 秒待っても増えません（{last - first} 点）。NR-500 の接続と LOGGER の状態を確かめてください。");
            }
            float[] values = logger.ReadRaw(channel - 1, first, checked((int)(last - first)));
            if (values.Length == 0 || values.Any(v => !float.IsFinite(v) || v >= InvalidRawValue))
                throw new InvalidDataException($"LOGGER から CH{channel} の電圧を読めません（無効値）。NR-500 の接続を確かめてください。");
            double seconds = (clock.Now - from).TotalSeconds;
            double average = values.Average(v => (double)v);
            log($"CH{channel} の電圧：{average.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)} V（{values.Length} 点の平均、" +
                $"{seconds:0.0} 秒で {last - first} 点 = 1 秒あたり {(last - first) / Math.Max(seconds, 0.001):0} 点）");
            return average;
        }
    }

    // 測定していなければ Start し、記録の点数が増え始めるまで待つ。始まるまでの秒数はログに出す(実機で確かめる)。
    private async Task EnsureMeasuringAsync(CancellationToken cancellationToken)
    {
        long state = logger.GetState();
        if (IsRecording(state)) return;
        DateTimeOffset deadline = clock.Now + _settings.LoggerStartTimeout;
        // 止まりかけ(読み出し・残処理)なら、止まるのを待つ。
        while (!IsStopped(state))
        {
            if (clock.Now >= deadline) throw new InvalidOperationException($"LOGGER が止まりません（状態 {state}）。");
            await clock.Delay(_settings.LoggerPollInterval, cancellationToken);
            state = logger.GetState();
        }
        DateTimeOffset started = clock.Now;
        long result = logger.Start();
        if (result != 0) throw new InvalidOperationException($"LOGGER の測定を始められません（戻り値 {result}）。");
        deadline = clock.Now + _settings.LoggerStartTimeout;
        long? baseline = null;
        while (true)
        {
            await clock.Delay(_settings.LoggerPollInterval, cancellationToken);
            state = logger.GetState();
            if (IsRecording(state))
            {
                long count = logger.GetDataCount();
                if (baseline is { } b && count > b)
                {
                    log($"LOGGER の測定を始めました（Start から記録が増え始めるまで {(clock.Now - started).TotalSeconds:0.0} 秒）。");
                    return;
                }
                baseline ??= count;
            }
            if (clock.Now >= deadline)
                throw new InvalidOperationException(
                    $"LOGGER が {_settings.LoggerStartTimeout.TotalSeconds:0} 秒待っても記録を始めません（状態 {state}）。校正用の雛形の設定（トリガーなし）と NR-500 を確かめてください。");
        }
    }

    // 校正画面を閉じたとき(適用・キャンセル): 測定を止め、校正用の雛形を閉じる。失敗は知らせるだけ(呼び出し側で作業用ファイルを読み直す)。
    public void Close()
    {
        if (!Opened) return;
        Opened = false;
        try
        {
            if (!IsStopped(logger.GetState())) logger.Stop();
            long closed = logger.CloseActiveFile();
            log(closed == 0 ? "校正用の雛形の測定を止めて閉じました。" : $"校正用の雛形を閉じられません（戻り値 {closed}）。");
        }
        catch (Exception ex) { log("校正用の雛形を閉じられません：" + ex.Message); }
    }

    private const float InvalidRawValue = 3.0e38f;
    // 1 停止・11 エラー停止。10・12 は止まりかけ。それ以外(2 開始待ち・3 トリガ待ち・4〜7 測定中)は記録中とみなす。
    private static bool IsStopped(long state) => state is 1 or 11;
    private static bool IsRecording(long state) => !IsStopped(state) && state is not (10 or 12) && state > 0;
}

// 校正用の雛形の測定に使う LOGGER の操作(機器なしの模擬に差し替えるため)。
internal interface ICalibrationLoggerSource
{
    bool HasActiveFile();
    long OpenFile(string path);
    long CloseActiveFile();
    long GetState();
    long Start();
    void Stop();
    // PC に読み出されている点数。
    long GetDataCount();
    // channelIndex は 0 始まり(xdt の CH 番号 − 1)。start 点目から count 点の換算前の値。
    float[] ReadRaw(int channelIndex, long start, int count);
}
