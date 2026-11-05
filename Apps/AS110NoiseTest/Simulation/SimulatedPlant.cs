using PSCAutomation.Apps.AS110NoiseTest.Control;

namespace PSCAutomation.Apps.AS110NoiseTest.Simulation;

// WF1973 + 7351E の代わり(Tools/CurrentControlCheck の SimulatedPlant と同じ。Tools/MeasurementCheck --sim と画面の --sim で使う)。
// 電圧 × ゲイン + オフセットの電流が流れ、変更直後の 1 回は変更前の値が読める。
internal sealed class SimulatedPlant(double milliampsPerVolt, double offsetMilliamps, double noiseMilliamps)
    : ICurrentMeter, IOffsetVoltageSource
{
    private readonly Random _random = new(1);
    private double _staleMilliamps;
    private bool _changed;

    public double Volts { get; private set; }
    public bool IsOutputOn { get; private set; }
    public int StaleReadingsAfterChange => 1;
    public double TrueMilliamps => IsOutputOn ? Volts * milliampsPerVolt + offsetMilliamps : 0;

    public void SetOffsetVolts(double volts)
    {
        _staleMilliamps = TrueMilliamps;
        Volts = volts;
        _changed = true;
    }

    public void SetOutput(bool on)
    {
        _staleMilliamps = TrueMilliamps;
        IsOutputOn = on;
        _changed = true;
    }

    // 指令 mA に対する制御圧力の平均 [kPa] のめやす(模擬 LOGGER の波形の中心に使う)。
    // 2026-11-03 の実機 SN71363 のアプリ測定(Apps/AS110NoiseTest/docs/実測比較_SN71363.md)。表の外は端の値。
    public static double TypicalControlKpa(int milliamps)
    {
        ReadOnlySpan<double> kpa =
        [
            0.6, 0.6, 0.6, 0.6, 0.6, 3.6, 6.7, 31.6, 60.2, 92.2,                 // 1〜10 mA(1〜4・6 は推定)
            125.0, 155.5, 189.9, 211.8, 235.5, 257.2, 278.5, 297.8, 316.0, 332.7,  // 11〜20
            347.9, 362.2, 374.4, 385.1, 394.9, 403.3, 410.7, 416.9, 422.4, 426.7,  // 21〜30
        ];
        return kpa[Math.Clamp(milliamps, 1, kpa.Length) - 1];
    }

    public double ReadMilliamps()
    {
        double value = _changed ? _staleMilliamps : TrueMilliamps;
        _changed = false;
        return Math.Round(value + (_random.NextDouble() * 2 - 1) * noiseMilliamps, 3);
    }
}
