using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using PSCAutomation.Apps.AS110NoiseTest.Analysis;
using PSCAutomation.Apps.AS110NoiseTest.Excel;
using PSCAutomation.Wrappers.LoggerFiles;

namespace PSCAutomation.Apps.AS110NoiseTest.Tools.ExcelWriteCheck;

// Excel 出力(Excel/NoiseResultWorkbook)の確認ツール。機器なしで、既存の xdt を雛形 Excel に書き込む。
//   ExcelWriteCheck <出力xlsx> <xdt またはフォルダ>... [--model 型式] [--memo 備忘録] [--sn SN] [--visible] [--leave-open]
// 型式・SN・備忘録・mA はファイル名 AS110_035_<備忘録>_<SN>_<mA>mA_... から取る(型式は最初の _ を - に戻して AS110-035。
// --model / --memo / --sn で上書き)。
// 測定日時は xdt のヘッダ(無ければファイルの更新時刻)。出力 xlsx が無ければ 雛形/AS110ノイズ測定_雛形.xlsx から作り、あれば追記する。
internal static class Program
{
    private static readonly Regex NamePattern = new(@"^(?<model>[A-Za-z]+\d+_\d+)_(?<memo>.+)_(?<sn>SN[^_]+)_(?<ma>\d+(?:\.\d+)?)mA_", RegexOptions.Compiled);

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string? output = null, modelOverride = null, memoOverride = null, snOverride = null;
            bool visible = false, leaveOpen = false;
            var inputs = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--model": modelOverride = args[++i]; break;
                    case "--memo": memoOverride = args[++i]; break;
                    case "--sn": snOverride = args[++i]; break;
                    case "--visible": visible = true; break;
                    case "--leave-open": leaveOpen = visible = true; break;
                    default:
                        if (output is null) output = args[i]; else inputs.Add(args[i]);
                        break;
                }
            }
            if (output is null || inputs.Count == 0) { Usage(); return 2; }

            var files = inputs.SelectMany(p => Directory.Exists(p)
                    ? Directory.EnumerateFiles(p, "*.xdt", SearchOption.AllDirectories)
                    : [p])
                .Select(Path.GetFullPath).Distinct().ToList();
            if (files.Count == 0) throw new FileNotFoundException("xdt がありません。");

            string template = Path.Combine(FindRepoRoot(), "雛形", "AS110ノイズ測定_雛形.xlsx");
            var judge = new NoiseJudgeSettings();
            var channels = new LoggerChannelMap();

            // ファイル名から SN・備忘録・mA を取り、SN → mA の順に並べる
            var items = new List<(string Path, string Model, string Sn, string Memo, double Ma)>();
            foreach (string f in files)
            {
                var m = NamePattern.Match(Path.GetFileName(f));
                string sn = snOverride ?? (m.Success ? m.Groups["sn"].Value : throw new InvalidDataException($"ファイル名から SN を読めません: {f}(--sn で指定)"));
                string memo = memoOverride ?? (m.Success ? m.Groups["memo"].Value : "");
                string model = modelOverride ?? (m.Success ? ModelFromFileName(m.Groups["model"].Value)
                    : throw new InvalidDataException($"ファイル名から型式を読めません: {f}(--model で指定)"));
                double ma = m.Success ? double.Parse(m.Groups["ma"].Value, CultureInfo.InvariantCulture)
                    : throw new InvalidDataException($"ファイル名から mA を読めません: {f}");
                items.Add((f, model, sn, memo, ma));
            }
            items = items.OrderBy(x => x.Sn, StringComparer.Ordinal).ThenBy(x => x.Ma).ToList();

            var total = Stopwatch.StartNew();
            using var wb = NoiseResultWorkbook.Open(output, template, visible);
            Console.WriteLine($"Excel 起動・ブック準備: {total.ElapsedMilliseconds} ms  -> {wb.Path}");
            wb.WriteSettings(judge);
            var rows = new SortedDictionary<int, (string Model, string Sn, string Memo)>();
            double writeMsTotal = 0, writeMsMax = 0;
            foreach (var it in items)
            {
                var analysis = XdtNoiseAnalyzer.Analyze(it.Path, judge, channels);
                DateTime at = XdtTimestamp.TryRead(it.Path) ?? File.GetLastWriteTime(it.Path);
                var record = new NoiseResultRecord(at, it.Model, it.Sn, it.Memo, it.Ma, null, analysis, Path.GetFileName(it.Path));
                var sw = Stopwatch.StartNew();
                int row = wb.Write(record);
                sw.Stop();
                writeMsTotal += sw.Elapsed.TotalMilliseconds; writeMsMax = Math.Max(writeMsMax, sw.Elapsed.TotalMilliseconds);
                rows[row] = (it.Model, it.Sn, it.Memo);
                Console.WriteLine($"  行{row} {it.Model} {it.Sn} {it.Memo} {it.Ma}mA  {at:yyyy/MM/dd HH:mm:ss}  p-p={analysis.Metrics.PeakToPeakKpa:F1}  mean={analysis.ControlPressure.Mean:F1}  判定={analysis.Metrics.Grade}  ({sw.Elapsed.TotalMilliseconds:F0} ms)");
            }
            wb.Save();
            Console.WriteLine($"{items.Count} 本を書き込み: 1 本あたり平均 {writeMsTotal / items.Count:F0} ms、最大 {writeMsMax:F0} ms。保存済み({total.ElapsedMilliseconds} ms)");
            Console.WriteLine("Excel で再計算した行の集計:");
            foreach (var kv in rows)
            {
                var s = wb.ReadRowSummary(kv.Key);
                Console.WriteLine($"  行{kv.Key} {kv.Value.Model} {kv.Value.Sn} {kv.Value.Memo}: 最大p-p={s.MaxPp?.ToString("F1") ?? "-"}  黄={s.Yellow?.ToString() ?? "-"}  赤={s.Red?.ToString() ?? "-"}  判定={s.Judge}");
            }
            if (leaveOpen) wb.LeaveOpenForUser();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("エラー: " + ex.Message);
            return 1;
        }
    }

    private static void Usage()
    {
        Console.WriteLine("使い方: ExcelWriteCheck <出力xlsx> <xdt またはフォルダ>... [--model 型式] [--memo 備忘録] [--sn SN] [--visible] [--leave-open]");
    }

    // ファイル名の型式(AS110_035)を一覧の表記(AS110-035)に戻す。最初の _ だけを - にする。
    private static string ModelFromFileName(string text)
    {
        int i = text.IndexOf('_');
        return i < 0 ? text : text[..i] + "-" + text[(i + 1)..];
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AS110NoiseTest.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("リポジトリ直下(AS110NoiseTest.csproj)が見つかりません。");
    }
}
