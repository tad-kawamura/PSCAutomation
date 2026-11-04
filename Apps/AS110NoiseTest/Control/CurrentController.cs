using System.Diagnostics;
using System.Globalization;

namespace PSCAutomation.Apps.AS110NoiseTest.Control;

// 電流を読む機器(7351E)。単位 mA。
internal interface ICurrentMeter
{
    // 電流を変えた後、変更前の値が混じりうる読み取り回数(制御側がこの回数だけ読み捨てる)。
    int StaleReadingsAfterChange { get; }
    double ReadMilliamps();
}

// オフセット電圧で電流を指令する機器(WF1973)。
internal interface IOffsetVoltageSource
{
    bool IsOutputOn { get; }
    void SetOffsetVolts(double volts);
    void SetOutput(bool on);
}

internal sealed record CurrentControlResult(
    double TargetMilliamps,
    // 最後に許容幅へ連続して入った読み取りの平均。
    double MeasuredMilliamps,
    double OffsetVolts,
    // 最初の設定を除いた補正回数。
    int Adjustments,
    // 捨てた分も含む全読み取り(mA、読んだ順)。
    IReadOnlyList<double> Readings,
    TimeSpan Elapsed);

internal sealed class CurrentControlException(string message) : Exception(message);

// WF1973 のオフセットを 7351E の実測でフィードバックし、目標電流 ±許容幅に合わせる。
// 手順:推定電圧を設定 → 変更前の値が混じりうる読み取りを捨てる(7351E が BUS トリガなら 0 回)→ 実測 →
//       許容幅内が規定回数続けば完了、外れたら推定ゲインで電圧を補正して繰り返す。
// 前回までの実測(電圧・電流)を覚えておき、次の目標の初期電圧とゲインの推定に使う。
// 機器は呼び出し側が接続・解放する。同じスレッドから呼ぶこと(VISA COM のため)。
internal sealed class CurrentController
{
    private readonly ICurrentMeter _meter;
    private readonly IOffsetVoltageSource _source;
    private readonly CurrentControlSettings _settings;
    // 実測ゲイン(mA/V)。分からないうちは設定の公称値。
    private double _gain;
    private bool _gainLearned;
    // 直近の安定した実測点(電圧, 電流)。
    private (double Volts, double Milliamps)? _lastPoint;

    public CurrentController(ICurrentMeter meter, IOffsetVoltageSource source, CurrentControlSettings settings)
    {
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        _meter = meter;
        _source = source;
        _settings = settings;
        _gain = settings.MilliampsPerVolt;
    }

    // 経過を知らせる(画面・ログ表示用)。
    public Action<string>? Log { get; set; }
    public double EstimatedMilliampsPerVolt => _gain;

    public CurrentControlResult Regulate(double targetMilliamps, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(targetMilliamps) || targetMilliamps < 0 || targetMilliamps > _settings.MaxMilliamps)
            throw new ArgumentOutOfRangeException(nameof(targetMilliamps), targetMilliamps,
                $"目標電流は 0〜{F(_settings.MaxMilliamps)}mA にしてください。");

        var stopwatch = Stopwatch.StartNew();
        var readings = new List<double>();
        double volts = LimitUnlearnedVolts(EstimateVolts(targetMilliamps));
        WriteLog($"目標 {F(targetMilliamps)}mA:初期電圧 {F(volts, 4)}V(推定ゲイン {F(_gain, 3)}mA/V)");

