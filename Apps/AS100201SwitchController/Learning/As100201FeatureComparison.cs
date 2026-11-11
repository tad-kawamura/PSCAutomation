using System.Globalization;
using PSCAutomation.Wrappers.LoggerFiles;

namespace PSCAutomation.Apps.AS100201SwitchController;

// 取り込みの CSV と、保存した xdt から計算し直した値の突き合わせ(SPEC_測定中の取り込み.md の確認用)。
// CSV の各行の「点数」で xdt の先頭を切り出して計算し、加工した値の欄が 1 文字も違わないことを確かめる。
// 行の情報(取り込み回・時刻・F8からの秒・側・位相)は xdt に無いので比べない。
internal static class As100201FeatureComparison
{
    public static List<string> Compare(string xdtPath, string csvPath)
    {
        var lines = File.ReadAllLines(csvPath, System.Text.Encoding.UTF8).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) return ["CSV が空です"];
        var header = As100201LiveFeatures.ParseCsvLine(lines[0]);
        var rows = lines.Skip(1).Select(l => As100201LiveFeatures.ParseCsvLine(l).ToArray()).ToList();
        return Compare(XdtFile.Load(xdtPath), header, rows);
    }

    public static List<string> Compare(XdtFile xdt, IReadOnlyList<string> header, IReadOnlyList<string[]> rows)
    {
        var problems = new List<string>();
        var template = As100201CaptureTemplate.FromXdt(xdt);
        var expectedHeader = As100201LiveCapture.InfoHeader.Concat(As100201LiveFeatures.Header(template.Channels)).ToList();
        if (!header.SequenceEqual(expectedHeader))
        {
            problems.Add("列名が xdt のチャンネルから作った列名と違います");
            return problems;
        }
        var values = xdt.Channels.Select(c => (IReadOnlyList<double>)c.ReadScaledValues()).ToList();
        int info = As100201LiveCapture.InfoHeader.Length;
        foreach (var row in rows)
        {
            if (row.Length != header.Count) { problems.Add($"取り込み回 {row.FirstOrDefault()}: 欄の数 {row.Length} / {header.Count}"); continue; }
            if (!int.TryParse(row[info], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 0 || n > xdt.SampleCount)
            {
                problems.Add($"取り込み回 {row[0]}: 点数 {row[info]} が xdt の点数 {xdt.SampleCount} を超えるか読めません");
                continue;
            }
            var expected = As100201LiveFeatures.Compute(template.Channels, values, n, template.IntervalSeconds);
            for (int i = 0; i < expected.Count; i++)
            {
                if (row[info + i] == expected[i]) continue;
                problems.Add($"取り込み回 {row[0]}（{n} 点）{header[info + i]}: CSV {row[info + i]} / xdt {expected[i]}");
                break;
            }
        }
        if (rows.Count > 0 && rows[^1][info] != xdt.SampleCount.ToString(CultureInfo.InvariantCulture))
            problems.Add($"最後の行の点数 {rows[^1][info]} が xdt の点数 {xdt.SampleCount} と違います");
        return problems;
    }
}
