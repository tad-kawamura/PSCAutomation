using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Excelの代わりに別プロセスの入力先を作り、最前面と実際のキー入力を検証する。
internal sealed class ForegroundProbe : IDisposable
{
    private readonly Process _process;
    private readonly IntPtr _handle;

    public static void RunTarget()
    {
        using var form = new Form { Text = "AS110 focus test target", Width = 300, Height = 160 };
        form.Controls.Add(new TextBox { Multiline = true, Dock = DockStyle.Fill });
        form.Shown += (_, _) => Console.WriteLine(form.Handle.ToInt64());
        Application.Run(form);
    }

    public ForegroundProbe()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(ForegroundProbe).Assembly.Location);
        start.ArgumentList.Add("--foreground-target");
        _process = Process.Start(start)!;
        try
        {
            string? handle = _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            _handle = new IntPtr(long.Parse(handle!));
        }
        catch { Dispose(); throw; }
    }

    public void Activate() => Activate(_handle);

    public static void Activate(IntPtr handle)
    {
        // 確かめの前提(別プロセスの窓が前面にある)を作る所。Windows は前面の切り替えを断ることがあり(起動した直後の Excel など)、
        // 1 回で諦めると 10 回に 1 回ほど検査が止まった(2026-11-22)。少し待ってやり直し、だめなら今の前面の窓の入力につないでから頼む。
        // それでもだめなら、そのとき前面にいた窓を付けて失敗にする(原因を追えるように)。
        const int maxAttempts = 10;
        for (int attempt = 1; ; attempt++)
        {
            if (attempt <= 2) SetForegroundWindow(handle);
            else SetForegroundAttached(handle);
            Application.DoEvents();
            if (GetForegroundWindow() == handle)
            {
                if (attempt > 1) Console.WriteLine($"RETRY external window came to the foreground on attempt {attempt}");
                return;
            }
            if (attempt >= maxAttempts)
                throw new Exception($"Cannot establish external foreground test condition ({maxAttempts} attempts; foreground was {Describe(GetForegroundWindow())}, wanted {Describe(handle)})");
            Thread.Sleep(200);
            Application.DoEvents();
        }
    }

    // 前面の窓のスレッドと入力をつなぐと、前面の切り替えを断られにくい。
    private static void SetForegroundAttached(IntPtr handle)
    {
        uint current = GetCurrentThreadId();
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        bool attached = foregroundThread != 0 && foregroundThread != current && AttachThreadInput(current, foregroundThread, true);
        try
        {
            if (IsIconic(handle)) ShowWindow(handle, 9); // SW_RESTORE
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
        }
        finally { if (attached) AttachThreadInput(current, foregroundThread, false); }
    }

    private static string Describe(IntPtr window)
    {
        if (window == IntPtr.Zero) return "(none)";
        var title = new System.Text.StringBuilder(200);
        GetWindowText(window, title, title.Capacity);
        GetWindowThreadProcessId(window, out uint processId);
        string process;
        try { using var p = Process.GetProcessById((int)processId); process = p.ProcessName; }
        catch (Exception) { process = "?"; }
        return $"[{process}] \"{title}\" (0x{window.ToInt64():X})";
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool attachFlag);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int maxCount);

    public static void AssertInput(Form notice)
    {
        if (GetForegroundWindow() != notice.Handle || !notice.ContainsFocus)
            throw new Exception("Popup is visible but keyboard input still belongs to another window");
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.CloseMainWindow();
            if (!_process.WaitForExit(3000)) _process.Kill();
        }
        _process.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
