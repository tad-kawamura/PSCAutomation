using System.Globalization;
using System.Text;
using PSCAutomation.Wrappers.LoggerFiles;

namespace PSCAutomation.Apps.AS100201SwitchController;

// 測定中の取り込みと加工(SPEC_測定中の取り込み.md、2026-11-11 承認)の「加工」。機器に依存しない。
// 「測定開始から n 点目まで」の換算後の値から、大きさ・傾きなどの値を計算する。
// 熟練者の判断を前提にせず、どの波形にも当てはまる一般的な値を広く取る(人の方針)。
// 同じ計算を保存した xdt の先頭 n 点に当てはめると、測定中に計算した値と同じ文字列になる
// (毎回、先頭から計算し直す。途中までの合計を持ち越さないので、計算の順番による差が出ない)。

// 雛形の 1 チャンネル。API のチャンネルは xdt の CH 番号 − 1(LiveReadCheck で確認)。
internal sealed record As100201FeatureChannel(int Number, string Name, string Unit, double Gain, double Offset)
{
    public int ApiChannel => Number - 1;
    public string Label => $"CH{Number}" + (string.IsNullOrWhiteSpace(Name) ? "" : " " + Name.Trim());
    // XdtChannel.ReadScaledValues と同じ換算(float に丸める)。
    public double Scale(float raw) => (float)(Gain * raw + Offset);
}

// 取り込みに使う雛形の情報(チャンネルと周期)。雛形を開く前に xdt から読む。
internal sealed record As100201CaptureTemplate(IReadOnlyList<As100201FeatureChannel> Channels, double IntervalSeconds)
{
    public static As100201CaptureTemplate FromXdt(XdtFile file)
    {
        double dt = file.SampleInterval?.TotalSeconds ?? throw new InvalidDataException("雛形からサンプリング周期を読めません。");
        if (dt <= 0) throw new InvalidDataException($"雛形のサンプリング周期が不正です（{dt} 秒）。");
        var channels = file.Channels.Select(c => new As100201FeatureChannel(c.Info.Number, c.Info.Name ?? "", c.Info.Unit ?? "", c.Info.Gain, c.Info.Offset))
            .ToList();
        if (channels.Count == 0) throw new InvalidDataException("雛形にチャンネルがありません。");
        if (channels.Any(c => c.Number < 1)) throw new InvalidDataException("雛形のチャンネル番号が不正です。");
        return new As100201CaptureTemplate(channels, dt);
    }

    public static As100201CaptureTemplate Load(string path) => FromXdt(XdtFile.Load(path));
}

internal static class As100201LiveFeatures
{
    // 指令のチャンネル(雛形の CH1 指令)。向き・指令に対する傾き・上りと下りの区間はこれを基準にする。
    public const int CommandChannelNumber = 1;
    // 「今」の値: 直近 0.2 秒の平均(取り込みの間隔と同じ)。
    public const double LatestWindowSeconds = 0.2;
    // 時間に対する傾き: 直近 1 秒・5 秒の回帰直線。窓に足りない間は空欄。
    public static readonly double[] TimeSlopeWindowsSeconds = [1, 5];
    // 指令に対する傾き: 直近 5 秒の「値 対 指令」の回帰直線。
    public const double CommandSlopeWindowSeconds = 5;
    // 指令の向き: 1 秒前との差(単位 / 秒)。この大きさより小さければ「平ら」。
    // 三角波 10.2Vpp・0.02Hz の指令は約 0.4 V/秒で動くので、その 1/8 ほど(ノイズで向きが揺れない大きさ)。AI の仮決め。
    public const double DirectionWindowSeconds = 1;
    public const double DirectionThresholdPerSecond = 0.05;
    // 上りと下りの差: 指令の範囲(測定開始からの最小〜最大)を 10 区間に分ける。
    public const int CommandBins = 10;

    public const string Up = "上り";
    public const string Down = "下り";
    public const string Flat = "平ら";

