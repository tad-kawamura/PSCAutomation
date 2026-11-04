using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace PSCAutomation.Wrappers.WaveLogger.Automation;

// WAVE LOGGER専用クラス、WAVE LOGGER Xでは使わない機能
// WAVE LOGGERの参照波形読み込み操作を自動で行う。
// 「参照波形データ読み出し」メニューを探して実行する。
// ファイル選択画面へ.xybのパスを入力し、「開く」を実行する。
// メニューや画面を操作できない場合は、エラーを知らせる。
// 波形が追加されたかは、最後にLOGGER画面で確認する。

internal static class LoggerReferenceLoader
{
    private delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);

    // WAVE LOGGERのメニュー名からコマンド番号を取得する。固定座標や固定番号は使わない。
    public static async Task OpenAsync(string moduleName, string path, Action<string> log)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("参照波形がありません。", path);
        var candidates = new List<(int Id, IntPtr Window)>();
        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(moduleName)))
        {
            using (process)
            {
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero)
                    candidates.Add((process.Id, process.MainWindowHandle));
            }
        }
        if (candidates.Count != 1)
            throw new InvalidOperationException($"LOGGERの画面候補が{candidates.Count}件です。対象のLOGGERだけを開いてください。");

        var target = candidates[0];
        if (FindDialog(target.Id, target.Window) != IntPtr.Zero)
            throw new InvalidOperationException("LOGGERで開いているダイアログを閉じてから「確認」を押してください。");
        IntPtr menu = GetMenu(target.Window);
        if (menu != IntPtr.Zero)
            InitializeMenu(target.Window, 0x0116, menu, IntPtr.Zero);
        uint command = FindReferenceCommand(target.Window, menu, log);
        if (command == uint.MaxValue)
            throw new InvalidOperationException("「参照波形データ読み出し」メニューが見つかりません。LOGGER画面を確認してください。");
        log($"参照波形：メニュー「参照波形データ読み出し」を実行 / command={command}");
        if (!PostMessage(target.Window, 0x0111, new IntPtr(command), IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "参照波形メニューの呼び出しに失敗しました。");

        var timer = Stopwatch.StartNew();
        IntPtr dialog = IntPtr.Zero;
        IntPtr fileName = IntPtr.Zero;
        IntPtr openButton = IntPtr.Zero;
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            dialog = FindDialog(target.Id, target.Window);
            if (dialog != IntPtr.Zero)
            {
                // Windowsの標準「開く」ダイアログ：cmb13またはedt1。
                fileName = GetDlgItem(dialog, 0x047C);
                if (fileName != IntPtr.Zero)
                {
                    IntPtr combo = FindWindowEx(fileName, IntPtr.Zero, "ComboBox", null);
                    IntPtr edit = FindWindowEx(combo != IntPtr.Zero ? combo : fileName, IntPtr.Zero, "Edit", null);
                    if (edit != IntPtr.Zero) fileName = edit;
                }
                else fileName = GetDlgItem(dialog, 0x0480);
                openButton = GetDlgItem(dialog, 1);
                if (fileName != IntPtr.Zero && openButton != IntPtr.Zero && IsWindowEnabled(openButton)) break;
            }
            await Task.Delay(100);
        }
        if (fileName == IntPtr.Zero || openButton == IntPtr.Zero || !IsWindowEnabled(openButton))
            throw new InvalidOperationException("参照波形のファイル選択欄を取得できませんでした。表示されたダイアログを確認してください。");
        string fullPath = Path.GetFullPath(path);
        if (SendMessageTimeout(fileName, 0x000C, IntPtr.Zero, fullPath, 0x0002, 2000, out IntPtr result) == IntPtr.Zero || result == IntPtr.Zero)
            throw new InvalidOperationException("参照波形のファイル名を設定できませんでした。");
        var value = new StringBuilder(fullPath.Length + 16);
        if (SendMessageTimeoutRead(fileName, 0x000D, new IntPtr(value.Capacity), value, 0x0002, 2000, out _) == IntPtr.Zero)
            throw new InvalidOperationException("参照波形のファイル名を読み取れませんでした。");
        if (!string.Equals(value.ToString(), fullPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("参照波形のファイル名を確認できませんでした。LOGGER画面を確認してください。");
        log("参照波形ファイル指定：" + fullPath);
        if (!PostMessage(openButton, 0x00F5, IntPtr.Zero, IntPtr.Zero)) // BM_CLICK
            throw new Win32Exception(Marshal.GetLastWin32Error(), "参照波形の「開く」操作に失敗しました。");
        timer.Restart();
        while (IsWindow(dialog) && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(100);
        if (IsWindow(dialog))
            throw new InvalidOperationException("参照波形のファイル選択画面が閉じませんでした。LOGGERのメッセージを確認してください。");
        log("参照波形：ファイル選択画面が閉じました。波形の表示はLOGGER画面で確認してください。");
    }

    private static uint FindReferenceCommand(IntPtr owner, IntPtr menu, Action<string> log)
    {
        if (menu == IntPtr.Zero) return uint.MaxValue;
        for (int i = 0; i < GetMenuItemCount(menu); i++)
        {
            var name = new StringBuilder(256);
            GetMenuString(menu, (uint)i, name, name.Capacity, 0x0400);
            if (name.ToString().Contains("参照波形データ読み出し"))
            {
                uint state = GetMenuState(menu, (uint)i, 0x0400);
                log($"参照波形：メニュー更新後の状態=0x{state:X}");
                if (state == uint.MaxValue || (state & 3) != 0)
                    throw new InvalidOperationException("メニュー更新後も「参照波形データ読み出し」が無効です。LOGGERで「ファイル」を開き、項目が選べるか確認してください。");
                return GetMenuItemID(menu, i);
            }
            IntPtr subMenu = GetSubMenu(menu, i);
            if (subMenu == IntPtr.Zero) continue;
            // メニュー表示直前にLOGGERが有効・無効状態を更新する機会を与える。
            InitializeMenu(owner, 0x0117, subMenu, new IntPtr(i));
            uint child = FindReferenceCommand(owner, subMenu, log);
            if (child != uint.MaxValue) return child;
        }
        return uint.MaxValue;
    }

    private static void InitializeMenu(IntPtr owner, uint message, IntPtr menu, IntPtr position)
    {
        if (SendMessageTimeoutNative(owner, message, menu, position, 0x0002, 2000, out _) == IntPtr.Zero)
            throw new InvalidOperationException("LOGGERのメニュー状態更新に応答がありませんでした。");
    }

    private static IntPtr FindDialog(int processId, IntPtr owner)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint id);
            if (id != processId || !IsWindowVisible(window) || GetAncestor(window, 3) != owner) return true;
            var name = new StringBuilder(64);
            GetClassName(window, name, name.Capacity);
            if (name.ToString() != "#32770") return true;
            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetMenu(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] private static extern IntPtr GetSubMenu(IntPtr menu, int position);
    [DllImport("user32.dll")] private static extern uint GetMenuItemID(IntPtr menu, int position);
    [DllImport("user32.dll")] private static extern uint GetMenuState(IntPtr menu, uint item, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMenuString(IntPtr menu, uint item, StringBuilder text, int count, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr window, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SendMessageTimeoutRead(IntPtr window, uint message, IntPtr wParam, StringBuilder lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)] private static extern IntPtr SendMessageTimeoutNative(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}
