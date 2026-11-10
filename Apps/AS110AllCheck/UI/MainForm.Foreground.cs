namespace PSCAutomation.Apps.AS110AllCheck;

// 接続確認・校正値の適用まではアプリを最前面に保つ。
// 参照波形の読み込み操作後は固定を解除し、使用中のLOGGERへ前面を渡す。
internal sealed partial class MainForm : Form
{
    private void ShowTransferredExcel()
    {
        // 再測定で無効になった転送結果から、遅れてExcelを前面へ戻さない。
        if(_closeRequested || _operationInProgress || _flow.Measuring || !_flow.HasTransfer)return;
        if (_repeatConfirmationShown) return;
        var displayTimer=System.Diagnostics.Stopwatch.StartNew();
        TopMost = false;
        try
        {
            AppendLog(_excel?.BringToFront() == true
                ? "Excel転送完了：判定シートを最前面に表示しました。"
                : "Excel前面表示：Windowsが切り替えを許可しませんでした。Excel画面をクリックしてください。");
        }
        catch (Exception ex)
        {
            // 前面切替の失敗で転送済みの測定結果を無効にしない。
            AppendLog("Excel前面表示：" + ex.Message + " / Excel画面をクリックしてください。");
        }
        finally
        {
            AppendLog($"Excel転送時間：Excel前面表示処理={displayTimer.Elapsed.TotalMilliseconds:F0}ms");
            // Excelを表示してからポップアップへ入力先を戻す。
            if (_saveNotice is { IsDisposed: false, Visible: true } notice)
                ActivateJudgmentNotice(notice, notice);
        }
    }

    private void KeepPreparationInFront()
    {
        // モーダル／モデルレスのどちらでも、表示中のポップアップから入力先を奪わない。
        if (IsDisposed || OwnedForms.Any(form => form.Visible)) return;
        TopMost = true;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        if (IsHandleCreated)
        {
            BringToFront();
            Activate();
        }
    }

    // MainForm.Files.csが参照波形の読み込み操作後に呼ぶ。
    private void CompletePreparationDisplay()
    {
        if (IsDisposed) return;
        TopMost = false;
        if (IsHandleCreated)
            BeginInvoke(new Action(ShowLoggerAfterReferenceLoaded));
        // LOGGERで参照波形を確認している間に、初回転送用のExcelを準備する。
        RequestExcelPreparation();
    }

    private void ShowLoggerAfterReferenceLoaded()
    {
        if (IsDisposed || OwnedForms.Any(form => form.Visible) || _selectedReferencePath is null || !_templateLoaded || !_calibrationApplied ||
            !_logger.IsInitialized) return;

        ShowLoggerInFront("参照波形読み込み操作完了");
    }

    private void ShowLoggerInFront(string operation)
    {
        TopMost = false;
        try
        {
            // M5Stackのボタンから始めたときや、前回のExcelが前面のときは、アプリが直前の操作を受けていないため
            // 単純な前面要求はWindowsに拒否されやすい。Excelと同じく重なり順の変更と入力先の切替を両方行う。
            bool activated = WindowActivation.TryActivate(_logger.GetMainWindowHandle());
            AppendLog(activated
                ? $"{operation}：LOGGERを最前面に表示しました。"
                : "LOGGER前面表示：Windowsが切り替えを許可しませんでした。LOGGER画面をクリックしてください。");
        }
        catch (Exception ex)
        {
            // 表示切り替えの失敗を、雛形読み込み・測定設定の失敗にはしない。
            AppendLog($"{operation}のLOGGER前面表示：" + ex.Message + " / LOGGER画面をクリックしてください。");
        }
    }

}
