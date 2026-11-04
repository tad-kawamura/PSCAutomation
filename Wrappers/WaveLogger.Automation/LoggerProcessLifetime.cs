using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PSCAutomation.Wrappers.WaveLogger.Automation;

// プロセス名による終了は禁止。COMの表示切替と対応する新規プロセスだけを保持する。
internal sealed class LoggerProcessLifetime : IDisposable
{
    private readonly Process _process;
    private readonly string _path;
    private readonly DateTime _started;
    private delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);

    private LoggerProcessLifetime(Process process, string path)
    {
        _process = process;
        _path = path;
        _started = process.StartTime;
        // ハンドルを先に開き、PID再利用後の別プロセスを対象にしない。
        _ = process.Handle;
    }

    public static HashSet<int> Snapshot(string name)
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(name))
            using (process) ids.Add(process.Id);
        return ids;
    }

    public static LoggerProcessLifetime Capture(object application, HashSet<int> existing,
        bool visible, Action<string>? log)
    {
        dynamic app = application;
        string path = Path.GetFullPath(Convert.ToString(app.GetModulePath())!);
        try
        {
            app.Visible = true;
            var shown = VisibleWindows();
            app.Visible = false;
            var hidden = VisibleWindows();
            app.Visible = true;
            var reshown = VisibleWindows();
            var ids = shown.Where(pair => !hidden.ContainsKey(pair.Key) &&
                reshown.TryGetValue(pair.Key, out int id) && id == pair.Value &&
                !existing.Contains(id)).Select(pair => pair.Value).Distinct().ToArray();
            if (ids.Length != 1)
                throw new InvalidOperationException($"LOGGERの制御対象PIDを特定できません（表示切替候補{ids.Length}件）。他のLOGGERは終了しません。");
            var process = Process.GetProcessById(ids[0]);
            try
            {
                if (!string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("LOGGERのCOM実行パスと対象プロセスが一致しません。");
                var lifetime = new LoggerProcessLifetime(process, path);
                log?.Invoke($"LOGGER制御対象：PID={process.Id} / 起動={lifetime._started:O} / パス={path} / 新規起動・COM表示切替一致");
                return lifetime;
            }
            catch { process.Dispose(); throw; }
        }
        finally { app.Visible = visible; }
    }

    public bool HasExited => _process.HasExited;

    public async Task CompleteAsync(bool allowTerminate, Action<string> log)
    {
        int id = _process.Id;
        var timer = Stopwatch.StartNew();
        log($"LOGGER終了：対象PID={id} の自然終了を待ちます。");
        while (!HasExited && timer.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(100);
        if (!HasExited)
        {
            // allowTerminateはStop、Quit、ファイル0、Visible=False、ID解除が全て成功した場合だけ。
            if (!allowTerminate || _process.StartTime != _started ||
                !string.Equals(_process.MainModule?.FileName, _path, StringComparison.OrdinalIgnoreCase) ||
                VisibleWindows().Values.Contains(id))
                throw new InvalidOperationException($"LOGGER PID={id} が残っています。安全な終了条件を満たさないため保持します。「確認」で再試行してください。");
            log($"LOGGER終了：正常終了手順後も対象PID={id}が10秒残存。記録済みハンドルの対象だけを終了します。");
            try { _process.Kill(entireProcessTree: false); }
            catch (InvalidOperationException) when (HasExited) { }
            timer.Restart();
            while (!HasExited && timer.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(100);
            if (!HasExited) throw new TimeoutException($"LOGGER PID={id} の終了を確認できません。");
        }
        log($"LOGGER終了：対象PID={id} のプロセス消滅を確認。");
    }

    public void Dispose() => _process.Dispose();

    private static Dictionary<IntPtr, int> VisibleWindows()
    {
        var windows = new Dictionary<IntPtr, int>();
        if (!EnumWindows((window, _) =>
        {
            if (IsWindowVisible(window))
            {
                GetWindowThreadProcessId(window, out uint id);
                windows.Add(window, checked((int)id));
            }
            return true;
        }, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return windows;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
