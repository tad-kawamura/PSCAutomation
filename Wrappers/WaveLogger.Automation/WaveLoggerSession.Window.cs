using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PSCAutomation.Wrappers.WaveLogger.Automation;

public sealed partial class WaveLoggerSession
{
    /// <summary>LOGGER画面を復元して前面へ渡す。Windowsが切り替えを拒否した場合はfalse。</summary>
    public bool BringToFront()
    {
        string processName = Path.GetFileNameWithoutExtension(GetLoggerModuleName());
        if (string.IsNullOrWhiteSpace(processName))
            throw new InvalidOperationException("LOGGERのプロセス名を取得できません。");
        var windows = new List<IntPtr>();
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero)
                    windows.Add(process.MainWindowHandle);
            }
        }
        if (windows.Count != 1)
            throw new InvalidOperationException($"LOGGERの画面候補が{windows.Count}件です。");
        IntPtr window = windows[0];
        if (IsIconic(window)) ShowWindowAsync(window, 9); // SW_RESTORE
        return SetForegroundWindow(window);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
