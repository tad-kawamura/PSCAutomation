using PSCAutomation.Wrappers.Instruments;

namespace PSCAutomation.Apps.AS110NoiseTest.Control;

// Wrappers/Instruments の機器クラスを、電流制御が使う interface で包む(中身はそのまま渡すだけ)。
// 移植前は Devices/ の機器クラスが ICurrentMeter・IOffsetVoltageSource を直接実装していた。
// wrapper はアプリの interface を知らないので、ここで包む(機器なしの模擬に差し替えられる形はそのまま)。

// ADCMT 7351E を ICurrentMeter として使う。接続・解放は呼び出し側。
internal sealed class Adcmt7351ECurrentMeter(Adcmt7351ESession meter) : ICurrentMeter
{
    public Adcmt7351ESession Meter { get; } = meter ?? throw new ArgumentNullException(nameof(meter));
    public int StaleReadingsAfterChange => Meter.StaleReadingsAfterChange;
    public double ReadMilliamps() => Meter.ReadMilliamps();
}

// WF1973 の DC オフセットを IOffsetVoltageSource として使う。接続・解放は呼び出し側。
internal sealed class Wf1973OffsetVoltageSource(Wf1973DcSource source) : IOffsetVoltageSource
{
    public Wf1973DcSource Source { get; } = source ?? throw new ArgumentNullException(nameof(source));
    public bool IsOutputOn => Source.IsOutputOn;
    public void SetOffsetVolts(double volts) => Source.SetOffsetVolts(volts);
    public void SetOutput(bool on) => Source.SetOutput(on);
}
