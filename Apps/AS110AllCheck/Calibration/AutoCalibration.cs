using System.Globalization;
using PSCAutomation.Wrappers.LoggerFiles;

namespace PSCAutomation.Apps.AS110AllCheck;

// 校正の自動読み取り(段階 1。2026-11-13 承認、SPEC「校正の自動読み取り」)。
// 流れ図「校正_変更」の ①〜④ を順に行い、8 項目(電流 2 点・圧力 2 点)を作る。最後に人が校正画面で「適用」を押す(ここでは適用しない)。
//   ① 電流の低点: WF1973 0 V(0 mA)→ NR-500 CH1 の電圧 = 0 mA
//   ② 電流の高点: WF1973 5 V 固定(30 mA の指令)→ 人がマルチメータの mA を入れる → CH1 の電圧 = 入れた mA
//   ③ 圧力の低点: 30 mA のまま、弁を大気側(①閉・②開)→ GC16 が落ち着いたらゼロ調整 → 3 秒後の GC16 の値と CH2 の電圧
//   ④ 圧力の高点: 30 mA のまま、弁を供給側(①開・②閉)→ 人がレギュレーターを目標に合わせて「合わせた」→ 落ち着いた GC16 の値と CH2 の電圧
// 機器と人の操作は下の interface で受け取る(機器なしの模擬に差し替えられる形)。弁は段階 1 では人がボールバルブを切り替える実装を使う。
// 異常時(決まったこと 17・18・21・27): 通信の失敗は出力を止めて「再開(止まった段の最初から)/ 中止」。済んだ段の値は残す。
// 中止・失敗しても、呼び出し側(校正画面)で手入力に切り替えれば測定へ進める(人の指示。この中だけの「止める」)。
internal sealed class AutoCalibrationProcedure
{
    private readonly IPressureGaugeLocator _locator;
    private readonly ICurrentCommandOutput _current;
    private readonly IChannelVoltageMeter _meter;
    private readonly IValveSwitcher _valves;
    private readonly IAutoCalibrationOperator _operator;
    private readonly ICalibrationClock _clock;
    private readonly AutoCalibrationSettings _settings;

    private IPressureGauge? _gauge;
    private double? _currentVolts;
    private ValvePosition _valvePosition = ValvePosition.Measurement;
    // 段ごとの結果(済んだ段は再開しても残す)。
    private (float Volts, float Milliamps)? _currentLow, _currentHigh;
    private (float Volts, float Kilopascals)? _pressureLow, _pressureHigh;
    private CalibrationZeroCheck? _zero;
    private TimeSpan? _sinceGaugeConfirmed;

    public AutoCalibrationProcedure(IPressureGaugeLocator locator, ICurrentCommandOutput current, IChannelVoltageMeter meter,
        IValveSwitcher valves, IAutoCalibrationOperator calibrationOperator, ICalibrationClock clock, AutoCalibrationSettings? settings = null)
    {
        _locator = locator;
        _current = current;
        _meter = meter;
        _valves = valves;
        _operator = calibrationOperator;
        _clock = clock;
        _settings = settings ?? AutoCalibrationSettings.Default;
        _settings.Validate();
    }

    // 後片付けで出力を止められなかったとき true(呼び出し側で「出力状態不明」にする)。
    public bool OutputStopFailed { get; private set; }
    // 使った GC16 の COM ポート(見つけていなければ null)。
    public string? GaugePort { get; private set; }

