using System.Diagnostics;
using System.Runtime.Versioning;
using static PSCAutomation.Wrappers.Excel.ExcelCom;

namespace PSCAutomation.Wrappers.Excel;

/// <summary>保存済みのブックを Excel で開いて人に渡す。以後の計算・グラフ更新・保存は Excel と人に任せる。</summary>
/// <remarks>
/// 元: XdtConversion XdtConversion/ExcelPresenter.cs。失敗時のメッセージから XdtConversion 固有の一文(変換済みファイルは保存されています)を除いた。
/// 自前の STA スレッドで動くので、画面のスレッドを止めない。
/// </remarks>
[SupportedOSPlatform("windows")]
public static class ExcelViewer
{
    /// <summary>新しい Excel を画面に出してブックを開き、計算方法を自動にして人に渡す。戻り値は開くまでにかかった時間。</summary>
    /// <param name="progress">進み具合の通知(「Excel処理:…」)。停止箇所の調査に使う。</param>
    public static Task<TimeSpan> OpenForUserAsync(string path, Action<string>? progress = null)
    {
        var completion = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            object? appObject = null, booksObject = null, bookObject = null;
            bool opened = false;
            int processId = 0;
            Exception? failure = null;
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
                try { processId = ProcessIdOf(appObject); } catch { }
                dynamic app = appObject;
                app.Visible = true;
                // Excel の確認ダイアログは通常どおり表示する。
                app.DisplayAlerts = true;
                Report("Excel起動 完了 / ブック読込 開始");
                booksObject = app.Workbooks;
                dynamic books = booksObject;
                bookObject = books.Open(Path.GetFullPath(path), UpdateLinks: 0, ReadOnly: false);
                opened = true;
                Report("ブック読込 完了 / 自動計算設定 開始");
                app.Calculation = XlCalculationAutomatic;
                dynamic book = bookObject;
                book.Activate();
                app.ScreenUpdating = true;
                // 全再計算の強制、計算状態の監視、グラフの Refresh、再保存は行わない。
                timer.Stop();
                Report("表示要求 完了（以後の計算・グラフ更新はExcelに任せます）");
                completion.TrySetResult(timer.Elapsed);
            }
            catch (Exception ex)
            {
                failure = new InvalidOperationException(
                    "Excelの起動・表示処理に失敗しました。"
                    + Environment.NewLine + "停止箇所：" + stage
                    + Environment.NewLine + ex.Message, ex);
            }
            finally
            {
                // 開いたブックは人に引き渡す。アプリを終了しても閉じない。
                if (!opened && appObject != null)
                {
                    try { ((dynamic)appObject).Quit(); } catch { }
                }
                ReleaseAll(bookObject); ReleaseAll(booksObject); ReleaseAll(appObject);
                if (!opened && processId != 0)
                {
                    // 元の ExcelPresenter には無い。Excel は終了を頼まれてから実際に終わるまで少しかかり、
                    // その間にアプリが終わると Excel が裏に残った(Excel.Tests で確認)。終わるまで最大 10 秒待ってから失敗を返す。
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    try { using var process = Process.GetProcessById(processId); process.WaitForExit(10000); } catch { }
                }
                if (failure is not null) completion.TrySetException(failure);
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
