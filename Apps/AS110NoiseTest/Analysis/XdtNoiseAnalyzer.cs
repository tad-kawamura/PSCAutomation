using PSCAutomation.Wrappers.LoggerFiles;

namespace PSCAutomation.Apps.AS110NoiseTest.Analysis;

// 1 チャンネルの統計(スケーリング後の物理量)。Excel の詳細シートに残す。
internal sealed record ChannelStats(double Mean, double Min, double Max)
{
    public double PeakToPeak => Max - Min;

    public static ChannelStats Of(double[] values)
    {
        if (values.Length == 0) throw new ArgumentException("データがありません。");
        double sum = 0, min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (double v in values)
        {
            sum += v;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        return new(sum / values.Length, min, max);
    }
}

// LOGGER のチャンネル番号(1 始まり)。CLAUDE.md: CH1=電流、CH2=供給圧力、CH3=制御圧力、CH4=不明(名前「指令」)。
internal sealed record LoggerChannelMap
{
    public int Current { get; init; } = 1;
    public int SupplyPressure { get; init; } = 2;
    public int ControlPressure { get; init; } = 3;
    // 0 なら読まない。xdt に無ければ無視する。
    public int Extra { get; init; } = 4;
}

// 1 本の xdt から得た、Excel に書く値一式。
internal sealed record XdtNoiseAnalysis(
    int Samples,
    ChannelStats Current,          // [mA]
    ChannelStats SupplyPressure,   // [kPa]
    ChannelStats ControlPressure,  // [kPa]
    ChannelStats? Extra,           // CH4 [V](無ければ null)
    NoiseMetrics Metrics);         // 制御圧力の判定

// xdt を読んで、全チャンネルの統計と制御圧力の判定をまとめて出す。
internal static class XdtNoiseAnalyzer
{
    public static XdtNoiseAnalysis Analyze(string xdtPath, NoiseJudgeSettings judgeSettings, LoggerChannelMap channels)
    {
        ArgumentNullException.ThrowIfNull(judgeSettings);
        ArgumentNullException.ThrowIfNull(channels);
        var info = XdtReader.Inspect(xdtPath);
        var wanted = new List<int> { channels.Current, channels.SupplyPressure, channels.ControlPressure };
        bool hasExtra = channels.Extra > 0 && info.Channels.Any(c => c.Number == channels.Extra);
        if (hasExtra) wanted.Add(channels.Extra);
        var data = XdtReader.Read(xdtPath, wanted).Values;
        double[] control = data[channels.ControlPressure];
        return new(
            info.Samples,
            ChannelStats.Of(data[channels.Current]),
            ChannelStats.Of(data[channels.SupplyPressure]),
            ChannelStats.Of(control),
            hasExtra ? ChannelStats.Of(data[channels.Extra]) : null,
            NoiseJudge.Evaluate(control, judgeSettings));
    }
}
