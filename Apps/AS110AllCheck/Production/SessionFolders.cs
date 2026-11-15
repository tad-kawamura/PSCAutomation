using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PSCAutomation.Apps.AS110AllCheck;

// アプリが起動のたびに作る一時フォルダー(測定の一時保存と作業用 Excel・LOGGER 雛形の写し)の後片付け
// (2026-11-14、全体見直し・区切り 5、人の指示)。ふつうは終了のときに消すが、異常終了(落ちた・強制終了・電源断)では残り、
// 画面に出ていない Excel も残った。次の起動のときに、前の起動の残りを片づける。
// - 使っているフォルダーには「使用中.lock」を開いたまま置く。開けない lock のあるフォルダーは、いま動いているアプリのものなので触らない
// - lock の無いフォルダー(この仕組みより前の版が残したもの)は、3 日より古いものだけ消す
// - 残っている Excel は、そのフォルダーの「excel.txt」に書いてあるもの(プロセス ID と起動時刻が合う EXCEL)で、画面に出ていないものだけ終了する。
//   画面に出ている Excel は人が見ているかもしれないので終了しない(その Excel が開いているフォルダーは消せないので、次の起動でまた試す)
internal static class SessionFolders
{
    public const string LockFileName = "使用中.lock";
    public const string ExcelFileName = "excel.txt";
    private static readonly Regex OwnName = new(@"^(?:ver3-)?[0-9a-f]{32}$", RegexOptions.CultureInvariant);
    public static readonly TimeSpan UnlockedAge = TimeSpan.FromDays(3);

    // フォルダーを作り、「使用中.lock」を開いたままにする(閉じると消える)。持てなかったら null(後片付けの対象から外れないだけで、動きは変わらない)。
    public static IDisposable? Hold(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            return new FileStream(Path.Combine(folder, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // このアプリが起動した Excel を覚える(異常終了のあとの起動で、残っていたら終了するため)。
    public static void WriteExcelProcess(string folder, int processId)
    {
        using var process = Process.GetProcessById(processId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ExcelFileName), $"{processId}\t{process.StartTime.Ticks}");
    }

    // parent の中の、前の起動が残したフォルダーを片づける。own はいまの起動のフォルダー(触らない)。したことを文で返す(ログ用)。
    // stopExcel: 残っていた Excel を終了する処理(検査で差し替える)。省くと StopHiddenExcel。
    public static List<string> Cleanup(string parent, IEnumerable<string> own, DateTime now, Func<string, string?>? stopExcel = null)
    {
        var notes = new List<string>();
        if (!Directory.Exists(parent)) return notes;
        var mine = new HashSet<string>(own.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        foreach (string folder in Directory.GetDirectories(parent))
        {
            if (mine.Contains(Path.GetFullPath(folder)) || !OwnName.IsMatch(Path.GetFileName(folder))) continue;
            string lockPath = Path.Combine(folder, LockFileName);
            bool hadLock = false;
            try
            {
                if (hadLock = File.Exists(lockPath))
                {
                    // 開けなければ、いま動いているアプリのフォルダー。開けたら持ち主はもういない。
                    try { using var probe = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                }
                else if (now - Directory.GetLastWriteTime(folder) < UnlockedAge) continue;
                if ((stopExcel ?? StopHiddenExcel)(folder) is { } stopped) notes.Add(stopped);
                Directory.Delete(folder, true);
                notes.Add("前回の起動が残した一時フォルダーを消しました：" + folder);
            }
            // 消せない(画面に出ている Excel が作業用ブックを開いている など)ときは、そのままにして次の起動でまた試す。
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 途中まで消えて lock のファイルも無くなると、「lock の無い新しいフォルダー」になって 3 日残る。印を置き直しておく。
                try { if (hadLock && Directory.Exists(folder) && !File.Exists(lockPath)) File.WriteAllText(lockPath, ""); }
                catch (Exception lockEx) when (lockEx is IOException or UnauthorizedAccessException) { }
                notes.Add($"前回の起動が残した一時フォルダーを消せませんでした（次の起動でまた試します）：{folder} / {ex.Message}");
            }
        }
        return notes;
    }

    // フォルダーの excel.txt に書いてある Excel が残っていて、画面に出ていなければ終了する。したら文を、何もしなければ null を返す。
    public static string? StopHiddenExcel(string folder)
    {
        if (ReadExcelProcess(folder) is not { } recorded) return null;
        Process process;
        try { process = Process.GetProcessById(recorded.ProcessId); }
        catch (ArgumentException) { return null; }
        using (process)
        {
            try
            {
                if (!IsRecordedExcel(process.ProcessName, process.StartTime.Ticks, recorded.StartTicks)) return null;
                if (process.MainWindowHandle != IntPtr.Zero)
                    return $"前回の起動が残した Excel（プロセス {recorded.ProcessId}）は画面に出ているので、終了していません。要らなければ閉じてください。";
                process.Kill();
                process.WaitForExit(5000);
                return $"前回の起動が残した、画面に出ていない Excel（プロセス {recorded.ProcessId}）を終了しました。";
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return null; }
        }
    }

    public static (int ProcessId, long StartTicks)? ReadExcelProcess(string folder)
    {
        try
        {
            string[] parts = File.ReadAllText(Path.Combine(folder, ExcelFileName)).Trim().Split('\t');
            return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int id)
                && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) ? (id, ticks) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // 覚えた Excel と同じものか。プロセス ID は使い回されるので、名前が EXCEL で、起動時刻が覚えた時刻と合う(2 秒以内)ものだけ。
    public static bool IsRecordedExcel(string processName, long startTicks, long recordedStartTicks) =>
        string.Equals(processName, "EXCEL", StringComparison.OrdinalIgnoreCase) && Math.Abs(startTicks - recordedStartTicks) <= TimeSpan.FromSeconds(2).Ticks;
}
