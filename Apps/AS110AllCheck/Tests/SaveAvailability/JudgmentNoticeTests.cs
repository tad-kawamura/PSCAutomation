using System.Reflection;
using System.Windows.Forms;

internal static class JudgmentNoticeTests
{
    public static void Run(Form owner, Type type, object flow, Type flowType, Assembly assembly)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Call(string name, params object[] args) => type.GetMethod(name, flags)!.Invoke(owner, args);
        Form? Notice() => type.GetField("_saveNotice", flags)!.GetValue(owner) as Form;
        void Mode(string mode) => flowType.GetProperty("Mode")!.SetValue(flow,
            Enum.Parse(assembly.GetType("PSCAutomation.Apps.AS110AllCheck.TestMode")!, mode));
        void Pass(bool pass) => type.GetField("_lastExcelPass", flags)!.SetValue(owner, pass);
        // 接続・機器操作なしで実際のポップアップを検証する。
        ((System.Windows.Forms.Timer)type.GetField("_statusHeartbeat", flags)!.GetValue(owner)!).Stop();
        using var foreground = new ForegroundProbe();
        foreach (string mode in new[] { "Adjustment", "Production" })
        foreach (bool pass in new[] { false, true })
        {
            Mode(mode); Pass(pass);
            foreground.Activate();
            Call("ShowJudgmentNotice");
            var notice = Notice() ?? throw new Exception("Judgment notice missing");
            if (notice.Text != (pass ? "合格" : "不合格") || notice.Modal || !notice.TopMost)
                throw new Exception("Wrong judgment notice behavior");
            // 持ち主つきだと、ポップアップを前に出すたびにWindowsがメイン画面も一緒に前へ出す(2026-11-11)。
            if (notice.Owner is not null || owner.OwnedForms.Contains(notice))
                throw new Exception("Judgment notice must not be owned by the main screen");
            ForegroundProbe.AssertInput(notice);
            Call("KeepPreparationInFront");
            Call("ShowTransferredExcel");
            ForegroundProbe.AssertInput(notice);
            SendKeys.SendWait("{ENTER}");
            Application.DoEvents();
            if (Notice() is not null) throw new Exception("Enter did not dismiss judgment notice");
            // データ調整・データ取りとも約0.4秒で自動的に閉じる。OKボタンは置かない。
            Call("ShowJudgmentNotice");
            var waiting = Notice() ?? throw new Exception("Judgment notice missing before auto close check");
            if (waiting.Controls.OfType<Button>().Any()) throw new Exception("Judgment notice must not have buttons");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 200) { Application.DoEvents(); Thread.Sleep(10); }
            if (Notice() is null) throw new Exception("Judgment notice closed too early");
            while (watch.ElapsedMilliseconds < 800) { Application.DoEvents(); Thread.Sleep(10); }
            bool autoClosed = Notice() is null && waiting.IsDisposed;
            if (!autoClosed)
                throw new Exception($"Wrong auto close: mode={mode}, closed={autoClosed}");
            Call("CloseJudgmentNotice");
            foreach (string action in new[] { "データ調整", "Excel転送" })
            {
                foreach (bool approve in mode == "Production" && pass ? new[] { false, true } : new[] { true })
                {
                    Call("ShowJudgmentNotice");
                    using var timer = new System.Windows.Forms.Timer { Interval = 50 };
                    Exception? failure = null;
                    timer.Tick += (_, _) =>
                    {
                        timer.Stop();
                        try
                        {
                            if (Notice() is not null) throw new Exception("Old notice remained during confirmation");
                            var dialog = owner.OwnedForms.Single(f => f.Modal);
                            ForegroundProbe.AssertInput(dialog);
                            if (!dialog.Controls.OfType<Label>().Any(l => l.Text.Contains("合格しているけど、またやるの？")))
                                throw new Exception("Repeat confirmation missing");
                            if ((bool)Call("ConfirmRepeatAfterPass", action)!)
                                throw new Exception("Repeated input bypassed confirmation");
                            if (((Button)dialog.AcceptButton!).Text != "いいえ")
                                throw new Exception("Enter must cancel repeat confirmation");
                            if (approve) dialog.Controls.OfType<Button>().Single(b => b.Text == "はい").PerformClick();
                            else SendKeys.SendWait("{ENTER}");
                        }
                        catch (Exception ex)
                        {
                            failure = ex;
                            foreach (var dialog in owner.OwnedForms.Where(f => f.Modal)) dialog.Close();
                        }
                    };
                    if (mode == "Production" && pass) timer.Start();
                    bool allowed = (bool)Call("ConfirmRepeatAfterPass", action)!;
                    if (failure is not null) throw failure;
                    if (allowed != approve || Notice() is not null)
                        throw new Exception("Wrong repeat/dismiss result");
                    if (!(bool)flowType.GetProperty("HasTransfer")!.GetValue(flow)!)
                        throw new Exception("Confirmation discarded transferred data");
                }
            }
            Console.WriteLine($"PASS judgment popup: {mode}, pass={pass}, Enter / both action buttons / repeat confirmation");
        }
        Mode("Production");
    }
}
