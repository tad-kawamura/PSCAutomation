using System.Diagnostics;
using System.Runtime.InteropServices;

// 検査が起動した Excel を、検査の終わりに残さないための後始末(2026-11-20。Tests/ExcelHeader と Tests/SaveAvailability で共有)。
// それまでは、検査が成功しても失敗しても、窓の無い EXCEL がタスクマネージャーに残ることがあった。
// 原因: 検査はセルや図形を Excel の COM で直接確かめる。その参照(sheet・Range など)を持ったまま Excel に終了(Quit)を頼むと、
// Excel は参照が返るまで窓を隠して待つ。.NET は終了のときに参照を返さないので、検査が終わっても Excel が待ち続けた。
// ここでは、検査のプロセスが終わるとき(成功・例外・Environment.Exit のどれでも)に次のことをする。
// 1. 検査が持っていた COM の参照を返す(GC)。ふつうはこれで Excel が自分で終わる
// 2. それでも残った Excel のうち、検査が起動したと分かっているもの(Track で覚えたプロセス ID と起動時刻が合う EXCEL)で、
//    画面に出ていないものだけ終了する。画面に出ているものと、人が開いた Excel には触らない(アプリの SessionFolders と同じ考え方)
internal static class ExcelLeftovers
{
    private static readonly Dictionary<int, long> Tracked = new();
    private static readonly object Gate = new();
    private static bool _installed, _finished;

    // Main の最初に呼ぶ。プロセスが終わるときに Finish を呼ぶようにする。
    public static void Install()
    {
        lock (Gate)
        {
            if (_installed) return;
            _installed = true;
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Finish();
        // ほかのスレッドの例外で落ちるときは ProcessExit が呼ばれないので、ここでも行う(Main の例外は Main で受けて、finally を走らせてから終わる)。
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Finish();
    }

    // 検査が起動した Excel を覚える。渡すのは、アプリの ExcelWorkSession(ExcelProcessId を持つ)か、Excel.Application の COM オブジェクト。
    // 覚えられなくても検査は止めない(null・Excel 未起動・切断済みなど)。
    public static void Track(object? sessionOrApplication)
    {
        // Excel が応答待ちでプロセス ID を聞けないことがあるので、少し待って 3 回まで試す。
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (TryTrack(sessionOrApplication)) return;
            Thread.Sleep(100);
        }
    }

    private static bool TryTrack(object? sessionOrApplication)
    {
        try
        {
            if (sessionOrApplication is null) return true;
            int? id;
            if (Marshal.IsComObject(sessionOrApplication))
            {
                GetWindowThreadProcessId(new IntPtr((int)((dynamic)sessionOrApplication).Hwnd), out uint found);
                id = (int)found;
            }
            else
            {
                id = sessionOrApplication.GetType().GetProperty("ExcelProcessId")?.GetValue(sessionOrApplication) as int?;
            }
            if (id is not > 0) return false;
            using var process = Process.GetProcessById(id.Value);
            if (!string.Equals(process.ProcessName, "EXCEL", StringComparison.OrdinalIgnoreCase)) return true;
            lock (Gate) Tracked[id.Value] = process.StartTime.Ticks;
            return true;
        }
        catch { return false; }
    }

    // Excel に終了(ブックを閉じる・Quit)を頼む直前に呼ぶ。検査がセルや図形を直接見たときの COM の参照(sheet・Range など)を返す。
    // 参照を持ったまま終了を頼むと、Excel は終了の途中で固まるか異常終了した(イベントログに EXCEL.EXE のエラーが残った)。
    // 参照を持っていたメソッドの中から呼んでも返らない(そのメソッドの変数が参照をつかんだまま)。
    // 検査の本体を別のメソッド(…Core)に分け、それが終わった(戻った・例外で抜けた)あとの finally から呼ぶ。
    public static void ReleaseComReferences()
    {
        for (int i = 0; i < 2; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
    }

    // 覚えた Excel が終わるのを待ち、残った「画面に出ていないもの」だけ終了する。何度呼んでも 1 回だけ行う。
    public static void Finish()
    {
        lock (Gate)
        {
            if (_finished) return;
            _finished = true;
        }
        try
        {
            ReleaseComReferences();
            KeyValuePair<int, long>[] tracked;
            lock (Gate) tracked = Tracked.ToArray();
            if (tracked.Length == 0) return;
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(5) && tracked.Any(item => IsAlive(item.Key, item.Value))) Thread.Sleep(100);
            foreach (var (id, startTicks) in tracked)
            {
                try
                {
                    using var process = Process.GetProcessById(id);
                    if (!IsSame(process, startTicks)) continue;
                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        Console.WriteLine($"CLEANUP 検査が起動した Excel(プロセス {id})は画面に出ているので、終了していません。要らなければ閉じてください。");
                        continue;
                    }
                    process.Kill();
                    process.WaitForExit(5000);
                    Console.WriteLine($"CLEANUP 検査が起動した、画面に出ていない Excel(プロセス {id})が残っていたので終了しました。");
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }
        catch { }
    }

    private static bool IsAlive(int id, long startTicks)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return IsSame(process, startTicks);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    // プロセス ID は使い回されるので、名前が EXCEL で、起動時刻が覚えた時刻と合う(2 秒以内)ものだけを同じとみなす。
    private static bool IsSame(Process process, long startTicks) =>
        !process.HasExited && string.Equals(process.ProcessName, "EXCEL", StringComparison.OrdinalIgnoreCase)
        && Math.Abs(process.StartTime.Ticks - startTicks) <= TimeSpan.FromSeconds(2).Ticks;

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
