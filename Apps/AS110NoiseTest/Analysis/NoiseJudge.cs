namespace PSCAutomation.Apps.AS110NoiseTest.Analysis;

internal enum NoiseGrade
{
    Normal, // 無色
    Yellow, // p-p が大きい
    Red,    // 矩形波的な変動(台地 / 低周波構造 / 段差)
}

// 1 本の波形に対する判定結果と、判定に使った指標。Excel の詳細シートにそのまま残す。
internal sealed record NoiseMetrics(
    double PeakToPeakKpa,        // max − min
    int PlateauRunSamples,       // 中央値 ±しきい値 の外に連続した最長サンプル数
    double LowHighPowerRatio,    // 低周波帯 / 高周波帯 の平均パワー比
    double StepKpa,              // 隣接区間平均の差の最大
    double StepT,                // 差がしきい値以上の点の中での t 値(差 ÷ 区間内ばらつき)の最大。該当点がなければ 0
    NoiseGrade Grade,
    bool PlateauHit, bool LowFrequencyHit, bool StepHit);

// 制御圧力 [kPa] の時系列から 無色 / 黄 / 赤 を判定する。
// 計算は Tools/Analysis/red_rule_eval.py(Python、規則の検証用)と同じ。両方を直すこと。
internal static class NoiseJudge
{
    public static NoiseMetrics Evaluate(double[] kpa, NoiseJudgeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(kpa);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        int n = kpa.Length;
        if (n < 2 * settings.StepWindowSamples + 1)
            throw new ArgumentException($"サンプル数 {n} は少なすぎます(段差判定に {2 * settings.StepWindowSamples + 1} 点以上必要)。");
        foreach (double v in kpa) if (!double.IsFinite(v)) throw new ArgumentException("波形に非数値が含まれています。");

        double min = kpa.Min(), max = kpa.Max();
        double pp = max - min;
        double median = Median(kpa);

        double edge = settings.PlateauThresholdKpa - settings.QuantizationMarginKpa;
        int run = Math.Max(
            LongestRun(kpa, v => v - median >= edge),
            LongestRun(kpa, v => median - v >= edge));

        double ratio = LowHighPowerRatio(kpa, settings);
        (double stepKpa, double stepT) = LargestStep(kpa, settings.StepWindowSamples, settings.StepMinKpa);

        bool plateau = run >= settings.PlateauMinSamples;
        bool lowFreq = ratio >= settings.LowHighPowerRatioMin;
        bool step = stepT >= settings.StepMinT;   // stepT は差が StepMinKpa 以上の点だけから求めている
        NoiseGrade grade =
            plateau || lowFreq || step ? NoiseGrade.Red :
            Math.Round(pp, 1) >= settings.YellowPeakToPeakKpa ? NoiseGrade.Yellow :
            NoiseGrade.Normal;
        return new(pp, run, ratio, stepKpa, stepT, grade, plateau, lowFreq, step);
    }

    // numpy.median と同じ(偶数個なら中央 2 点の平均)。
    private static double Median(double[] x)
    {
        var s = (double[])x.Clone();
        Array.Sort(s);
        int n = s.Length;
        return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2;
    }

    private static int LongestRun(double[] x, Func<double, bool> hit)
    {
        int best = 0, cur = 0;
        foreach (double v in x)
        {
            cur = hit(v) ? cur + 1 : 0;
            if (cur > best) best = cur;
        }
        return best;
    }

    // 平均を引いてハニング窓を掛け、必要な周波数ビンだけ直接 DFT で求める。
    // 2000 点・約 880 ビンで数 ms。FFT ライブラリは使わない。
    private static double LowHighPowerRatio(double[] x, NoiseJudgeSettings s)
    {
        int n = x.Length;
        double mean = x.Average();
        var xw = new double[n];
        for (int i = 0; i < n; i++)
            xw[i] = (x[i] - mean) * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)));   // numpy.hanning(n)

        // 回転因子 e^{-2πi m/n} の表。k·i mod n で引く(厳密な DFT)。
        var cos = new double[n];
        var sin = new double[n];
        for (int m = 0; m < n; m++) { cos[m] = Math.Cos(2 * Math.PI * m / n); sin[m] = Math.Sin(2 * Math.PI * m / n); }

        double duration = n * s.SampleIntervalSeconds;   // numpy.fft.rfftfreq: k / (n·dt)
        double lowSum = 0, highSum = 0;
        int lowCount = 0, highCount = 0;
        for (int k = 0; k <= n / 2; k++)
        {
            double f = k / duration;
            bool low = f >= s.LowBandMinHz && f < s.LowBandMaxHz;
            bool high = f >= s.HighBandMinHz && f < s.HighBandMaxHz;
            if (!low && !high) continue;
            double re = 0, im = 0;
            long idx = 0;
            for (int i = 0; i < n; i++)
            {
                re += xw[i] * cos[idx];
                im -= xw[i] * sin[idx];
                idx += k; if (idx >= n) idx -= n;
            }
            double power = re * re + im * im;
            if (low) { lowSum += power; lowCount++; }
            else { highSum += power; highCount++; }
        }
        if (lowCount == 0 || highCount == 0) throw new InvalidOperationException("周波数帯にビンがありません。サンプル数か周波数帯の設定を確認してください。");
        return (lowSum / lowCount) / (highSum / highCount);
    }

    // 隣接する w 点ずつの区間 [i, i+w) と [i+w, i+2w) の平均の差 d を全点で求め、
    // |d| の最大と、|d| ≥ minKpa の点での t = |d| ÷ sqrt((σ₁² + σ₂²) / w) の最大を返す
    // (σ は母標準偏差、Python 版と同じ。該当点がなければ t は 0)。
    // 「|d| 最大の 1 点の t」にしないのは、|d| がほぼ同点の候補が複数あると丸めで選ばれる点が変わり t が不安定になるため。
    private static (double StepKpa, double StepT) LargestStep(double[] x, int w, double minKpa)
    {
        int n = x.Length;
        int windows = n - w + 1;
        var mean = new double[windows];
        var var_ = new double[windows];
        for (int i = 0; i < windows; i++)
        {
            double sum = 0;
            for (int j = i; j < i + w; j++) sum += x[j];
            double m = sum / w, sq = 0;
            for (int j = i; j < i + w; j++) { double d = x[j] - m; sq += d * d; }
            mean[i] = m; var_[i] = sq / w;
        }
        double maxAbs = 0, maxT = 0;
        for (int i = 0; i + w < windows; i++)
        {
            double a = Math.Abs(mean[i + w] - mean[i]);
            if (a > maxAbs) maxAbs = a;
            if (a < minKpa) continue;
            double t = a / (Math.Sqrt((var_[i + w] + var_[i]) / w) + 1e-9);
            if (t > maxT) maxT = t;
        }
        return (maxAbs, maxT);
    }
}
