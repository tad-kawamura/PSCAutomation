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

    // 保存データではないファイル(2026-11-14、全体見直し・区切り 5)。Excel がブックを開いている間に作る「~$…」と、Windows が作る Thumbs.db・desktop.ini。
    // それまでは「形式でないファイル n 件」や、同じ S/N の「別の名前のファイル」に数えられ、知らせが紛らわしかった。
    public static bool IsDataFile(string path)
    {
        string name = Path.GetFileName(path);
        return !name.StartsWith("~$", StringComparison.Ordinal)
            && !name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) && !name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
    }

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
            foreach (string file in Directory.EnumerateFiles(path).Where(IsDataFile).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
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

    // その S/N の保存ファイル(Excel/PDF/LOGGER。フルパス)を全部返す。日付・型式・前の版の名前(末尾 _6500)が違っても同じ S/N なら拾う。番号は先頭の 0 を無視して比べる。
    // 正式保存の前に、同じ S/N の前のファイルが残っていないかを確かめるのに使う(2026-11-13、全体見直し。以前は同じ日付の名前しか見ず、
    // 日付が変わってから測り直すと、聞かれないまま同じ S/N のファイルが 2 組残った)。
    public static List<string> FindSerialFiles(string root, string serial)
    {
        var found = new List<string>();
        if (!long.TryParse(serial, NumberStyles.None, CultureInfo.InvariantCulture, out long number)) return found;
        foreach (string folder in DataFolders)
        {
            string path = Path.Combine(root, folder);
            if (!Directory.Exists(path)) continue;
            foreach (string file in Directory.EnumerateFiles(path).Where(IsDataFile).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var match = FilePattern.Match(Path.GetFileNameWithoutExtension(file));
                if (match.Success && long.TryParse(match.Groups["serial"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long n) && n == number)
                    found.Add(file);
            }
        }
        return found;
    }

    // 正式保存の前に見つけた、同じ S/N の前の保存ファイルを 2 つに分ける。
    // Overwritten: 今回書くファイル(targets)と同じもの(上書きされる)。Kept: 別の名前のもの(今回の保存では消えずに残る)。
    public static (string[] Overwritten, string[] Kept) FindSaveConflicts(string root, string serial, IEnumerable<string> targets)
    {
        var targetSet = new HashSet<string>(targets.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var files = FindSerialFiles(root, serial).Select(Path.GetFullPath).ToArray();
        return (files.Where(targetSet.Contains).ToArray(), files.Where(f => !targetSet.Contains(f)).ToArray());
    }

    // 上書きの確認に出す文。上書きされるファイルと、消えずに残るファイルを分けて見せる。
    public static string DescribeSaveConflicts(string serial, string baseName, IReadOnlyCollection<string> overwritten, IReadOnlyCollection<string> kept)
    {
        var text = new System.Text.StringBuilder($"S/N {serial} の保存ファイルがすでにあります（合格にはなっていません）。\n");
        if (overwritten.Count > 0) text.Append("\n■ 同じ名前（「はい」で上書きします）\n").Append(string.Join("\n", overwritten)).Append('\n');
        if (kept.Count > 0)
            text.Append("\n■ 別の名前（日付・型式が違う、または前の版の名前〈末尾 _6500〉。「はい」でも消さずに残ります）\n").Append(string.Join("\n", kept))
                .Append($"\n※ 残ったままだと、S/N {serial} のデータが 2 組になります。要らない方は保存のあとで人が片づけてください。\n");
        text.Append($"\n今回の測定を {baseName} の名前で保存しますか？\n「はい」：保存する\n「いいえ」：保存しない。前のデータを残すなら「設定変更（測定を破棄）」→ 測定開始S/N を次の番号にして測り直す");
        return text.ToString();
    }

    // 完了記録(COMPLETE / RELEASE いずれも)に無い S/N のデータ。
    public static (List<(string Serial, string Model, string BaseName)> Unrecorded, int Unreadable) FindUnrecorded(string root, IReadOnlyDictionary<string, string> records)
    {
        var (files, unreadable) = Scan(root);
        return (files.Where(f => !records.ContainsKey(f.Serial)).ToList(), unreadable);
    }

    private static long Number(string digits) => long.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
}

// 正式保存の置き換えが途中で止まったときの例外。Path は止まったファイル、Restored は保存先を保存の前の状態に戻せたか。
internal sealed class SaveReplaceException(string path, bool restored, Exception inner) : IOException(inner.Message, inner)
{
    public string Path { get; } = path;
    public bool Restored { get; } = restored;
}

// 正式保存の最後の置き換え(一時フォルダーに作った Excel・PDF・LOGGER を、保存先の名前へ移す)。
// 途中で止まったら(前に保存したファイルを Excel などで開いたまま保存し直した、など)、置き換えた分を元に戻す。
// 保存先はいつも「全部前のまま」か「全部今回」のどちらかになる(2026-11-14、全体見直し・区切り 5、人が承認。
// それまでは Excel は今回・PDF は前回、という組が保存先に残り得た)。
internal static class SaveReplace
{
    // files: (一時フォルダーのファイル, 保存先のファイル)。backupFolder: 前のファイルを一時的によけておく所(保存先と同じドライブ)。
    // 止まったときは SaveReplaceException。Restored が false のときは、前のファイルが backupFolder に残っていることがある(呼ぶ側で消さない)。
    public static void ReplaceAll(IReadOnlyList<(string From, string To)> files, string backupFolder)
    {
        var done = new List<(string From, string To, string? Backup)>();
        for (int i = 0; i < files.Count; i++)
        {
            var (from, to) = files[i];
            string? backup = null;
            try
            {
                if (File.Exists(to))
                {
                    string aside = System.IO.Path.Combine(backupFolder, $"前のファイル{i + 1}{System.IO.Path.GetExtension(to)}");
                    File.Move(to, aside);
                    backup = aside;
                }
                File.Move(from, to);
                done.Add((from, to, backup));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                bool restored = true;
                // 止まったファイル: 前のファイルをよけただけで今回の分を置けなかったなら、前のファイルを戻す。
                try { if (backup is not null && !File.Exists(to)) File.Move(backup, to); }
                catch (Exception restoreEx) when (restoreEx is IOException or UnauthorizedAccessException) { restored = false; }
                // すでに置き換えたファイル: 今回の分を一時フォルダーへ戻し、前のファイルを戻す。
                foreach (var d in Enumerable.Reverse(done))
                {
                    try
                    {
                        File.Move(d.To, d.From);
                        if (d.Backup is not null) File.Move(d.Backup, d.To);
                    }
                    catch (Exception restoreEx) when (restoreEx is IOException or UnauthorizedAccessException) { restored = false; }
                }
                throw new SaveReplaceException(to, restored, ex);
            }
        }
    }
}
