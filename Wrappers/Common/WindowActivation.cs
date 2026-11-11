using System.Runtime.InteropServices;

namespace PSCAutomation.Wrappers.Common;

public static class WindowActivation
{
    /// <summary>
    /// 入力先(前面)を変えずに、窓を通常の窓の一番上へ出す。一度だけ最前面にして、すぐ通常の窓に戻す(固定しない)。
    /// </summary>
    /// <remarks>
    /// 人の操作を受けていないアプリ(M5Stack など外部入力から呼んだとき)は、Windows に前面の切り替えを断られる。
    /// そのとき <see cref="TryActivate(IntPtr)"/> の重なり順の変更(HWND_TOP)だけでは、前面の窓(Excel など)の上には出ない。
    /// 最前面を経由すると、切り替えを断られる状況でも画面の上には出る(キーボードの入力先は前面の窓のまま)。
    /// 入力先も移したいときは、続けて <see cref="TryActivate(IntPtr)"/> を呼ぶ。2026-11-11 追加(元: AS110AllCheck の LOGGER 前面表示)。
    /// </remarks>
    /// <returns>窓が無効、または重なり順の変更に失敗したとき false。</returns>
    public static bool BringToTop(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle)) return false;
        if (IsIconic(handle)) ShowWindow(handle, 9); // SW_RESTORE
        const uint flags = 0x0001 | 0x0002 | 0x0010; // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE
        bool raised = SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, flags); // HWND_TOPMOST
        bool restored = SetWindowPos(handle, new IntPtr(-2), 0, 0, 0, 0, flags); // HWND_NOTOPMOST(通常の窓の一番上)
        return raised && restored;
    }

    /// <summary>window が other より重なり順で上にあるとき true。どちらかが無効、または同じ窓なら false。</summary>
    public static bool IsAbove(IntPtr window, IntPtr other)
    {
        if (window == IntPtr.Zero || other == IntPtr.Zero || window == other || !IsWindow(window) || !IsWindow(other)) return false;
        for (IntPtr current = GetTopWindow(IntPtr.Zero); current != IntPtr.Zero; current = GetWindow(current, 2)) // GW_HWNDNEXT
        {
            if (current == window) return true;
            if (current == other) return false;
        }
        return false;
    }

    // 外部プロセスのExcelにも、表示順の変更と入力先の切替を両方行う。
    public static bool TryActivate(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle)) return false;
        if (IsIconic(handle)) ShowWindow(handle, 9); // SW_RESTORE
        // 入力先の切替が制限されても、通常ウィンドウの先頭には表示する。
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040);
        if (SetForegroundWindow(handle) && GetForegroundWindow() == handle) return true;
        uint currentThread = GetCurrentThreadId();
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        bool attached = false;
        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
                attached = AttachThreadInput(currentThread, foregroundThread, true);
            SetForegroundWindow(handle);
            return GetForegroundWindow() == handle;
        }
        finally
        {
            if (attached) AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    // TopMostは重なり順だけを変える。Excelへ渡したキーボード入力先も戻す。
    public static bool TryActivate(Form window, Control input)
    {
        if (window.IsDisposed || !window.Visible) return false;
        uint currentThread = GetCurrentThreadId();
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        bool attached = false;
        try
        {
            window.BringToFront();
            if (!SetForegroundWindow(window.Handle) && foregroundThread != 0 && foregroundThread != currentThread)
            {
                attached = AttachThreadInput(currentThread, foregroundThread, true);
                if (attached) SetForegroundWindow(window.Handle);
            }
            window.Activate();
            input.Focus();
            return GetForegroundWindow() == window.Handle && input.ContainsFocus;
        }
        finally
        {
            if (attached) AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr parent);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint from, uint to, [MarshalAs(UnmanagedType.Bool)] bool attach);
}