    // 列名(加工した値の部分)。行の情報(取り込み回・時刻など)は As100201LiveCapture が前に足す。
    public static IReadOnlyList<string> Header(IReadOnlyList<As100201FeatureChannel> channels)
    {
        var h = new List<string> { "点数", "データの秒", "指令の向き" };
        foreach (var c in channels)
        {
            string p = Prefix(c);
            h.Add(p + "今");
            h.Add(p + "最大");
            h.Add(p + "最小");
            h.Add(p + "平均");
            h.Add(p + "標準偏差");
            foreach (double w in TimeSlopeWindowsSeconds) h.Add(p + $"傾き_直近{w:0.#}秒(/秒)");
        }
        foreach (var c in channels.Where(c => c.Number != CommandChannelNumber))
            h.Add(Prefix(c) + $"指令に対する傾き_直近{CommandSlopeWindowSeconds:0.#}秒");
        foreach (var c in channels.Where(c => c.Number != CommandChannelNumber))
        {
            for (int b = 1; b <= CommandBins; b++)
            {
                h.Add(Prefix(c) + $"区間{b}_上り平均");
                h.Add(Prefix(c) + $"区間{b}_下り平均");
                h.Add(Prefix(c) + $"区間{b}_上り−下り");
            }
        }
        return h;
    }

    private static string Prefix(As100201FeatureChannel c) =>
        c.Label + (string.IsNullOrWhiteSpace(c.Unit) ? "" : $"[{c.Unit.Trim()}]") + " ";

    // values[i] は channels[i] の換算後の値(先頭から n 点以上)。先頭 n 点で計算する。
    public static IReadOnlyList<string> Compute(IReadOnlyList<As100201FeatureChannel> channels,
        IReadOnlyList<IReadOnlyList<double>> values, int n, double dt)
    {
        if (values.Count != channels.Count) throw new ArgumentException("チャンネルと値の数が合いません。", nameof(values));
        if (values.Any(v => v.Count < n)) throw new ArgumentException("値が点数より少ないチャンネルがあります。", nameof(values));
        int commandIndex = IndexOf(channels, CommandChannelNumber);
        IReadOnlyList<double>? command = commandIndex < 0 ? null : values[commandIndex];
        int directionWindow = Points(DirectionWindowSeconds, dt);

        var row = new List<string> { n.ToString(CultureInfo.InvariantCulture), Format(n * dt) };
        row.Add(command is null ? "" : DirectionText(Direction(command, n - 1, directionWindow, dt)));

        int latest = Points(LatestWindowSeconds, dt);
        for (int c = 0; c < channels.Count; c++)
        {
            var v = values[c];
            if (n == 0)
            {
                row.AddRange(Enumerable.Repeat("", 5 + TimeSlopeWindowsSeconds.Length));
                continue;
            }
            row.Add(Format(Mean(v, Math.Max(0, n - latest), n)));
            double max = double.NegativeInfinity, min = double.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                if (v[i] > max) max = v[i];
                if (v[i] < min) min = v[i];
            }
            double mean = Mean(v, 0, n);
            double ss = 0;
            for (int i = 0; i < n; i++) ss += (v[i] - mean) * (v[i] - mean);
            row.Add(Format(max));
            row.Add(Format(min));
            row.Add(Format(mean));
            row.Add(Format(Math.Sqrt(ss / n)));
            foreach (double w in TimeSlopeWindowsSeconds)
            {
                int points = Points(w, dt);
                row.Add(n >= points && points >= 2 ? Format(TimeSlope(v, n - points, n, dt)) : "");
            }
        }

        int commandSlopePoints = Points(CommandSlopeWindowSeconds, dt);
        for (int c = 0; c < channels.Count; c++)
        {
            if (c == commandIndex) continue;
            row.Add(command is not null && n >= commandSlopePoints && commandSlopePoints >= 2
                ? FormatOrBlank(Regression(command, values[c], n - commandSlopePoints, n)) : "");
        }

