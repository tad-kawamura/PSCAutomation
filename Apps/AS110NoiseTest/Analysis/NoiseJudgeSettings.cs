namespace PSCAutomation.Apps.AS110NoiseTest.Analysis;

// 制御圧力ノイズの合否(無色 / 黄 / 赤)のしきい値。値はコードに直書きせず、ここから渡す。
// 既定値は docs/赤判定規則メモ.md のとおり(学習データ 426 件で人の判定と全件一致)。
// 前提: 20 ms サンプリング・2000 点(40 秒)。サンプリングが変わったら SampleIntervalSeconds と
// サンプル数で指定している値(PlateauMinSamples, StepWindowSamples)を見直す。
internal sealed record NoiseJudgeSettings
{
    // サンプリング周期 [s]。周波数帯の計算に使う。
    public double SampleIntervalSeconds { get; init; } = 0.02;

    // 黄: p-p(max − min)を 0.1 kPa に丸めた値がこれ以上。人は LOGGER の表示値(0.1 kPa 刻み)で判定していた。
    public double YellowPeakToPeakKpa { get; init; } = 1.4;

    // 赤 (A) 台地: 中央値から ±PlateauThresholdKpa 以上離れた生サンプルが PlateauMinSamples 以上連続。
    // データは 0.1 kPa 刻みなので「以上」は QuantizationMarginKpa の余裕を付けて判定する
    // (xdt 読込時の float 丸めで、ちょうど ±0.4 のサンプルがどちらに転ぶかが変わらないように)。
    public double PlateauThresholdKpa { get; init; } = 0.4;
    public int PlateauMinSamples { get; init; } = 15;
    public double QuantizationMarginKpa { get; init; } = 0.01;

    // 赤 (B) 低周波構造: 低周波帯の平均パワー ÷ 高周波帯の平均パワー がこれ以上(ハニング窓)。
    // 白色ノイズなら 1.5〜3、浅い矩形が繰り返すと 10 以上になる。
    public double LowHighPowerRatioMin { get; init; } = 7.8;
    public double LowBandMinHz { get; init; } = 0.1;
    public double LowBandMaxHz { get; init; } = 2.0;   // 未満
    public double HighBandMinHz { get; init; } = 5.0;
    public double HighBandMaxHz { get; init; } = 25.0; // 未満

    // 赤 (C) 段差: 隣接する StepWindowSamples 点ずつの平均の差が StepMinKpa 以上の点のうち、
    // 差 ÷ 区間内ばらつき(t 値)が StepMinT 以上の点が 1 つでもある。
    public int StepWindowSamples { get; init; } = 100;
    public double StepMinKpa { get; init; } = 0.35;
    public double StepMinT { get; init; } = 6.0;

    public void Validate()
    {
        static bool Positive(double v) => double.IsFinite(v) && v > 0;
        if (!Positive(SampleIntervalSeconds)) throw new ArgumentException("SampleIntervalSeconds は正の値にしてください。");
        if (!Positive(YellowPeakToPeakKpa)) throw new ArgumentException("YellowPeakToPeakKpa は正の値にしてください。");
        if (!Positive(PlateauThresholdKpa)) throw new ArgumentException("PlateauThresholdKpa は正の値にしてください。");
        if (PlateauMinSamples < 1) throw new ArgumentException("PlateauMinSamples は 1 以上にしてください。");
        if (!double.IsFinite(QuantizationMarginKpa) || QuantizationMarginKpa < 0 || QuantizationMarginKpa >= PlateauThresholdKpa)
            throw new ArgumentException("QuantizationMarginKpa は 0 以上 PlateauThresholdKpa 未満にしてください。");
        if (!Positive(LowHighPowerRatioMin)) throw new ArgumentException("LowHighPowerRatioMin は正の値にしてください。");
        if (!(LowBandMinHz >= 0 && LowBandMinHz < LowBandMaxHz && LowBandMaxHz <= HighBandMinHz && HighBandMinHz < HighBandMaxHz))
            throw new ArgumentException("周波数帯は 0 ≤ LowMin < LowMax ≤ HighMin < HighMax にしてください。");
        if (StepWindowSamples < 2) throw new ArgumentException("StepWindowSamples は 2 以上にしてください。");
        if (!Positive(StepMinKpa)) throw new ArgumentException("StepMinKpa は正の値にしてください。");
        if (!Positive(StepMinT)) throw new ArgumentException("StepMinT は正の値にしてください。");
    }
}
