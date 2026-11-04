using System.Globalization;

namespace PSCAutomation.Wrappers.Instruments;

// 発振器へ渡す、機種に依存しないバースト波形の設定。
// 周波数・振幅・オフセット・周期数・開始位相をまとめる。
// 現在の対応波形は三角波。波形を増やすときは両機種の実装も追加する。
// 開始位相がnullなら、発振器本体の設定を使用する。
// 元: AS110AllCheck Generator/GeneratorBurstSettings.cs(AS110BoltCheck と同一)。namespace と public のみ変更。
public enum GeneratorWaveform { Triangle }

public sealed record GeneratorBurstSettings(
    GeneratorWaveform Waveform,
    double FrequencyHz,
    double AmplitudeVpp,
    double OffsetVolts,
    double BurstCycles, //サイクル0.5だったのでintから変更
    double? StartPhaseDegrees = null)
{
    public void Validate()
    {
        if (Waveform != GeneratorWaveform.Triangle)
            throw new ArgumentOutOfRangeException(nameof(Waveform), "未対応の波形です。");
        if (!double.IsFinite(FrequencyHz) || FrequencyHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(FrequencyHz));
        if (!double.IsFinite(AmplitudeVpp) || AmplitudeVpp <= 0)
            throw new ArgumentOutOfRangeException(nameof(AmplitudeVpp));
        if (!double.IsFinite(OffsetVolts))
            throw new ArgumentOutOfRangeException(nameof(OffsetVolts));
        if (!double.IsFinite(BurstCycles) || BurstCycles < 0.5)
            throw new ArgumentOutOfRangeException(
                nameof(BurstCycles),
                "バースト周期数は0.5以上で指定してください。");
        if (StartPhaseDegrees is double phase &&
            (!double.IsFinite(phase) || phase < 0 || phase >= 360))
            throw new ArgumentOutOfRangeException(nameof(StartPhaseDegrees));
    }

    public string Summary =>
        $"三角波／{Number(FrequencyHz)} Hz／{Number(AmplitudeVpp)} Vp-p／" +
        $"オフセット{Number(OffsetVolts)} V／{BurstCycles}周期／" +
        (StartPhaseDegrees is double phase
            ? $"開始位相{Number(phase)}°"
            : "開始位相は本体設定");

    internal static string Number(double value) =>
        value.ToString("G", CultureInfo.InvariantCulture);
}
