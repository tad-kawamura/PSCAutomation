using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PSCAutomation.Wrappers.WaveLogger.Automation;

// WAVE LOGGER X 専用クラス　
// WAVE LOGGER Xへの参照波形.xybの読み込みに使用する。
// LOGGER画面へ、ファイルをドラッグしたのと同じ通知を送る。
// 対象のLOGGER画面を探し、指定したファイルのドロップを通知する。
// 読み込み完了までは確認しないため、結果はLOGGER画面で確認する。

internal static class LoggerFileDrop
{
    private const uint WmDropFiles = 0x0233;
    private const uint WsExAcceptFiles = 0x10;
    private delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);

    // 通知をキューへ登録した結果だけを返す。読み込み完了の結果ではない。
    public static void Post(string moduleName, string path, Action<string> log)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("参照波形がありません。", path);
        var timer = Stopwatch.StartNew();
        string processName = Path.GetFileNameWithoutExtension(moduleName);
        var candidates = new List<(int Id, IntPtr Window)>();
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero)
                    candidates.Add((process.Id, process.MainWindowHandle));
            }
        }
        if (candidates.Count != 1)
            throw new InvalidOperationException(
                $"LOGGER（{processName}）の画面候補が{candidates.Count}件です。対象のLOGGERだけを開いて試してください。");

        var candidate = candidates[0];
        IntPtr target = candidate.Window;
        bool accepts = AcceptsFiles(target);
        if (!accepts)
        {
            // 子ウィンドウにドロップを受け付ける設定があれば、表示面積が最大のものを使用する。
            long largestArea = 0;
            EnumChildWindows(candidate.Window, (window, _) =>
            {
                if (IsWindowVisible(window) && AcceptsFiles(window) && GetClientRect(window, out Rect rect))
                {
                    long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                    if (area > largestArea) { largestArea = area; target = window; accepts = true; }
                }
                return true;
            }, IntPtr.Zero);
        }
        var title = new StringBuilder(512);
        GetWindowText(target, title, title.Capacity);
        log($"ドロップ試験：PID={candidate.Id} / HWND=0x{target.ToInt64():X} / タイトル={title}");
        log($"ドロップ試験：WS_EX_ACCEPTFILES={(accepts ? "あり" : "なし（メイン画面への通知を試験）")}");

        // DROPFILESは20バイト。後ろにUnicodeのファイル名リスト（二重NULL終端）を配置する。
        byte[] names = Encoding.Unicode.GetBytes(Path.GetFullPath(path) + "\0\0");
        IntPtr drop = GlobalAlloc(0x42, (UIntPtr)(20 + names.Length)); // MOVEABLE | ZEROINIT
        if (drop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        bool posted = false;
        try
        {
            IntPtr memory = GlobalLock(drop);
            if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                Marshal.WriteInt32(memory, 0, 20);
                if (GetClientRect(target, out Rect rect))
                {
                    Marshal.WriteInt32(memory, 4, (rect.Right - rect.Left) / 2);
                    Marshal.WriteInt32(memory, 8, (rect.Bottom - rect.Top) / 2);
                }
                Marshal.WriteInt32(memory, 12, 0); // クライアント座標
                Marshal.WriteInt32(memory, 16, 1); // Unicode
                Marshal.Copy(names, 0, IntPtr.Add(memory, 20), names.Length);
            }
            finally { GlobalUnlock(drop); }

            posted = PostMessage(target, WmDropFiles, drop, IntPtr.Zero);
            if (!posted)
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, $"WM_DROPFILES送信失敗（Win32={error}）。" +
                    (error == 5 ? "両アプリの管理者権限の違いを確認してください。" : ""));
            }
            // 成功後は受信側がDragFinishで解放する。送信側で解放しない。
        }
        finally { if (!posted) GlobalFree(drop); }
        timer.Stop();
        log($"ドロップ試験：PostMessage=True / 通知準備・送信={timer.Elapsed.TotalMilliseconds:F1} ms");
        log("ドロップ試験：通知を送信しました。参照波形の読み込み完了はLOGGER画面で確認してください。");
    }

    private static bool AcceptsFiles(IntPtr window) =>
        (unchecked((uint)GetWindowLong(window, -20)) & WsExAcceptFiles) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr handle);
}
