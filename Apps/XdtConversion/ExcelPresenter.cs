using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PSCAutomation.Apps.XdtConversion;

// 元: XdtConversion XdtConversion/ExcelPresenter.cs。変更は namespace のみ。
// Excel を起動する操作をどこまで Wrappers/Excel に置くかは判断待ち(docs/wrapper比較.md 判断 8)なので、アプリ側に置いたまま。

/// <summary>保存済みのブックをExcelで表示する。計算・グラフ更新はExcelに任せる。</summary>
public static class ExcelPresenter
{
    private static void Release(object? obj)
    {
        if (obj == null || !Marshal.IsComObject(obj)) return;
        try { Marshal.FinalReleaseComObject(obj); } catch { }
    }

    public static Task<TimeSpan> OpenAsync(string path, Action<string>? progress = null)
    {
        var completion = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            object? appObject = null, booksObject = null, bookObject = null;
            bool opened = false;
            string stage = "Excel起動";
            void Report(string value)
            {
                stage = value;
                progress?.Invoke("Excel処理：" + value);
            }
            try
            {
                var timer = Stopwatch.StartNew();
                Report("Excel起動 開始");
                var type = Type.GetTypeFromProgID("Excel.Application")
                    ?? throw new InvalidOperationException("デスクトップ版Excelが見つかりません。");
                appObject = Activator.CreateInstance(type)
                    ?? throw new InvalidOperationException("Excelを起動できませんでした。");
                dynamic app = appObject;
                app.Visible = true;
                // Excelの確認ダイアログは通常通り表示する。
                app.DisplayAlerts = true;
                Report("Excel起動 完了 / ブック読込 開始");
                booksObject = app.Workbooks;
                dynamic books = booksObject;
                bookObject = books.Open(Path.GetFullPath(path), UpdateLinks: 0, ReadOnly: false);
                opened = true;
                Report("ブック読込 完了 / 自動計算設定 開始");
                app.Calculation = -4105; // xlCalculationAutomatic
                dynamic book = bookObject;
                book.Activate();
                app.ScreenUpdating = true;
                // 全再計算の強制、計算状態の監視、グラフRefresh、再保存は行わない。
                timer.Stop();
                Report("表示要求 完了（以後の計算・グラフ更新はExcelに任せます）");
                completion.TrySetResult(timer.Elapsed);
            }
            catch (Exception ex)
            {
                completion.TrySetException(new InvalidOperationException(
                    "Excelの起動・表示処理に失敗しました。変換済みのExcelファイルは保存されています。"
                    + Environment.NewLine + "停止箇所：" + stage
                    + Environment.NewLine + ex.Message, ex));
            }
            finally
            {
                // 開いたブックは利用者へ引き渡す。アプリ終了時にも閉じない。
                if (!opened && appObject != null)
                {
                    try { ((dynamic)appObject).Quit(); } catch { }
                }
                Release(bookObject); Release(booksObject); Release(appObject);
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
