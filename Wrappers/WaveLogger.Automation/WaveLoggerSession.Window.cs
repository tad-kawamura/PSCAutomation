using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PSCAutomation.Wrappers.WaveLogger.Automation;

public sealed partial class WaveLoggerSession
{
    /// <summary>LOGGER画面を復元して前面へ渡す。Windowsが切り替えを拒否した場合はfalse。</summary>
    /// <remarks>
    /// SetForegroundWindow を1回呼ぶだけ。直前に人が操作していないアプリ(M5Stack など外部入力から呼んだとき、
    /// ほかのアプリが前面のとき)からは Windows に拒否されやすい。確実に前面へ出すには
    /// <see cref="GetMainWindowHandle"/> の戻り値を <c>PSCAutomation.Wrappers.Common.WindowActivation.TryActivate</c> に渡す。
    /// </remarks>
    public bool BringToFront()
    {
        IntPtr window = GetMainWindowHandle();
        if (IsIconic(window)) ShowWindowAsync(window, 9); // SW_RESTORE
        return SetForegroundWindow(window);
    }

    /// <summary>LOGGER本体ソフトの画面(メインウィンドウ)のハンドルを返す。画面を操作しない。</summary>
    /// <exception cref="InvalidOperationException">プロセス名を取得できない、または画面候補が1件でない。</exception>
    public IntPtr GetMainWindowHandle()
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
        return windows[0];
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
