using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// 漏れ試験の判定画面(SPEC「漏れ試験の自動判定」10)。最前面で出し、入力先(キーボード)を奪わない。
// 試験中は「合格」、決まったら「不合格」「判定できません」を出し、不合格・判定できないは 2 秒(欄の値)、合格は 0.5 秒で閉じる。画像も残す。
internal static class LeakJudgeWindowTests
{
    public static void Run(Assembly assembly)
    {
        var windowType = assembly.GetType("PSCAutomation.Apps.AS110AllCheck.LeakJudgeWindow")!;
        var outcomeType = assembly.GetType("PSCAutomation.Apps.AS110AllCheck.LeakOutcome")!;
        object Outcome(string name) => Enum.Parse(outcomeType, name);
        using var focusOwner = new Form { Text = "focus owner", StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Size = new Size(300, 200) };
        focusOwner.Show();
        focusOwner.Activate();
        Application.DoEvents();
        IntPtr foregroundBefore = GetForegroundWindow();

        using var window = (Form)Activator.CreateInstance(windowType, 10, 1.0, (double?)530.0, 2.0, SystemFonts.DefaultFont)!;
        window.Show();
        Application.DoEvents();
        const int GwlExStyle = -20, WsExTopmost = 0x8;
        if ((GetWindowLong(window.Handle, GwlExStyle) & WsExTopmost) == 0) throw new Exception("Leak judge window is not topmost");
        if (GetForegroundWindow() != foregroundBefore) throw new Exception("Leak judge window took the keyboard focus");
        string Texts() => string.Join("|", window.Controls.OfType<Label>().Select(l => l.Text));
        if (!Texts().Contains("合格")) throw new Exception("Leak judge window must show 合格 while running: " + Texts());

        windowType.GetMethod("ShowValues")!.Invoke(window, [539.700, 540.314, 0.614, TimeSpan.FromSeconds(3.2)]);
        Application.DoEvents();
        if (!Texts().Contains("539.700") || !Texts().Contains("540.314") || !Texts().Contains("0.614") || !Texts().Contains("残り 6.8 秒") || !Texts().Contains("下限 530.0 kPa"))
            throw new Exception("Leak judge window values: " + Texts());
        foreach (Control control in window.Controls)
            if (control.Right > window.ClientSize.Width || control.Bottom > window.ClientSize.Height) throw new Exception("Leak judge window clips " + control.Text);
        Save(window, "leak-judge-pass.png");

        windowType.GetMethod("ShowResult")!.Invoke(window, [Outcome("Fail")]);
        Application.DoEvents();
        if (!Texts().Contains("不合格")) throw new Exception("Leak judge window must show 不合格: " + Texts());
        Save(window, "leak-judge-fail.png");
        windowType.GetMethod("ShowResult")!.Invoke(window, [Outcome("Unmeasurable")]);
        Application.DoEvents();
        if (!Texts().Contains("判定できません")) throw new Exception("Leak judge window must show 判定できません: " + Texts());

        // 判定できない(不合格と同じ)は、欄の秒数(ここでは 2 秒)出してから閉じる。
        long failClose = CloseTime(windowType, window);
        if (failClose < 1950 || failClose > 3500) throw new Exception($"Leak judge window must close 2 s after 判定できません ({failClose} ms)");

        // 合格は今までどおり 0.5 秒で閉じる(下限なしの窓では下限を出さない)。
        using var passWindow = (Form)Activator.CreateInstance(windowType, 10, 1.0, null, 2.0, SystemFonts.DefaultFont)!;
        passWindow.Show();
        Application.DoEvents();
        windowType.GetMethod("ShowValues")!.Invoke(passWindow, [539.700, 540.314, 0.614, TimeSpan.FromSeconds(10)]);
        string passTexts = string.Join("|", passWindow.Controls.OfType<Label>().Select(l => l.Text));
        if (passTexts.Contains("下限")) throw new Exception("Leak judge window without a lower limit must not show it: " + passTexts);
        windowType.GetMethod("ShowResult")!.Invoke(passWindow, [Outcome("Pass")]);
        long passClose = CloseTime(windowType, passWindow);
        if (passClose < 450 || passClose > 1500) throw new Exception($"Leak judge window must close 0.5 s after 合格 ({passClose} ms)");
        Console.WriteLine($"PASS leak judge window is topmost without taking focus, shows 合格 / values / lower limit / 不合格 / 判定できません, closes {failClose} ms after 判定できません and {passClose} ms after 合格; images: leak-judge-pass.png / leak-judge-fail.png");
    }

    private static long CloseTime(Type windowType, Form window)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        windowType.GetMethod("CloseAfterResult")!.Invoke(window, null);
        while (!window.IsDisposed && watch.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(10); }
        if (!window.IsDisposed) throw new Exception("Leak judge window did not close");
        return watch.ElapsedMilliseconds;
    }

    private static void Save(Form window, string name)
    {
        using var bitmap = new Bitmap(window.Width, window.Height);
        window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size));
        bitmap.Save(Path.Combine(AppContext.BaseDirectory, name));
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr window, int index);
}