        for (int adjustment = 0; ; adjustment++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            volts = Apply(volts);
            for (int i = 0; i < _meter.StaleReadingsAfterChange; i++)
                Read(readings, cancellationToken);

            var inTolerance = new List<double>();
            double measured;
            while (true)
            {
                measured = Read(readings, cancellationToken);
                if (!Within(measured, targetMilliamps)) break;
                inTolerance.Add(measured);
                if (inTolerance.Count >= _settings.RequiredConsecutiveReadings)
                {
                    double mean = inTolerance.Average();
                    Remember(volts, mean);
                    WriteLog($"目標 {F(targetMilliamps)}mA:完了 実測 {F(mean)}mA/{F(volts, 4)}V/補正 {adjustment} 回");
                    return new CurrentControlResult(
                        targetMilliamps, mean, volts, adjustment, readings, stopwatch.Elapsed);
                }
            }

            Remember(volts, measured);
            WriteLog($"目標 {F(targetMilliamps)}mA:実測 {F(measured)}mA({F(measured - targetMilliamps, 3)}mA)/{F(volts, 4)}V");
            if (adjustment >= _settings.MaxAdjustments)
                throw new CurrentControlException(
                    $"{F(targetMilliamps)}mA に合わせられません。補正 {adjustment} 回後の実測 {F(measured)}mA、" +
                    $"オフセット {F(volts, 4)}V。配線・7351E のレンジ・オフセット範囲 " +
                    $"{F(_settings.MinOffsetVolts)}〜{F(_settings.MaxOffsetVolts)}V を確認してください。");
            volts += (targetMilliamps - measured) / _gain;
        }
    }

    // 出力を 0V にして止める(測定の終了時・異常時)。
    public void TurnOff()
    {
        _source.SetOffsetVolts(0);
        _source.SetOutput(false);
        WriteLog("出力 OFF(0V)");
    }

    private double EstimateVolts(double targetMilliamps) =>
        _lastPoint is { } last
            ? last.Volts + (targetMilliamps - last.Milliamps) / _gain
            : targetMilliamps / _gain;

    // ゲイン未学習のまま上限付近を狙うと、公称ゲインとの差で上限を超えて保護停止しうる。
    // ゲインが見込みの誤差だけ大きくても上限を超えない電圧に抑え、残りは実測で補正する。
    private double LimitUnlearnedVolts(double volts)
    {
        if (_gainLearned) return volts;
        double worstGain = _gain * (1 + _settings.InitialGainUncertainty);
        (double baseVolts, double baseMilliamps) = _lastPoint ?? (0, 0);
        double limit = baseVolts + (_settings.MaxMilliamps - baseMilliamps) / worstGain;
        if (volts <= limit) return volts;
        WriteLog($"ゲイン未学習のため {F(volts, 4)}V を {F(limit, 4)}V に抑えて開始");
        return limit;
    }

    private double Apply(double volts)
    {
        double clamped = Math.Clamp(volts, _settings.MinOffsetVolts, _settings.MaxOffsetVolts);
        if (clamped != volts)
            WriteLog($"電圧 {F(volts, 4)}V は範囲外のため {F(clamped, 4)}V に制限");
        _source.SetOffsetVolts(clamped);
        if (!_source.IsOutputOn) _source.SetOutput(true);
        return clamped;
    }

    private double Read(List<double> readings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        double milliamps = _meter.ReadMilliamps();
        readings.Add(milliamps);
        if (!double.IsFinite(milliamps))
            throw new CurrentControlException($"7351E の値が不正です({milliamps})。");
        if (milliamps > _settings.MaxMilliamps + _settings.ToleranceMilliamps)
        {
            // 上限超過は機器保護を優先して即停止する。
            try { TurnOff(); } catch (Exception ex) { WriteLog("出力 OFF に失敗:" + ex.Message); }
            throw new CurrentControlException(
                $"実測 {F(milliamps)}mA が上限 {F(_settings.MaxMilliamps)}mA を超えたため出力を止めました。");
        }
        return milliamps;
    }

    private bool Within(double measured, double target) =>
        Math.Abs(measured - target) <= _settings.ToleranceMilliamps;

    // 前の安定点と十分離れていれば、2 点の傾きでゲインを更新する。
    // 公称値から大きく外れる傾きは読み取りの乱れとみなして使わない。
    private void Remember(double volts, double milliamps)
    {
        if (_lastPoint is { } last && Math.Abs(volts - last.Volts) >= 0.05)
        {
            double slope = (milliamps - last.Milliamps) / (volts - last.Volts);
            double nominal = _settings.MilliampsPerVolt;
            if (slope >= nominal * 0.5 && slope <= nominal * 1.5)
            {
                _gain = slope;
                _gainLearned = true;
            }
        }
        _lastPoint = (volts, milliamps);
    }

    private void WriteLog(string message) => Log?.Invoke(message);

    private static string F(double value, int digits = 3) =>
        value.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
