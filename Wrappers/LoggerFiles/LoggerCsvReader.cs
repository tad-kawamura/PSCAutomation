using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PSCAutomation.Wrappers.LoggerFiles;

/// <summary>
/// CSV から読み出す 1 列の指定。
/// </summary>
/// <param name="Channel">LOGGER の CH 番号(1 始まり)。列の自動検出で見出しの「CH n」「1-n」と照合する。</param>
/// <param name="LabelKeyword">列の自動検出で見出しに含まれていれば一致とみなす語(例: "電流")。空なら CH 番号だけで探す。</param>
/// <param name="ColumnNumber">CSV の列番号(1 始まり)。0 は自動検出。</param>
public sealed record LoggerCsvColumn(int Channel, string LabelKeyword, int ColumnNumber = 0);

/// <summary>
/// WAVE LOGGER / WAVE LOGGER X の標準 CSV 出力から、指定した列の測定値を読む。
/// LOGGER 側でスケーリング・平滑化を適用した値をそのまま返し、圧縮(間引き)はしない。列が分からないときは推測せず例外にする。
/// 元: AS110AllCheck Conversion/LoggerCsvReader.cs(NR-X の CSV を電流・圧力の 2 列で読んでいた)。
/// アプリ固有の設定クラス(TransferSettings)の代わりに、CH 番号・列番号・点数を引数で受け取る形にした。判定の中身は元のまま。
/// </summary>
public static class LoggerCsvReader
{
    /// <summary>
    /// 指定した列の値を、columns と同じ順で返す。
    /// <para>
    /// 列番号は全部指定するか、全部 0(自動)にする。1 つでも 0 があると、全部の列を見出しから探し直す(元の動作)。
    /// 自動検出は、各列の候補がちょうど 1 つずつで、互いに違う列になった最初の見出し行を採用する。
    /// </para>
    /// <para>
    /// 値は「全列が数値として読める行」の連続区間ごとにまとめ、点数が expectedSamples の区間がちょうど 1 つのときだけ返す。
    /// </para>
    /// </summary>
    public static IReadOnlyList<double[]> Read(string path, IReadOnlyList<LoggerCsvColumn> columns, int expectedSamples)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0) throw new ArgumentException("読み出す列を 1 つ以上指定してください。", nameof(columns));
        if (expectedSamples < 1) throw new ArgumentOutOfRangeException(nameof(expectedSamples), "点数は 1 以上です。");

        string[][] lines = ReadLines(path);
        int[] indexes = columns.Select(c => c.ColumnNumber - 1).ToArray();
        if (indexes.Any(i => i < 0))
        {
            bool found = false;
            foreach (var row in lines)
            {
                var candidates = columns
                    .Select(c => Enumerable.Range(0, row.Length).Where(i => Match(row[i], c.Channel, c.LabelKeyword)).ToArray())
                    .ToArray();
                if (candidates.All(a => a.Length == 1) && candidates.Select(a => a[0]).Distinct().Count() == candidates.Length)
                {
                    indexes = candidates.Select(a => a[0]).ToArray();
                    found = true;
                    break;
                }
            }
            if (!found)
                throw new InvalidDataException("CSV の列を特定できません。CSV の列番号(1 始まり)を指定してください。");
        }
        if (indexes.Distinct().Count() != indexes.Length)
            throw new InvalidDataException("CSV の複数の項目に同じ列が指定されています。");

        int maxIndex = indexes.Max();
        var runs = new List<List<double[]>>();
        var run = new List<double[]>();
        foreach (var row in lines)
        {
            double[]? values = row.Length > maxIndex ? TryParseRow(row, indexes) : null;
            if (values is not null) run.Add(values);
            else if (run.Count > 0) { runs.Add(run); run = []; }
        }
        if (run.Count > 0) runs.Add(run);

        var matches = runs.Where(r => r.Count == expectedSamples).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException("CSV に設定点数の測定値を一意に確認できません。標準出力・列設定・点数を確認してください。");
        return Enumerable.Range(0, indexes.Length)
            .Select(j => matches[0].Select(v => v[j]).ToArray())
            .ToArray();
    }

    private static double[]? TryParseRow(string[] row, int[] indexes)
    {
        var values = new double[indexes.Length];
        for (int j = 0; j < indexes.Length; j++)
        {
            if (!double.TryParse(row[indexes[j]], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
                return null;
            values[j] = v;
        }
        return values;
    }

    // UTF-8(BOM の有無とも)、UTF-16LE(BOM 付き)、それ以外は CP932 として読む。区切りは行ごとにタブかカンマ。
    private static string[][] ReadLines(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] bytes = File.ReadAllBytes(path);
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { text = Encoding.GetEncoding(932).GetString(bytes); }
        if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) text = Encoding.Unicode.GetString(bytes);
        return text.TrimStart('\uFEFF').Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(Split).ToArray();
    }

    private static string[] Split(string line)
    {
        var result = new List<string>();
        var value = new StringBuilder();
        bool quote = false;
        char delimiter = line.Contains('\t') ? '\t' : ',';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (quote && i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; }
                else quote = !quote;
            }
            else if (c == delimiter && !quote) { result.Add(value.ToString().Trim()); value.Clear(); }
            else value.Append(c);
        }
        result.Add(value.ToString().Trim());
        return result.ToArray();
    }

    private static bool Match(string label, int channel, string keyword) =>
        (!string.IsNullOrEmpty(keyword) && label.Contains(keyword)) ||
        Regex.IsMatch(label, $@"(?i)(?:\bCH\s*0*{channel}\b|^\s*1[-:]0*{channel}\s*$)");
}
