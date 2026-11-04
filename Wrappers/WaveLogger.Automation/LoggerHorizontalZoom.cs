using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PSCAutomation.Wrappers.WaveLogger.Automation;

// LOGGERの横方向の拡大（旧LOGGERの実メニュー名は「時間軸拡大」）を実行する。
// 画面の固定座標や推測したコマンド番号は使わない。
// 候補が一意で有効な場合だけ、指定回数の操作を送る。
// 見つからない・応答しない場合は手動操作を案内する。
// 実際の表示範囲はLOGGER側の拡大動作に従う。
internal static class LoggerHorizontalZoom
{
    private const uint WmCommand = 0x0111;
    private const uint ByPosition = 0x0400;

    public static async Task ZoomInAsync(string moduleName, int steps,
        Action<string> log, Action stepCompleted)
    {
        if (steps <= 0) return;
        var windows = new List<IntPtr>();
        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(moduleName)))
        {
            using (process)
            {
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero) windows.Add(process.MainWindowHandle);
            }
        }
        if (windows.Count != 1)
            throw new InvalidOperationException("横軸拡大の対象LOGGERを一意に確認できません。対象のLOGGERだけを開いてください。");
        IntPtr window = windows[0];
        // 参照波形読み込み後の画面更新を待つ。UIスレッドは止めない。
        await Task.Delay(300);
        for (int step = 0; step < steps; step++)
        {
            IntPtr menu = GetMenu(window);
            if (menu == IntPtr.Zero) throw ManualZoomRequired("LOGGERのメニューを取得できません。");
            Send(window, 0x0116, menu, IntPtr.Zero);
            var candidates = new Dictionary<uint, string>();
            var labels = new List<string>();
            FindCommands(window, menu, "", candidates, labels);
            if (candidates.Count != 1)
            {
                log("横軸拡大：メニュー候補=" + string.Join(" / ", labels.Take(100)));
                throw ManualZoomRequired($"横軸拡大のメニュー候補が{candidates.Count}件です。");
            }
            var command = candidates.Single();
            Send(window, WmCommand, new IntPtr(command.Key), IntPtr.Zero);
            stepCompleted();
            log($"横軸拡大：{command.Value} / command={command.Key} / 実行{step + 1}/{steps}");
            await Task.Delay(200);
        }
    }

    private static InvalidOperationException ManualZoomRequired(string detail) =>
        new(detail + Environment.NewLine +
            "参照波形は読み込み操作済みです。LOGGERで横軸拡大を手動で行ってください。" +
            Environment.NewLine + "ログの横軸拡大メニュー候補があれば、対応の確認に使えます。");

    private static string Normalize(string label)
    {
        label = label.Split('\t')[0].Replace("&", "").Replace("Ｘ", "X").Replace("ｘ", "X");
        label = Regex.Replace(label, @"[（(][A-Za-z][)）]", "");
        return Regex.Replace(label, @"\s+", "").TrimEnd('.', '。', '…');
    }

    private static bool IsHorizontalZoom(string label, string parent)
    {
        label = Normalize(label);
        parent = Normalize(parent);
        return label == "時間軸拡大" && parent == "表示"
            || label is "横軸拡大" or "横軸の拡大" or "X軸拡大" or "X軸の拡大" or "x軸拡大" or "x軸の拡大"
            || label == "拡大" && (parent.EndsWith("横軸", StringComparison.Ordinal)
                || parent.EndsWith("X軸", StringComparison.Ordinal));
    }

    private static void FindCommands(IntPtr owner, IntPtr menu, string parent,
        Dictionary<uint, string> candidates, List<string> labels)
    {
        int count = GetMenuItemCount(menu);
        if (count < 0) throw ManualZoomRequired("LOGGERのメニュー一覧を取得できません。");
        for (int i = 0; i < count; i++)
        {
            var text = new StringBuilder(256);
            GetMenuString(menu, (uint)i, text, text.Capacity, ByPosition);
            string label = text.ToString();
            string path = string.IsNullOrEmpty(parent) ? label : parent + " > " + label;
            IntPtr child = GetSubMenu(menu, i);
            if (child != IntPtr.Zero)
            {
                Send(owner, 0x0117, child, new IntPtr(i));
                FindCommands(owner, child, path, candidates, labels);
                continue;
            }
            if (!string.IsNullOrWhiteSpace(label)) labels.Add(path);
            if (!IsHorizontalZoom(label, parent)) continue;
            uint state = GetMenuState(menu, (uint)i, ByPosition);
            uint command = GetMenuItemID(menu, i);
            if (state == uint.MaxValue || (state & 3) != 0 || command == uint.MaxValue || command > ushort.MaxValue)
                throw ManualZoomRequired("LOGGERの横軸拡大メニューが無効です。");
            candidates[command] = path;
        }
    }

    private static void Send(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (SendMessageTimeout(window, message, wParam, lParam, 0x0002, 2000, out _) == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "LOGGERの横軸拡大操作に応答がありません。LOGGER画面を確認してください。");
    }

    [DllImport("user32.dll")] private static extern IntPtr GetMenu(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] private static extern IntPtr GetSubMenu(IntPtr menu, int position);
    [DllImport("user32.dll")] private static extern uint GetMenuItemID(IntPtr menu, int position);
    [DllImport("user32.dll")] private static extern uint GetMenuState(IntPtr menu, uint item, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMenuString(IntPtr menu, uint item, StringBuilder text, int count, uint flags);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam,
        IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
