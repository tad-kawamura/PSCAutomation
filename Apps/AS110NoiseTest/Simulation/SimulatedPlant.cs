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

    public double ReadMilliamps()
    {
        double value = _changed ? _staleMilliamps : TrueMilliamps;
        _changed = false;
        return Math.Round(value + (_random.NextDouble() * 2 - 1) * noiseMilliamps, 3);
    }
}
