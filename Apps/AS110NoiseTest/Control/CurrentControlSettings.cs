namespace PSCAutomation.Apps.AS110NoiseTest.Control;

// 電流フィードバック制御の設定値。値はコードに直書きせず、ここから渡す。
// 既定値は CLAUDE.md の決定事項(1V = 6mA、±0.1mA、上限 35mA)と 7351E の実機確認に基づく。
internal sealed record CurrentControlSettings
{
    // WF1973 オフセット 1V あたりの電流(初期推定と補正の初期ゲインに使う)。
    public double MilliampsPerVolt { get; init; } = 6.0;
    // 実測でゲインを学習するまでの、公称ゲインに対する誤差の見込み(比率)。
    // 上限付近の目標はこの分だけ多く流れても上限を超えない電圧から始める(実機は公称より約 0.6% 大きい)。
    public double InitialGainUncertainty { get; init; } = 0.03;
    // 目標に対する許容幅(±)。
    public double ToleranceMilliamps { get; init; } = 0.1;
    // これを超える目標は受け付けない。実測がこれ+許容幅を超えたら出力を止めて中断する。
    public double MaxMilliamps { get; init; } = 35.0;
    // オフセット電圧の範囲。補正がこれを超える場合は範囲内に抑える(収束しなければエラー)。
    // 既定の上限 6.5V は 35mA(約 5.83V)に約 1割の余裕を持たせた値。
    public double MinOffsetVolts { get; init; } = 0.0;
    public double MaxOffsetVolts { get; init; } = 6.5;
    // 許容幅に連続して入った回数がこれに達したら合わせ込み完了とする。
    public int RequiredConsecutiveReadings { get; init; } = 2;
    // 補正(電圧の変更)の最大回数。超えたらエラー。
    public int MaxAdjustments { get; init; } = 8;

    public void Validate()
    {
        static bool Positive(double v) => double.IsFinite(v) && v > 0;
        if (!Positive(MilliampsPerVolt)) throw new ArgumentException("MilliampsPerVolt は正の値にしてください。");
        if (!double.IsFinite(InitialGainUncertainty) || InitialGainUncertainty < 0 || InitialGainUncertainty >= 0.5)
            throw new ArgumentException("InitialGainUncertainty は 0 以上 0.5 未満にしてください。");
        if (!Positive(ToleranceMilliamps)) throw new ArgumentException("ToleranceMilliamps は正の値にしてください。");
        if (!Positive(MaxMilliamps)) throw new ArgumentException("MaxMilliamps は正の値にしてください。");
        if (!double.IsFinite(MinOffsetVolts) || !double.IsFinite(MaxOffsetVolts) || MinOffsetVolts >= MaxOffsetVolts)
            throw new ArgumentException("MinOffsetVolts < MaxOffsetVolts にしてください。");
        if (RequiredConsecutiveReadings < 1) throw new ArgumentException("RequiredConsecutiveReadings は 1 以上にしてください。");
        if (MaxAdjustments < 0) throw new ArgumentException("MaxAdjustments は 0 以上にしてください。");
    }
}
