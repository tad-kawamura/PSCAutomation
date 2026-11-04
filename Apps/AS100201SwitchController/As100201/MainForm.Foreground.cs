namespace PSCAutomation.Apps.AS100201SwitchController;

// 接続確認・雛形読み込みまではアプリを最前面に保つ。
// 雛形の読み込み成功後は固定を解除し、使用中のLOGGERへ前面を渡す。
internal sealed partial class MainForm : Form
{
    private void KeepPreparationInFront()
    {
        if (_closing || IsDisposed) return;
        TopMost = true;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        if (IsHandleCreated)
        {
            BringToFront();
            Activate();
        }
    }

    // MainForm.Files.csが雛形の読み込み成功後に呼ぶ。
    private void CompletePreparationDisplay()
    {
        if (_closing || IsDisposed) return;
        TopMost = false;
        if (IsHandleCreated)
            BeginInvoke(new Action(ShowLoggerAfterTemplateLoaded));
    }

    private void ShowLoggerAfterTemplateLoaded()
    {
        if (IsDisposed || _closing || !_templateLoaded ||
            !_logger.IsInitialized) return;

        TopMost = false;
        try
        {
            bool activated = _logger.BringToFront();
            AppendLog(activated
                ? "雛形読み込み完了：LOGGERを最前面に表示しました。"
                : "LOGGER前面表示：Windowsが切り替えを許可しませんでした。LOGGER画面をクリックしてください。");
        }
        catch (Exception ex)
        {
            // 表示切り替えの失敗を、雛形読み込み・測定設定の失敗にはしない。
            AppendLog("LOGGER前面表示：" + ex.Message + " / LOGGER画面をクリックしてください。");
        }
    }

}
