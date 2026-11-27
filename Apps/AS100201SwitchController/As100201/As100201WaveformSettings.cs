namespace PSCAutomation.Apps.AS100201SwitchController;

// AS100-201の設定値。位相は画面で調整する(ここは初期値)。振幅・周波数・オフセット・形はここだけ変更する。
// AS110のバースト設定とは分け、連続出力の位相再開始に使用する。
internal static class As100201WaveformSettings
{
    public static GeneratorContinuousSettings Continuous { get; } = new(AmplitudeVpp, FrequencyHz, OffsetVolts);

    // 1 回押し・2 回押しの位相と 2 回押しの待ち時間は画面で変える(As100201SwitchSettings)。
    // ここの値は初期値(「初期値に戻す」・設定ファイルが無いとき)。2026-11-09 人の指示で PC 0° / 75°、CR 140° / 250° に更新。
    public const int PcPhaseDegrees = 0;
    public const int CrPhaseDegrees = 140;
    public const int PcDoublePhaseDegrees = 75;
    public const int CrDoublePhaseDegrees = 250;
    public const int DoubleWaitMilliseconds = 250;
    public const double AmplitudeVpp = 10.2;
    public const double FrequencyHz = 0.02;
    public const double OffsetVolts = 0;

    // 指令の電圧から電流への換算(2026-11-27 人。5V = 30mA。雛形の「電流モニタ」CH の換算と同じ)。
    // タッチパネルの発振器のボタンに、位相の代わりに電流で出すのに使う(位相の入力・台帳は ° のまま)。
    public const double MilliampsPerVolt = 30.0 / 5.0;

    // 位相の点の電流(mA)と、そこから上るか。三角波(対称)は 0° が 0 から上り、90° が山、270° が谷
    // (WF1973。2026-11-10 の学習テストの xdt で、0° / 75° / 250° から出し直した直後の指令の値と向きを確かめた)。
    // 山・谷ちょうど(90° / 270°)は、そこから向かう向き(90° は下り、270° は上り)にする。
    public static (double Milliamps, bool Rising) CurrentAtPhase(int degrees)
    {
        int d = ((degrees % 360) + 360) % 360;
        double ratio = d <= 90 ? d / 90.0 : d <= 270 ? (180 - d) / 90.0 : (d - 360) / 90.0;
        double volts = OffsetVolts + AmplitudeVpp / 2 * ratio;
        return (volts * MilliampsPerVolt, d < 90 || d >= 270);
    }

    // Tab5 のボタンと同じ書き方(例 +25.5mA↑ / 0.0mA↑ / -23.8mA↓)。小数 1 桁で丸める。
    public static string CurrentText(int degrees)
    {
        var (milliamps, rising) = CurrentAtPhase(degrees);
        double rounded = Math.Round(milliamps, 1, MidpointRounding.AwayFromZero);
        string sign = rounded > 0 ? "+" : "";
        return sign + rounded.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "mA" + (rising ? "↑" : "↓");
    }

    // 波形の形。接続時に WF1973 へ送る(2026-11-04 移植時に追加。元は本体の設定のままで、
    // 別のアプリが DC にした WF1973 では DC が出続けた)。三角波は対称 50%。
    public const GeneratorWaveform Waveform = GeneratorWaveform.Triangle;
}
