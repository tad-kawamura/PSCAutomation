using System.Reflection;
using System.Windows.Forms;

// 検査が途中で失敗して抜けるときも、実 MainForm を終了処理(Close)を通して閉じる(2026-11-20)。
// Close を通さずに Dispose だけで終わると、new MainForm() が始めた LOGGER 本体ソフトの事前起動が破棄されず、
// 窓なしの WAVE LOGGER(NRWL.exe)が 1 つ残る。成功の経路は検査の最後で Close しているが、途中で例外が出るとそこまで届かなかった。
// 使い方: 画面を作った直後に「using var closer = new MainFormCloser(form);」と書く(画面の using より後に書くと、画面の Dispose より先に動く)。
// すでに閉じていれば何もしない。閉じられなくても検査の失敗(元の例外)は隠さず、知らせるだけにする。
internal sealed class MainFormCloser(Form form) : IDisposable
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    // 画面のスレッドが止まっていて Close を通せないとき(見張りタイマーが別のスレッドから Environment.Exit で終わらせるとき)用(2026-11-20)。
    // new MainForm() が始めた LOGGER 本体ソフトの事前起動だけを破棄する。部品(WaveLoggerSession)の事前起動の破棄は、ほかのスレッドから呼べる作り。
    // 起動の途中で終わると参照を手放す前にプロセスが終わるので、起動が終わるのを待ってから破棄し、手放すのを少し待つ。
    // これを呼ばずに終わると、窓なしの WAVE LOGGER(NRWL.exe)が 1 つ残った。
    public static void AbandonLoggerStartup(Form form)
    {
        try
        {
            object? session = form.GetType().GetField("_logger", Private)?.GetValue(form);
            if (session is null) return;
            var type = session.GetType();
            if (!(bool)type.GetProperty("HasPendingServerStartup")!.GetValue(session)!) return;
            var started = (Task)type.GetMethod("WaitForServerStartupAsync")!.Invoke(session, null)!;
            started.Wait(TimeSpan.FromSeconds(20));
            type.GetMethod("CancelServerStartup")!.Invoke(session, null);
            Thread.Sleep(1000);
        }
        catch (Exception ex)
        {
            Console.WriteLine("CLEANUP LOGGER 本体ソフトの事前起動を破棄できませんでした(窓の無い WAVE LOGGER が残ることがあります): " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (form.IsDisposed) return;
        try
        {
            var type = form.GetType();
            void Set(string name, object? value) => type.GetField(name, Private)!.SetValue(form, value);
            ((System.Windows.Forms.Timer)type.GetField("_statusHeartbeat", Private)!.GetValue(form)!).Stop();
            // 検査が立てたままの印(処理中・漏れ試験中・保存後の段階)があると終了処理が始まらないので、下ろす。終了の確認は出さない。
            Set("_operationInProgress", false);
            Set("_leakRunning", false);
            Set("_reviewStage", false);
            Set("_confirmClose", (Func<string, bool>)(_ => true));
            _ = form.Handle;
            form.Close();
            var closing = System.Diagnostics.Stopwatch.StartNew();
            while (!form.IsDisposed && closing.Elapsed < TimeSpan.FromSeconds(10)) { Application.DoEvents(); Thread.Sleep(5); }
            if (!form.IsDisposed) Console.WriteLine("CLEANUP 検査の失敗のあと、画面を終了処理で閉じられませんでした(窓の無い WAVE LOGGER が残ることがあります)。");
        }
        catch (Exception ex)
        {
            Console.WriteLine("CLEANUP 検査の失敗のあと、画面を終了処理で閉じられませんでした(窓の無い WAVE LOGGER が残ることがあります): " + ex.Message);
        }
    }
}
