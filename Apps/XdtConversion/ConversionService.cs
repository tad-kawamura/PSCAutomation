using System.Diagnostics;
using PSCAutomation.Wrappers.Excel;
using PSCAutomation.Wrappers.LoggerFiles;

namespace PSCAutomation.Apps.XdtConversion;

// 元: XdtConversion XdtConversion/ConversionService.cs。変更は namespace と using のみ。
// 間引きと「どの CH をどのシート・セルへ」の対応はアプリ固有なのでアプリ側に置く(読み書きは Wrappers/LoggerFiles・Wrappers/Excel)。

public sealed record Mapping(string Source, int Channel, string Sheet, string StartCell);
public sealed record ConversionReport(TimeSpan Total, TimeSpan ReadConvert, TimeSpan Save, string[] Details);

public static class ConversionService
{
    public static ConversionReport Convert(string template, string output,
        Dictionary<string, string> sources, IReadOnlyList<Mapping> mappings, int stride)
    {
        if (stride < 1) throw new ArgumentException("間引き数は1以上にしてください。");
        var total = Stopwatch.StartNew();
        var read = Stopwatch.StartNew();
        var columns = new List<TransferColumn>();
        var details = new List<string>();
        foreach (var group in mappings.GroupBy(x => x.Source))
        {
            if (!sources.TryGetValue(group.Key, out string? source) || !File.Exists(source))
                throw new ArgumentException($"{group.Key}のXDTファイルを指定してください。");
            var result = XdtReader.Read(source, group.Select(x => x.Channel));
            foreach (var mapping in group)
            {
                var values = result.Values[mapping.Channel];
                var sampled = new double[(values.Length - 1) / stride + 1];
                for (int i = 0; i < sampled.Length; i++) sampled[i] = values[i * stride];
                columns.Add(new(mapping.Sheet, mapping.StartCell, sampled));
                details.Add($"{group.Key} / {result.Info.Format} / CH{mapping.Channel} → {mapping.Sheet}!{mapping.StartCell} / {values.Length:N0}点 → {sampled.Length:N0}点");
            }
        }
        read.Stop();
        var save = Stopwatch.StartNew();
        TemplateWriter.Save(template, output, columns);
        save.Stop(); total.Stop();
        return new(total.Elapsed, read.Elapsed, save.Elapsed, details.ToArray());
    }
}
