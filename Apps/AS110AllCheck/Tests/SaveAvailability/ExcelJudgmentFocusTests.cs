using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class ExcelJudgmentFocusTests
{
    public static void Run(Form owner, Type type)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? app = null, books = null, book = null, sheets = null, sheet = null, cell = null;
        void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
        try
        {
            // テスト専用のExcelインスタンスと未保存ブックだけを操作する。
            app = Activator.CreateInstance(Type.GetTypeFromProgID("Excel.Application", true)!);
            ExcelLeftovers.Track(app);
            ((dynamic)app!).Visible = true;
            ((dynamic)app).DisplayAlerts = false;
            books = ((dynamic)app).Workbooks;
            book = ((dynamic)books).Add();
            sheets = ((dynamic)book).Worksheets;
            sheet = ((dynamic)sheets)[1];
            cell = ((dynamic)sheet).Range["A1"];
            // 合否ポップアップは自動で閉じる(MainForm.JudgmentNoticeDisplayMilliseconds)。PC が遅いとき(作り直した直後の 1 回目など)は、
            // 確かめの途中で自動で閉じ、閉じた画面を見て ObjectDisposedException で止まった(2026-11-20。34 件のあとで止まる件)。
            // Enter を送り終えるまでが自動で閉じる時間より十分短かった回だけを確かめに使い、間に合わなかった回は出し直してやり直す。
            int displayMilliseconds = (int)type.GetField("JudgmentNoticeDisplayMilliseconds", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
            long limitMilliseconds = displayMilliseconds - 100;
            const int maxAttempts = 5;
            foreach (bool pass in new[] { false, true })
            {
                for (int attempt = 1; ; attempt++)
                {
                    ((dynamic)book).Activate();
                    ((dynamic)cell).Select();
                    ForegroundProbe.Activate(new IntPtr((int)((dynamic)app).Hwnd));
                    type.GetField("_lastExcelPass", flags)!.SetValue(owner, pass);
                    type.GetMethod("ShowJudgmentNotice", flags)!.Invoke(owner, null);
                    // 自動で閉じるまでの時間は ShowJudgmentNotice の最後から数え始まるので、ここから測れば短めには出ない。
                    var sinceShown = System.Diagnostics.Stopwatch.StartNew();
                    var notice = type.GetField("_saveNotice", flags)!.GetValue(owner) as Form;
                    // 別スレッドのExcelが処理待ちだった場合も、イベント処理後の入力先を確認する。
                    Application.DoEvents();
                    bool inTime = notice is { IsDisposed: false } && sinceShown.ElapsedMilliseconds < limitMilliseconds;
                    if (inTime)
                    {
                        ForegroundProbe.AssertInput(notice!);
                        SendKeys.SendWait("{ENTER}");
                        Application.DoEvents();
                        // ここまでが自動で閉じる時間より短ければ、閉じたのは Enter(自動ではない)。
                        inTime = sinceShown.ElapsedMilliseconds < limitMilliseconds;
                    }
                    if (inTime) break;
                    // 間に合わなかった回: Enter が Excel に渡って選択が動いていることがあるので、閉じて最初(A1 を選ぶ所)からやり直す。
                    type.GetMethod("CloseJudgmentNotice", flags)!.Invoke(owner, null);
                    if (attempt >= maxAttempts)
                        throw new Exception($"Judgment popup closed by itself ({displayMilliseconds} ms) before the check finished, {maxAttempts} times in a row; the PC is too slow to check it");
                    Console.WriteLine($"RETRY judgment popup check did not finish within {limitMilliseconds} ms (attempt {attempt}); showing it again");
                }
                if (type.GetField("_saveNotice", flags)!.GetValue(owner) is not null)
                    throw new Exception("Enter did not close judgment popup after Excel foreground");
                object selected = ((dynamic)app).ActiveCell;
                try
                {
                    if ((string)((dynamic)selected).Address != "$A$1")
                        throw new Exception("Enter moved the Excel selection instead of only closing popup");
                }
                finally { Release(selected); }
                Console.WriteLine($"PASS real Excel foreground -> judgment pass={pass} -> Enter closes popup and leaves Excel A1 unchanged");
            }
        }
        finally
        {
            type.GetMethod("CloseJudgmentNotice", flags)!.Invoke(owner, null);
            try { if (book is not null) ((dynamic)book).Close(false); }
            finally
            {
                try { if (app is not null) ((dynamic)app).Quit(); }
                finally { Release(cell); Release(sheet); Release(sheets); Release(book); Release(books); Release(app); }
            }
        }
    }
}
