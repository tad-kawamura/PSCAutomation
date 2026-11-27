namespace PSCAutomation.Apps.AS100201SwitchController;

// 接続確認・雛形の状態変化では、アプリを一時的に前面へ出す(最前面には固定しない)。
// 雛形の読み込み成功後は、使用中のLOGGERへ前面を渡す。
// 2026-11-13 までは雛形の読み込みまでアプリを最前面(TopMost)に固定していたが、
// AS110AllCheck(2026-11-11、LOGGERよりアプリの画面が前に出た)に合わせ、人の指示で固定をやめた。
// メッセージボックスなどの最前面指定は、LOGGERの背後に隠れないよう残す。
internal sealed partial class MainForm : Form
{
    private void KeepPreparationInFront()
    {
        if (_closing || IsDisposed) return;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        if (!IsHandleCreated) return;
        // 一度だけ最前面にして重なり順を上げ、すぐ通常表示へ戻す(固定しない)。
        TopMost = true;
        TopMost = false;
        WindowActivation.TryActivate(this, this);
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
            // スイッチ・タッチパネルなど外部から始めたときは、Windowsに入力先の切り替えを断られることがある。
            // 断られても画面は上に出るよう、先に重なり順を上げてから入力先の切替を試す(AS110AllCheck と同じ)。
            IntPtr window = _logger.GetMainWindowHandle();
            WindowActivation.BringToTop(window);
            bool activated = WindowActivation.TryActivate(window);
            AppendLog(activated
                ? "雛形読み込み完了：LOGGERを最前面に表示しました。"
                : "雛形読み込み完了：LOGGERを前に出しました（Windowsが入力先の切り替えを許可しないため、入力するときはLOGGER画面をクリックしてください）。");
        }
        catch (Exception ex)
        {
            // 表示切り替えの失敗を、雛形読み込み・測定設定の失敗にはしない。
            AppendLog("LOGGER前面表示：" + ex.Message + " / LOGGER画面をクリックしてください。");
        }
    }

    // タッチパネルの操作で LOGGER の画面が変わったら(測り始め・止める・位相の出し直し)、毎回 LOGGER を前に出す(2026-11-27 人の指示)。
    // Tab5 から呼ぶので、Windows は入力先の切り替えを断ることが多い。そのときも重なり順は上げてあるので、ログには出さない。
    // PC の画面のボタン(測定停止・測定開始(確認用))では出さない(押した人がアプリの画面を見ているため。AI の決め)。
    private void ShowLoggerForPanel(string operation)
    {
        if (_closing || IsDisposed || !_templateLoaded || !_logger.IsInitialized) return;
        try
        {
            IntPtr window = _logger.GetMainWindowHandle();
            if (!WindowActivation.BringToTop(window))
            {
                AppendLog($"{operation}：LOGGER を前に出せませんでした。LOGGER 画面をクリックしてください。");
                return;
            }
            WindowActivation.TryActivate(window);
        }
        catch (Exception ex)
        {
            // 前に出せなくても、測定・保存は止めない。
            AppendLog($"{operation}のLOGGER前面表示：" + ex.Message);
        }
    }
}