    public async Task<AutoCalibrationResult> RunAsync(ProductModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        try
        {
            var stage = AutoCalibrationStage.CurrentLow;
            while (stage <= AutoCalibrationStage.PressureHigh)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await RunStageAsync(stage, model, cancellationToken);
                    stage++;
                }
                catch (RedoStageException)
                {
                    _operator.ShowStatus(StageName(stage) + "を最初からやり直します。");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 17: まず出力を止め、何が起きたかを出す。「再開」で止まった段の最初から(GC16 は探し直す。COM 番号が変わっても探せる)。
                    StopCurrent();
                    ReleaseGauge();
                    string message = $"{StageName(stage)}で止まりました。\r\n{ex.Message}\r\n\r\n" +
                        "WF1973 の出力を止めました。変換ケーブル・機器を確かめて「再開」を押すと、この段の最初からやり直します。";
                    if (!await _operator.AskResumeAsync(message, cancellationToken))
                        throw new OperationCanceledException("校正の自動読み取りを中止しました。", ex);
                }
            }
            var calibration = new As110Calibration(
                new ScalingPoints(_currentLow!.Value.Volts, _currentLow.Value.Milliamps, _currentHigh!.Value.Volts, _currentHigh.Value.Milliamps),
                new ScalingPoints(_pressureLow!.Value.Volts, _pressureLow.Value.Kilopascals, _pressureHigh!.Value.Volts, _pressureHigh.Value.Kilopascals));
            calibration.Validate();
            return new AutoCalibrationResult(calibration, model, _zero!, _sinceGaugeConfirmed, GaugePort ?? "");
        }
        finally
        {
            await CleanUpAsync();
        }
    }

    private Task RunStageAsync(AutoCalibrationStage stage, ProductModel model, CancellationToken ct)
    {
        _operator.ShowStatus(StageName(stage) + "を始めます。");
        return stage switch
        {
            AutoCalibrationStage.CurrentLow => CurrentLowAsync(ct),
            AutoCalibrationStage.CurrentHigh => CurrentHighAsync(ct),
            AutoCalibrationStage.PressureLow => PressureLowAsync(ct),
            _ => PressureHighAsync(model, ct),
        };
    }

    // ① 0 mA の CH1 の電圧。
    private async Task CurrentLowAsync(CancellationToken ct)
    {
        SetCurrent(0);
        await _clock.Delay(_settings.CurrentSettle, ct);
        float volts = ReadVolts(_settings.CurrentChannel, "電流");
        _currentLow = (volts, 0f);
        _operator.ShowStatus($"① 電流の低点：{F(volts)} V = 0 mA");
    }

    // ② 5 V 固定(30 mA の指令)。実際の電流は人がマルチメータを見て入れる(決まったこと 14・23)。
    private async Task CurrentHighAsync(CancellationToken ct)
    {
        SetCurrent(_settings.CommandVolts);
        await _clock.Delay(_settings.CurrentSettle, ct);
        double? milliamps = await _operator.AskActualCurrentAsync(ct);
        if (milliamps is null) throw new OperationCanceledException("校正の自動読み取りを中止しました。");
        if (!double.IsFinite(milliamps.Value) || milliamps.Value <= 0)
            throw new InvalidDataException("マルチメータの電流は 0 より大きい数で入れてください。");
        float volts = ReadVolts(_settings.CurrentChannel, "電流");
        if (_currentLow is { } low && volts <= low.Volts)
            throw new InvalidDataException($"電流の高点の電圧 {F(volts)} V が低点 {F(low.Volts)} V 以下です。WF1973 の出力と配線を確かめてください。");
        _currentHigh = (volts, (float)milliamps.Value);
        _operator.ShowStatus($"② 電流の高点：{F(volts)} V = {F((float)milliamps.Value)} mA");
    }

    // ③ 大気側で落ち着いたらゼロ調整し、0 点を確かめて記録する(決まったこと 20・21・27、AI の案 8)。
    private async Task PressureLowAsync(CancellationToken ct)
    {
        SetCurrent(_settings.CommandVolts);
        var gauge = await EnsureGaugeAsync(ct);
        _valvePosition = ValvePosition.Switching;
        await _valves.SetAtmosphereAsync(ct);
        _valvePosition = ValvePosition.Atmosphere;
        while (true)
        {
            double settled = await WaitSettledAsync(gauge, "③ 圧力の低点（大気圧）", ct);
            if (Math.Abs(settled) <= _settings.LowDeviationKpa) break;
            // 外れているあいだはゼロ調整しない。
            var choice = await _operator.AskDeviationAsync(
                $"③ 大気圧のはずが {F(settled)} kPa で落ち着きました（±{F(_settings.LowDeviationKpa)} kPa を超えています）。\r\n" +
                "ボールバルブ①が閉じ、②が開いているか確かめてください。", ct);
            if (choice == DeviationChoice.UseAsIs) break;
            if (choice == DeviationChoice.Abort) throw new OperationCanceledException("校正の自動読み取りを中止しました。");
        }
        _sinceGaugeConfirmed = _locator.FirstConfirmedAt is { } confirmed ? _clock.Now - confirmed : null;
        int adjustments = 0, round = 0;
        double zero;
        while (true)
        {
            await gauge.ZeroAdjustAsync(ct);
            adjustments++;
            round++;
            _operator.ShowStatus($"③ GC16 をゼロ調整しました（{adjustments} 回目）。{_settings.ZeroSettle.TotalSeconds:0.#} 秒待って 0 点を確かめます。");
            await _clock.Delay(_settings.ZeroSettle, ct);
            zero = await gauge.ReadKilopascalsAsync(ct);
            _operator.ShowPressure(zero);
            if (Math.Abs(zero) <= _settings.ZeroAllowedKpa) break;
            // ±0.2 kPa を超えたら自動でゼロ調整し直す(最大 3 回)。それでも超えたら「もう一度 / 中止」。超えたままは続けられない。
            if (round <= _settings.ZeroRetries) continue;
            bool again = await _operator.AskZeroRetryAsync(
                $"③ ゼロ調整を {round} 回しても 0 点が {F(zero)} kPa です（±{F(_settings.ZeroAllowedKpa)} kPa を超えています）。\r\n" +
                "大気に開いているか確かめて「もう一度」を押してください。", ct);
            if (!again) throw new OperationCanceledException("校正の自動読み取りを中止しました。");
            round = 0;
        }
        float volts = ReadVolts(_settings.PressureChannel, "圧力");
        _zero = new CalibrationZeroCheck(zero, adjustments, Math.Abs(zero) <= _settings.ZeroIdealKpa, _settings.ZeroIdealKpa, _settings.ZeroAllowedKpa);
        _pressureLow = (volts, (float)zero);
        _operator.ShowStatus($"③ 圧力の低点：{F(volts)} V = {F((float)zero)} kPa（0 点 {_zero.Describe()}）");
    }

    // ④ 供給側にし、人がレギュレーターを合わせて「合わせた」を押してから落ち着きを待つ(決まったこと 20・22・27)。
    private async Task PressureHighAsync(ProductModel model, CancellationToken ct)
    {
        SetCurrent(_settings.CommandVolts);
        var gauge = await EnsureGaugeAsync(ct);
        _valvePosition = ValvePosition.Switching;
        await _valves.SetSupplyAsync(ct);
        _valvePosition = ValvePosition.Measurement;
        double target = model.SupplyPressure;
        while (true)
        {
            await WaitRegulatorAdjustedAsync(gauge, target, ct);
            double settled = await WaitSettledAsync(gauge, "④ 圧力の高点", ct);
            // 落ち着いたときの CH2 の電圧を、その場で読む(知らせのあいだに変わらないように)。
            float volts = ReadVolts(_settings.PressureChannel, "圧力");
            double limit = target * _settings.HighDeviationRatio;
            if (Math.Abs(settled - target) > limit)
            {
                var choice = await _operator.AskDeviationAsync(
                    $"④ {F(settled)} kPa で落ち着きました。目標 {target} kPa から ±{F(limit)} kPa（{_settings.HighDeviationRatio:P0}）を超えています。\r\n" +
                    "ボールバルブ①が開き、②が閉じているか、レギュレーターを確かめてください。", ct);
                if (choice == DeviationChoice.Readjust) continue;
                if (choice == DeviationChoice.Abort) throw new OperationCanceledException("校正の自動読み取りを中止しました。");
            }
            _pressureHigh = (volts, (float)settled);
            _operator.ShowStatus($"④ 圧力の高点：{F(volts)} V = {F((float)settled)} kPa（目標 {target} kPa）");
            return;
        }
    }

    // 人が「合わせた」を押すまで、GC16 の値を出し続ける(人はそれを見てレギュレーターを回す)。
    private async Task WaitRegulatorAdjustedAsync(IPressureGauge gauge, double target, CancellationToken ct)
    {
        using var prompt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var adjusted = _operator.WaitRegulatorAdjustedAsync(target, prompt.Token);
        try
        {
            while (!adjusted.IsCompleted)
            {
                _operator.ShowPressure(await gauge.ReadKilopascalsAsync(ct));
                await Task.WhenAny(adjusted, _clock.Delay(_settings.SampleInterval, ct));
                ct.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            // 読み取りが失敗したときは、出したままの「合わせた」を引っ込める。
            prompt.Cancel();
        }
        if (!await adjusted) throw new OperationCanceledException("校正の自動読み取りを中止しました。");
    }

    // 直近 StableWindow(5 秒)の GC16 の値がすべて、最後の値から ±StableTolerance(0.2 kPa)以内になるまで待つ。
    // 待つ上限(15 秒)を過ぎたら弁の向きを確かめるよう知らせ、「待ち続ける / この段をやり直す / 中止」(決まったこと 18・22)。
    private async Task<double> WaitSettledAsync(IPressureGauge gauge, string stageName, CancellationToken ct)
    {
        var samples = new List<(DateTimeOffset At, double Kpa)>();
        DateTimeOffset start = _clock.Now;
        DateTimeOffset deadline = start + _settings.WaitLimit;
        _operator.ShowStatus($"{stageName}：GC16 の値が落ち着くのを待っています。");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            double value = await gauge.ReadKilopascalsAsync(ct);
            DateTimeOffset now = _clock.Now;
            _operator.ShowPressure(value);
            samples.Add((now, value));
            samples.RemoveAll(s => s.At < now - _settings.StableWindow);
            if (now - start >= _settings.StableWindow && samples.All(s => Math.Abs(s.Kpa - value) <= _settings.StableToleranceKpa))
                return value;
            if (now >= deadline)
            {
                var choice = await _operator.AskNotSettledAsync(
                    $"{stageName}：{_settings.WaitLimit.TotalSeconds:0} 秒待っても GC16 の値が落ち着きません（今 {F(value)} kPa）。\r\n" +
                    "ボールバルブ①②の向きを確かめてください。", ct);
                if (choice == WaitChoice.Abort) throw new OperationCanceledException("校正の自動読み取りを中止しました。");
                if (choice == WaitChoice.RedoStage) throw new RedoStageException();
                // 待ち続ける: 知らせのあいだの空きをまたがないよう、見始めからやり直す。
                samples.Clear();
                start = _clock.Now;
                deadline = start + _settings.WaitLimit;
                continue;
            }
            await _clock.Delay(_settings.SampleInterval, ct);
        }
    }

    private async Task<IPressureGauge> EnsureGaugeAsync(CancellationToken ct)
    {
        if (_gauge is not null) return _gauge;
        _operator.ShowStatus("GC16 を探しています。");
        _gauge = await _locator.FindAsync(ct);
        GaugePort = _gauge.PortName;
        _operator.ShowStatus($"GC16 を {_gauge.PortName} で見つけ、通信で操作する状態（REMOTE）にしました。");
        return _gauge;
    }

    private void SetCurrent(double volts)
    {
        if (_currentVolts == volts) return;
        _currentVolts = null;
        _current.SetVolts(volts);
        _currentVolts = volts;
    }

    private float ReadVolts(int channel, string name)
    {
        double volts = _meter.ReadVolts(channel);
        if (!double.IsFinite(volts)) throw new InvalidDataException($"NR-500 CH{channel}（{name}）の電圧を読めません。");
        return (float)volts;
    }

    private void StopCurrent()
    {
        _currentVolts = null;
        try
        {
            _current.Stop();
            OutputStopFailed = false;
        }
        catch (Exception ex)
        {
            OutputStopFailed = true;
            _operator.ShowStatus("WF1973 の出力を止められません：" + ex.Message);
        }
    }

    private void ReleaseGauge()
    {
        var gauge = _gauge;
        _gauge = null;
        if (gauge is null) return;
        try { gauge.Dispose(); }
        catch (Exception ex) { _operator.ShowStatus("GC16 を前面キーが効く状態に戻せません（電源を入れ直すと戻ります）：" + ex.Message); }
    }

    // 終わったら(成功・中止・失敗とも): WF1973 の出力を止め、GC16 を RM,0 に戻し、弁を測定の向き(①開・②閉)に戻す(AI の案 7、決まったこと 19)。
    private async Task CleanUpAsync()
    {
        StopCurrent();
        ReleaseGauge();
        if (_valvePosition != ValvePosition.Measurement)
        {
            try
            {
                await _valves.RestoreForMeasurementAsync();
                _valvePosition = ValvePosition.Measurement;
            }
            catch (Exception ex) { _operator.ShowStatus("弁を測定の向きに戻せません：" + ex.Message); }
        }
    }

    public static string StageName(AutoCalibrationStage stage) => stage switch
    {
        AutoCalibrationStage.CurrentLow => "① 電流の低点（0 mA）",
        AutoCalibrationStage.CurrentHigh => "② 電流の高点（30 mA）",
        AutoCalibrationStage.PressureLow => "③ 圧力の低点（大気圧）",
        _ => "④ 圧力の高点（供給圧力）",
    };

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private enum ValvePosition { Measurement, Switching, Atmosphere }
    private sealed class RedoStageException : Exception;
}

