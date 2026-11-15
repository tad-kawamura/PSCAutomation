namespace PSCAutomation.Apps.AS110AllCheck;

// ファイルをほかで開いている・保存先が見えないときの案内(2026-11-14、全体見直し・区切り 5)。
// .NET の文は英語のまま出る(「Access to the path is denied.」「The process cannot access the file … because it is being used by another process.」)。
// 何を閉じればよいかが分かるよう、ファイル名と確かめることを日本語で出す。元の文は末尾の「詳細」と InnerException に残す。
// 漏れ試験記録の案内(LeakTestRecordFile.UnwritableAdvice)と同じ考え方。
internal sealed class FileAccessAdviceException(string message, Exception inner) : IOException(message, inner);

internal static class FileAccessAdvice
{
    // 開けない・書けないの例外か(ファイルが無いのは別の案内があるので含めない。中身が不正〈InvalidDataException〉も含めない)。
    public static bool IsAccessProblem(Exception ex) =>
        ex is not FileAccessAdviceException and (UnauthorizedAccessException or IOException) and not FileNotFoundException;

    // what: 何のファイルか(例: 「完了記録」)。after: 閉じたあとにすること(1 行。無ければ空)。openHint: 1 つ目の確かめ(省くと「Excel などで開いていたら閉じる」)。
    public static string Text(string what, string path, Exception ex, string after = "", string? openHint = null) =>
        $"{what}を開けません（読み書きできません）。\n" +
        $"・{openHint ?? $"「{Path.GetFileName(path)}」を Excel などで開いていたら閉じてください"}\n" +
        "・保存先（USB メモリー・外付けディスクなど）をエクスプローラーで開けるか確かめてください\n" +
        (after.Length > 0 ? after + "\n" : "") +
        $"{path}\n（詳細：{ex.Message}）";

    public static void Run(string what, string path, Action action, string after = "", string? openHint = null) =>
        Run(what, path, () => { action(); return true; }, after, openHint);

    public static T Run<T>(string what, string path, Func<T> action, string after = "", string? openHint = null)
    {
        try { return action(); }
        catch (Exception ex) when (IsAccessProblem(ex)) { throw new FileAccessAdviceException(Text(what, path, ex, after, openHint), ex); }
    }
}