        AddBins(row, channels, values, command, commandIndex, n, dt, directionWindow);
        return row;
    }

    // 指令の範囲(先頭 n 点の最小〜最大)を 10 区間に分け、向きが上り・下りの点ごとに区間の平均を取る。
    // 向きが決まらない点(先頭 1 秒・平ら)は使わない。
    private static void AddBins(List<string> row, IReadOnlyList<As100201FeatureChannel> channels, IReadOnlyList<IReadOnlyList<double>> values,
        IReadOnlyList<double>? command, int commandIndex, int n, double dt, int directionWindow)
    {
        int others = channels.Count - (commandIndex < 0 ? 0 : 1);
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        if (command is not null)
            for (int i = 0; i < n; i++) { lo = Math.Min(lo, command[i]); hi = Math.Max(hi, command[i]); }
        if (command is null || n == 0 || !(hi > lo))
        {
            row.AddRange(Enumerable.Repeat("", others * CommandBins * 3));
            return;
        }

        var bin = new int[n];
        var dir = new int[n];
        for (int i = 0; i < n; i++)
        {
            bin[i] = Math.Min(CommandBins - 1, (int)Math.Floor((command[i] - lo) / (hi - lo) * CommandBins));
            dir[i] = Direction(command, i, directionWindow, dt);
        }
        for (int c = 0; c < channels.Count; c++)
        {
            if (c == commandIndex) continue;
            var v = values[c];
            var upSum = new double[CommandBins]; var upCount = new int[CommandBins];
            var downSum = new double[CommandBins]; var downCount = new int[CommandBins];
            for (int i = 0; i < n; i++)
            {
                if (dir[i] > 0) { upSum[bin[i]] += v[i]; upCount[bin[i]]++; }
                else if (dir[i] < 0) { downSum[bin[i]] += v[i]; downCount[bin[i]]++; }
            }
            for (int b = 0; b < CommandBins; b++)
            {
                double? up = upCount[b] > 0 ? upSum[b] / upCount[b] : null;
                double? down = downCount[b] > 0 ? downSum[b] / downCount[b] : null;
                row.Add(up is double u ? Format(u) : "");
                row.Add(down is double d ? Format(d) : "");
                row.Add(up is double u2 && down is double d2 ? Format(u2 - d2) : "");
            }
        }
    }

    // i 点目の指令の向き: 1 秒前との差。+1 上り / -1 下り / 0 平ら / 2 まだ決まらない(先頭 1 秒)。
    private static int Direction(IReadOnlyList<double> command, int i, int window, double dt)
    {
        if (i < window || window < 1) return 2;
        double perSecond = (command[i] - command[i - window]) / (window * dt);
        return perSecond > DirectionThresholdPerSecond ? 1 : perSecond < -DirectionThresholdPerSecond ? -1 : 0;
    }

    private static string DirectionText(int direction) => direction switch { 1 => Up, -1 => Down, 0 => Flat, _ => "" };

    private static int Points(double seconds, double dt) => Math.Max(1, (int)Math.Round(seconds / dt));

    private static int IndexOf(IReadOnlyList<As100201FeatureChannel> channels, int number)
    {
        for (int i = 0; i < channels.Count; i++) if (channels[i].Number == number) return i;
        return -1;
    }

    private static double Mean(IReadOnlyList<double> v, int from, int to)
    {
        double sum = 0;
        for (int i = from; i < to; i++) sum += v[i];
        return sum / (to - from);
    }

    // 時間(i × dt)に対する回帰直線の傾き。
    private static double TimeSlope(IReadOnlyList<double> v, int from, int to, double dt)
    {
        int m = to - from;
        double tMean = (m - 1) / 2.0;
        double yMean = Mean(v, from, to);
        double sxy = 0, sxx = 0;
        for (int k = 0; k < m; k++)
        {
            double t = k - tMean;
            sxy += t * (v[from + k] - yMean);
            sxx += t * t;
        }
        return sxy / sxx / dt;
    }

    // y 対 x の回帰直線の傾き。x がほとんど動いていなければ null(空欄)。
    private static double? Regression(IReadOnlyList<double> x, IReadOnlyList<double> y, int from, int to)
    {
        double xMean = Mean(x, from, to), yMean = Mean(y, from, to);
        double sxy = 0, sxx = 0;
        for (int i = from; i < to; i++)
        {
            sxy += (x[i] - xMean) * (y[i] - yMean);
            sxx += (x[i] - xMean) * (x[i] - xMean);
        }
        return sxx > 1e-12 ? sxy / sxx : null;
    }

    public static string Format(double value) => value.ToString("G7", CultureInfo.InvariantCulture);
    private static string FormatOrBlank(double? value) => value is double d ? Format(d) : "";

    // CSV の 1 行(カンマ・引用符・改行を含む欄は引用符で囲む)。
    public static string CsvLine(IEnumerable<string> fields) => string.Join(",", fields.Select(f =>
        f.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + f.Replace("\"", "\"\"") + "\"" : f));

    // CSV の 1 行を欄に分ける(CsvLine の逆。比較用)。
    public static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