internal enum AutoCalibrationStage { CurrentLow, CurrentHigh, PressureLow, PressureHigh }
internal enum WaitChoice { KeepWaiting, RedoStage, Abort }
internal enum DeviationChoice { Readjust, UseAsIs, Abort }

// 読み取りの決まり。実機で変えられるように 1 か所にまとめる(決まったこと 20〜24・27)。
internal sealed record AutoCalibrationSettings
{
    public static readonly AutoCalibrationSettings Default = new();
    // ②〜④ で WF1973 に出す電圧(30 mA の指令。1 V = 6 mA)。
    public double CommandVolts { get; init; } = 5.0;
    // ①② で出力を変えてから CH1 を読むまでの待ち。
    public TimeSpan CurrentSettle { get; init; } = TimeSpan.FromSeconds(1);
    // GC16 を読む間隔(表示の更新は 0.3 秒ごと)。
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(0.3);
    // 落ち着いた: 直近 5 秒の値がすべて、最後の値から ±0.2 kPa 以内。
    public TimeSpan StableWindow { get; init; } = TimeSpan.FromSeconds(5);
    public double StableToleranceKpa { get; init; } = 0.2;
    // 待つ上限(③は弁の OK、④は「合わせた」から)。
    public TimeSpan WaitLimit { get; init; } = TimeSpan.FromSeconds(15);
    // ゼロ調整のあと 0 点を確かめるまでの待ち(移動平均 2.4 秒が入れ替わる分)。
    public TimeSpan ZeroSettle { get; init; } = TimeSpan.FromSeconds(3);
    public double ZeroIdealKpa { get; init; } = 0.1;
    public double ZeroAllowedKpa { get; init; } = 0.2;
    // 0 点が許容を超えたときに自動でゼロ調整し直す回数。
    public int ZeroRetries { get; init; } = 3;
    // 外れの知らせ: ③ はゼロ調整の前の値が ±5 kPa、④ は目標から ±10%。
    public double LowDeviationKpa { get; init; } = 5;
    public double HighDeviationRatio { get; init; } = 0.10;
    public int CurrentChannel { get; init; } = 1;
    public int PressureChannel { get; init; } = 2;

