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
        SetForegroundWindow(handle);
        Application.DoEvents();
        if (GetForegroundWindow() != handle) throw new Exception("Cannot establish external foreground test condition");
    }

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
