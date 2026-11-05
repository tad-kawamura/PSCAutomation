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
            ((dynamic)app!).Visible = true;
            ((dynamic)app).DisplayAlerts = false;
            books = ((dynamic)app).Workbooks;
            book = ((dynamic)books).Add();
            sheets = ((dynamic)book).Worksheets;
            sheet = ((dynamic)sheets)[1];
            cell = ((dynamic)sheet).Range["A1"];
            foreach (bool pass in new[] { false, true })
            {
                ((dynamic)book).Activate();
                ((dynamic)cell).Select();
                ForegroundProbe.Activate(new IntPtr((int)((dynamic)app).Hwnd));
                type.GetField("_lastExcelPass", flags)!.SetValue(owner, pass);
                type.GetMethod("ShowJudgmentNotice", flags)!.Invoke(owner, null);
                var notice = (Form)type.GetField("_saveNotice", flags)!.GetValue(owner)!;
                // 別スレッドのExcelが処理待ちだった場合も、イベント処理後の入力先を確認する。
                Application.DoEvents();
                ForegroundProbe.AssertInput(notice);
                SendKeys.SendWait("{ENTER}");
                Application.DoEvents();
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