    public void Validate()
    {
        if (!double.IsFinite(CommandVolts) || CommandVolts <= 0 || CommandVolts > 5)
            throw new ArgumentOutOfRangeException(nameof(CommandVolts), "WF1973 に出す電圧は 0 より大きく 5 V 以下にしてください。");
        if (SampleInterval <= TimeSpan.Zero || StableWindow <= TimeSpan.Zero || WaitLimit <= TimeSpan.Zero ||
            CurrentSettle < TimeSpan.Zero || ZeroSettle < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(WaitLimit), "待ち時間を確かめてください。");
        if (StableToleranceKpa <= 0 || ZeroIdealKpa <= 0 || ZeroAllowedKpa < ZeroIdealKpa || ZeroRetries < 0 ||
            LowDeviationKpa <= 0 || HighDeviationRatio <= 0)
            throw new ArgumentOutOfRangeException(nameof(ZeroAllowedKpa), "落ち着き・0 点・外れの基準を確かめてください。");
    }
}

// 0 点の確かめの結果(決まったこと 21)。
internal sealed record CalibrationZeroCheck(double ValueKpa, int Adjustments, bool WithinIdeal, double IdealKpa, double AllowedKpa)
{
    public string Describe() =>
        $"{ValueKpa.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture)} kPa（" +
        (WithinIdeal ? $"±{IdealKpa.ToString("0.0##", CultureInfo.InvariantCulture)} kPa 以内" :
            $"許容範囲。±{AllowedKpa.ToString("0.0##", CultureInfo.InvariantCulture)} kPa 以内") +
        $"、ゼロ調整 {Adjustments} 回）";
}

internal sealed record AutoCalibrationResult(As110Calibration Calibration, ProductModel Model, CalibrationZeroCheck Zero,
    TimeSpan? SinceGaugeConfirmed, string GaugePort);

// GC16(通信で操作する状態にしたもの)。Dispose で前面キーが効く状態(RM,0)に戻して手放す。
internal interface IPressureGauge : IDisposable
{
    string PortName { get; }
    Task<double> ReadKilopascalsAsync(CancellationToken cancellationToken);
    Task ZeroAdjustAsync(CancellationToken cancellationToken);
}

// GC16 を探す。見つけたら通信で操作する状態にして返す。見つからなければ例外。
internal interface IPressureGaugeLocator
{
    // この起動で最初に GC16 との通信を確かめた時刻(ウォーミングアップの代わりに経過時間を記録する。決まったこと 13)。
    DateTimeOffset? FirstConfirmedAt { get; }
    Task<IPressureGauge> FindAsync(CancellationToken cancellationToken);
}

// 電流の指令(WF1973 の DC 出力)。
internal interface ICurrentCommandOutput
{
    void SetVolts(double volts);
    void Stop();
}

// NR-500 の今の電圧(CH 番号は 1 始まり)。
internal interface IChannelVoltageMeter
{
    double ReadVolts(int channel);
}

// 弁①(供給側)・②(大気側)の切り替え(AI の案 3)。どの実装でも①と②を同時に開けない(開いている方を閉じてから、もう一方を開ける)。
// 段階 1 は人がボールバルブを切り替える(ManualBallValveSwitcher)。段階 2 で電磁弁の実装を足して差し替える。
internal interface IValveSwitcher
{
    // ①閉・②開(AS110 の中を大気圧にする)。
    Task SetAtmosphereAsync(CancellationToken cancellationToken);
    // ①開・②閉(供給圧力が AS110 にかかる。測定のときの向き)。
    Task SetSupplyAsync(CancellationToken cancellationToken);
    // 中止・失敗のあと、測定の向き(①開・②閉)に戻す。取り消せない。
    Task RestoreForMeasurementAsync();
}

// 人とのやりとり(校正の読み取り画面)。
internal interface IAutoCalibrationOperator
{
    void ShowStatus(string text);
    void ShowPressure(double? kilopascals);
    // 指示を出して OK を待つ。false = 中止。
    Task<bool> ConfirmAsync(string instruction, CancellationToken cancellationToken);
    // 指示を出して OK を待つ(中止は選べない。後片付け用)。
    Task NotifyAsync(string instruction);
    // ② マルチメータの電流(mA)。null = 中止。
    Task<double?> AskActualCurrentAsync(CancellationToken cancellationToken);
    // ④ 人がレギュレーターを目標に合わせて「合わせた」を押すのを待つ。false = 中止。
    Task<bool> WaitRegulatorAdjustedAsync(double targetKilopascals, CancellationToken cancellationToken);
    Task<WaitChoice> AskNotSettledAsync(string message, CancellationToken cancellationToken);
    Task<DeviationChoice> AskDeviationAsync(string message, CancellationToken cancellationToken);
    // 0 点が合わない: true = もう一度、false = 中止。
    Task<bool> AskZeroRetryAsync(string message, CancellationToken cancellationToken);
    // 通信の失敗: true = 再開(止まった段の最初から)、false = 中止。
    Task<bool> AskResumeAsync(string message, CancellationToken cancellationToken);
}

internal interface ICalibrationClock
{
    DateTimeOffset Now { get; }
    Task Delay(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemCalibrationClock : ICalibrationClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
    public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

// 段階 1 の弁: 人にボールバルブを切り替えてもらい、OK を待つ(電磁弁①②は未選定。決まったこと 15)。
internal sealed class ManualBallValveSwitcher(IAutoCalibrationOperator calibrationOperator) : IValveSwitcher
{
    public const string AtmosphereInstruction = "ボールバルブを大気側にしてください：先に①（供給側）を閉じ、そのあと②（大気側）を開けてください。\r\n切り替えたら「OK」を押してください。";
    public const string SupplyInstruction = "ボールバルブを供給側にしてください：先に②（大気側）を閉じ、そのあと①（供給側）を開けてください。\r\n切り替えたら「OK」を押してください。";
    public const string RestoreInstruction = "ボールバルブを測定の向きに戻してください：先に②（大気側）を閉じ、そのあと①（供給側）を開けてください。";

    public async Task SetAtmosphereAsync(CancellationToken cancellationToken)
    {
        if (!await calibrationOperator.ConfirmAsync(AtmosphereInstruction, cancellationToken))
            throw new OperationCanceledException("校正の自動読み取りを中止しました。");
    }

    public async Task SetSupplyAsync(CancellationToken cancellationToken)
    {
        if (!await calibrationOperator.ConfirmAsync(SupplyInstruction, cancellationToken))
            throw new OperationCanceledException("校正の自動読み取りを中止しました。");
    }

    public Task RestoreForMeasurementAsync() => calibrationOperator.NotifyAsync(RestoreInstruction);
}
