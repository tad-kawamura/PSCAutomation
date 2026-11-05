namespace PSCAutomation.Apps.AS110AllCheck;

// AS110各モードの発振器設定を、ここ1か所に定義する。
// WF1973は従来の半周期設定、SDG1032Xは1周期設定を使用する。
// 準備だけでは出力を開始しない。測定時のTriggerBurstで開始する。
internal static class As110MotionTest
{
    // WF1973は0.5周期を設定できる。本体に残る位相に依存せず、開始位相0°を指定する。
    public static GeneratorBurstSettings Settings { get; } = new(
        Waveform: GeneratorWaveform.Triangle,
        FrequencyHz: 0.2,
        AmplitudeVpp: 10.2,
        OffsetVolts: 0,
        BurstCycles: 0.5,
        StartPhaseDegrees: 0);

    // データ調整・データ取りは従来の出力時間12.5秒を維持する。
    public static GeneratorBurstSettings DataSettings { get; } =
        Settings with { FrequencyHz = 0.04 };

    // SDG1032Xは最小1周期。周波数2倍・振幅半分・オフセット+2.55 Vと
    // 開始位相270°で、0 → +5.1 → 0 Vの山の出力時間と傾きを維持する。
    public static GeneratorBurstSettings SdgSettings { get; } = new(
        Waveform: GeneratorWaveform.Triangle,
        FrequencyHz: 0.4,
        AmplitudeVpp: 5.1,
        OffsetVolts: 2.55,
        BurstCycles: 1,
        StartPhaseDegrees: 270);

    public static GeneratorBurstSettings SdgDataSettings { get; } =
        SdgSettings with { FrequencyHz = 0.08 };

    public static GeneratorBurstSettings GetSettings(bool isSdg1032X, bool isMotion) =>
        isSdg1032X
            ? (isMotion ? SdgSettings : SdgDataSettings)
            : (isMotion ? Settings : DataSettings);
}
