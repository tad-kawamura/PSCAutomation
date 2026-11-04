using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PSCAutomation.Apps.AS110NoiseTest.Analysis;
using PSCAutomation.Wrappers.LoggerFiles;

namespace PSCAutomation.Apps.AS110NoiseTest.Tools.NoiseJudgeCheck;

// 判定規則(Analysis/NoiseJudge)の確認ツール。
//   NoiseJudgeCheck <xdt>                     1 本を判定して指標を表示
//   NoiseJudgeCheck --all [学習データのフォルダ]  配下の xdt を全部判定し、Tools/Analysis/noise_judge_cs.csv に書く
//                                             (Python 版 red_rule_scores.csv と Tools/Analysis/compare_cs.py で照合する)
// 制御圧力は CH3。xdt は Wrappers/LoggerFiles の XdtReader で読む(2 点スケーリング済みの kPa)。
internal static class Program
{
    private const int ControlPressureChannel = 3;

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) { Usage(); return 2; }
            if (args[0] == "--all")
            {
                string root = args.Length > 1 ? args[1] : FindDefaultRoot();
                return JudgeAll(root);
            }
            return JudgeOne(args[0]);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("エラー: " + ex.Message);
            return 1;
        }
    }

    private static void Usage()
    {
        Console.WriteLine("使い方: NoiseJudgeCheck <xdt>  または  NoiseJudgeCheck --all [学習データのフォルダ]");
    }

    private static int JudgeOne(string path)
    {
        var settings = new NoiseJudgeSettings();
        double[] kpa = XdtReader.Read(path, [ControlPressureChannel]).Values[ControlPressureChannel];
        var sw = Stopwatch.StartNew();
        var m = NoiseJudge.Evaluate(kpa, settings);
        sw.Stop();
        Console.WriteLine($"{Path.GetFileName(path)}  点数={kpa.Length}  判定={m.Grade}  ({sw.Elapsed.TotalMilliseconds:F2} ms)");
        Console.WriteLine($"  p-p={m.PeakToPeakKpa:F3} kPa  台地連続={m.PlateauRunSamples}  低周波/高周波比={m.LowHighPowerRatio:F2}  段差={m.StepKpa:F3} kPa (t={m.StepT:F1})");
        Console.WriteLine($"  赤の根拠: 台地={m.PlateauHit} 低周波={m.LowFrequencyHit} 段差={m.StepHit}");
        return 0;
    }

    private static int JudgeAll(string root)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var settings = new NoiseJudgeSettings();
        var files = Directory.EnumerateFiles(root, "*.xdt", SearchOption.AllDirectories)
            .Where(p => Regex.IsMatch(Path.GetFileName(p), @"_(\d+)mA_"))
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        string outPath = Path.Combine(FindRepoRoot(), "Tools", "Analysis", "noise_judge_cs.csv");
        var sb = new StringBuilder();
        sb.AppendLine("dataset,folder,mA,pp,run0.4,lfhf,step_d,step_t,judged");
        var counts = new Dictionary<NoiseGrade, int>();
        double totalMs = 0, maxMs = 0;
        foreach (string p in files)
        {
            string rel = Path.GetRelativePath(root, p).Replace('\\', '/');
            string[] parts = rel.Split('/');
            string dataset = parts[0];
            string folder = string.Join('/', parts.Skip(1).Take(parts.Length - 2));
            int mA = int.Parse(Regex.Match(Path.GetFileName(p), @"_(\d+)mA_").Groups[1].Value, CultureInfo.InvariantCulture);
            double[] kpa = XdtReader.Read(p, [ControlPressureChannel]).Values[ControlPressureChannel];
            var sw = Stopwatch.StartNew();
            var m = NoiseJudge.Evaluate(kpa, settings);
            sw.Stop();
            totalMs += sw.Elapsed.TotalMilliseconds; maxMs = Math.Max(maxMs, sw.Elapsed.TotalMilliseconds);
            counts[m.Grade] = counts.GetValueOrDefault(m.Grade) + 1;
            string grade = m.Grade switch { NoiseGrade.Red => "red", NoiseGrade.Yellow => "yellow", _ => "none" };
            sb.AppendLine(string.Join(',', dataset, folder, mA.ToString(CultureInfo.InvariantCulture),
                R(m.PeakToPeakKpa), m.PlateauRunSamples.ToString(CultureInfo.InvariantCulture), R(m.LowHighPowerRatio), R(m.StepKpa), R(m.StepT), grade));
        }
        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true));
        Console.WriteLine($"{files.Count} 本を判定: " + string.Join(", ", counts.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
        Console.WriteLine($"判定時間: 平均 {totalMs / Math.Max(1, files.Count):F2} ms / 本、最大 {maxMs:F2} ms(xdt 読込は含まず)");
        Console.WriteLine("結果 -> " + outPath);
        return 0;
    }

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string FindRepoRoot()
    {
        // bin/Debug/net9.0-windows から Tools/NoiseJudgeCheck を経てリポジトリ直下へ。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AS110NoiseTest.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("リポジトリ直下(AS110NoiseTest.csproj)が見つかりません。");
    }

    private static string FindDefaultRoot() => Path.Combine(FindRepoRoot(), "学習データ");
}
