namespace PSCAutomation.Apps.AS100201SwitchController;

// AS100-201の設定値。実機で位相を調整するときはここだけ変更する。
// AS110のバースト設定とは分け、連続出力の位相再開始に使用する。
internal static class As100201WaveformSettings
{
    public static GeneratorContinuousSettings Continuous { get; } = new(AmplitudeVpp, FrequencyHz, OffsetVolts);

    // 1 回押し・2 回押しの位相と 2 回押しの待ち時間は、試用版では画面で変える(As100201SwitchSettings)。
    // ここの値は初期値(「初期値に戻す」・設定ファイルが無いとき)。2026-11-09 人の指示。
    public const int PcPhaseDegrees = 15;
    public const int CrPhaseDegrees = 165;
    public const int PcDoublePhaseDegrees = 75;
    public const int CrDoublePhaseDegrees = 200;
    public const int DoubleWaitMilliseconds = 250;
    public const double AmplitudeVpp = 10.2;
    public const double FrequencyHz = 0.02;
    public const double OffsetVolts = 0;

    // 波形の形。接続時に WF1973 へ送る(2026-11-04 移植時に追加。元は本体の設定のままで、
    // 別のアプリが DC にした WF1973 では DC が出続けた)。三角波は対称 50%。
    public const GeneratorWaveform Waveform = GeneratorWaveform.Triangle;
}
