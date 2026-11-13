using System.Globalization;
using System.Text.RegularExpressions;
namespace PSCAutomation.Apps.AS110AllCheck;

// 保存先フォルダーの決まりごと。
// - フォルダー名は「SN開始-最後」(例: SN71360-71460)を含む。開始S/N・最後S/N はここから決め、測定開始S/N(今の S/N)がその範囲内なら一致
// - 中身は Excel/PDF/LOGGER の「型式_SN番号_日付.拡張子」(2026-11-12 より前の版は「型式_SN番号_日付_6500.拡張子」。どちらも読む)。
//   ここから S/N を拾い、完了記録に無いものを見つける
internal static class SaveFolder
{
    private static readonly Regex RangePattern = new(@"SN(\d+)-(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FilePattern = new(@"^(?<model>.+?)_SN(?<serial>\d+)_\d{8}(?:_\d+)?$", RegexOptions.CultureInvariant);
    private static readonly string[] DataFolders = ["Excel", "PDF", "LOGGER"];

    public static string FolderName(string root) => Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));

    // フォルダー名から S/N の範囲を読む。無い、または開始が最後より大きいときは null。
    public static (string Start, string End)? ParseRange(string root)
    {
        if (root.Length == 0) return null;
        var match = RangePattern.Match(FolderName(root));
        if (!match.Success) return null;
        string start = match.Groups[1].Value, end = match.Groups[2].Value;
        return Number(start) <= Number(end) ? (start, end) : null;
    }

    // フォルダー名と S/N の照合。問題が無ければ ""。あれば人に見せる文(保存先フォルダーを確認してください、で終わる)。
    // 範囲の開始・最後はフォルダー名から決めるので、照合するのは測定開始S/N(今の S/N)が範囲内かだけ。
    public static string CheckRange(string root, string currentSerial)
    {
        if (root.Length == 0) return "";
        string name = FolderName(root);
        var range = ParseRange(root);
        if (range is null) return $"保存先フォルダー名「{name}」に SN開始-最後 がありません。保存先フォルダーを確認してください。";
        var (start, end) = range.Value;
        string problem;
        if (currentSerial.Length == 0) problem = "測定開始S/N が空です";
        else if (!long.TryParse(currentSerial, NumberStyles.None, CultureInfo.InvariantCulture, out long current)) problem = "測定開始S/N が数字ではありません";
        else if (current < Number(start) || current > Number(end)) problem = $"測定開始S/N {currentSerial} が範囲 {start}〜{end} の外です";
        else return "";
        return $"保存先フォルダー名「{name}」と合いません（{problem}）。保存先フォルダーを確認してください。";
    }

    // Excel/PDF/LOGGER のファイル名から S/N を拾う。同じ S/N は最初に見つけた 1 件にまとめる。形式が読めないファイルは件数だけ数える。
    public static (List<(string Serial, string Model, string BaseName)> Files, int Unreadable) Scan(string root)
    {
        var files = new List<(string Serial, string Model, string BaseName)>();
        var seen = new HashSet<string>();
        int unreadable = 0;
        foreach (string folder in DataFolders)
        {
            string path = Path.Combine(root, folder);
            if (!Directory.Exists(path)) continue;
            foreach (string file in Directory.EnumerateFiles(path).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string baseName = Path.GetFileNameWithoutExtension(file);
                var match = FilePattern.Match(baseName);
                if (!match.Success) { unreadable++; continue; }
                string serial = Number(match.Groups["serial"].Value).ToString(CultureInfo.InvariantCulture);
                if (seen.Add(serial)) files.Add((serial, match.Groups["model"].Value, baseName));
            }
        }
        return (files.OrderBy(f => Number(f.Serial)).ToList(), unreadable);
    }

    // 完了記録(COMPLETE / RELEASE いずれも)に無い S/N のデータ。
    public static (List<(string Serial, string Model, string BaseName)> Unrecorded, int Unreadable) FindUnrecorded(string root, IReadOnlyDictionary<string, string> records)
    {
        var (files, unreadable) = Scan(root);
        return (files.Where(f => !records.ContainsKey(f.Serial)).ToList(), unreadable);
    }

    private static long Number(string digits) => long.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
}
