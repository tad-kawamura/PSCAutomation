using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PSCAutomation.Apps.AS110AllCheck;

// 接続確認・雛形の状態変化では、アプリを一時的に前面へ出す(最前面には固定しない)。
// 参照波形の読み込み操作後・データ調整・漏れ試験では、使用中のLOGGERへ前面を渡す。
// 2026-11-11 まではスタンド選択〜参照波形の読み込みまでアプリを最前面(TopMost)に固定していたが、
// データ調整でアプリの画面がLOGGERより前に出る報告(ver2.02)を受け、人の指示で固定をやめた。
// 各ポップアップ・ダイアログの最前面指定は、LOGGERの背後に隠れないよう残す。
internal sealed partial class MainForm : Form
{
    // LOGGERを前に出してから、出たままか確かめるまでの時間。
    private const int LoggerFrontRecheckMilliseconds = 500;
    // LOGGERの起動・雛形読み込みでLOGGERが遅れて前面に出る場合に、アプリを前へ戻し直すまでの時間。
    private const int PreparationFrontRecheckMilliseconds = 800;

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
        if (!RaisePreparationWindow()) return;
        // 参照波形の選択前だけ、LOGGERが遅れて前に出た分を1回だけ戻す。
        _ = RaisePreparationAgainAsync();
    }

    private bool RaisePreparationWindow()
    {
        // モーダル／モデルレスのどちらでも、表示中のポップアップから入力先を奪わない。
        if (IsDisposed || PopupVisible) return false;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        if (!IsHandleCreated) return false;
        // 一度だけ最前面にして重なり順を上げ、すぐ通常表示へ戻す(固定しない)。
        TopMost = true;
        TopMost = false;
        WindowActivation.TryActivate(this, this);
        return true;
    }

    private async Task RaisePreparationAgainAsync()
    {
        try
        {
            await Task.Delay(PreparationFrontRecheckMilliseconds);
            if (IsDisposed || _closeRequested || _selectedReferencePath is not null || _flow.Measuring || _leakRunning) return;
            if (GetForegroundWindow() == Handle) return;
            RaisePreparationWindow();
        }
        catch (Exception ex)
        {
            AppendLog("準備画面の前面表示：" + ex.Message);
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
        if (!CanShowLoggerAfterReference) return;
        ShowLoggerInFront("参照波形読み込み操作完了", () => CanShowLoggerAfterReference && !_flow.Measuring);
    }

    private bool CanShowLoggerAfterReference => !IsDisposed && !PopupVisible &&
        _selectedReferencePath is not null && _templateLoaded && _calibrationApplied && _logger.IsInitialized;

    // 表示中のポップアップ・ダイアログ。合否ポップアップは持ち主なしで出すため、OwnedFormsとは別に見る。
    private bool PopupVisible => OwnedForms.Any(form => form.Visible) ||
        _saveNotice is { IsDisposed: false, Visible: true };

    /// <summary>LOGGERを前面に出す。stillWantedを渡すと、少し後にLOGGERが前に残っているか確かめ、外れていれば1回だけ出し直す。</summary>
    private void ShowLoggerInFront(string operation, Func<bool>? stillWanted = null)
    {
        TopMost = false;
        try
        {
            // M5Stackのボタンから始めたときや、前回のExcelが前面のときは、アプリが直前の操作を受けていないため
            // 単純な前面要求はWindowsに拒否されやすい。Excelと同じく重なり順の変更と入力先の切替を両方行う。
            bool activated = WindowActivation.TryActivate(_logger.GetMainWindowHandle());
            AppendLog(activated
                ? $"{operation}：LOGGERを最前面に表示しました。"
                : $"{operation}：Windowsが切り替えを許可しませんでした（前面={DescribeForegroundWindow()}）。LOGGER画面をクリックしてください。");
        }
        catch (Exception ex)
        {
            // 表示切り替えの失敗を、雛形読み込み・測定設定の失敗にはしない。
            AppendLog($"{operation}のLOGGER前面表示：" + ex.Message + " / LOGGER画面をクリックしてください。");
            return;
        }
        if (stillWanted is not null) _ = RecheckLoggerInFrontAsync(operation, stillWanted);
    }

    private async Task RecheckLoggerInFrontAsync(string operation, Func<bool> stillWanted)
    {
        try
        {
            await Task.Delay(LoggerFrontRecheckMilliseconds);
            if (IsDisposed || _closeRequested || !stillWanted()) return;
            IntPtr logger = _logger.GetMainWindowHandle();
            IntPtr foreground = GetForegroundWindow();
            if (foreground == logger) return;
            string front = DescribeForegroundWindow();
            // 警告などアプリのポップアップが出ているときは、その入力先を奪わない。
            if (foreground != Handle && IsOwnProcessWindow(foreground))
            {
                AppendLog($"{operation}：LOGGERの前に{front}があるため、出し直しませんでした。");
                return;
            }
            bool activated = WindowActivation.TryActivate(logger);
            AppendLog(activated
                ? $"{operation}：LOGGERの前に{front}が出たため、LOGGERを出し直しました。"
                : $"{operation}：LOGGERの前に{front}が出ており、出し直しもWindowsに拒否されました。LOGGER画面をクリックしてください。");
        }
        catch (Exception ex)
        {
            AppendLog($"{operation}のLOGGER前面確認：" + ex.Message);
        }
    }

    // 前面の窓をログ用の言葉にする。原因の切り分け用(アプリの画面か、ほかのプロセスか)。
    private string DescribeForegroundWindow()
    {
        try
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero) return "なし";
            if (window == Handle) return "アプリのメイン画面";
            var title = new StringBuilder(256);
            GetWindowText(window, title, title.Capacity);
            GetWindowThreadProcessId(window, out uint processId);
            string processName;
            try { using var process = Process.GetProcessById((int)processId); processName = process.ProcessName; }
            catch (ArgumentException) { processName = "pid " + processId; }
            return IsOwnProcessWindow(window)
                ? $"アプリの画面「{title}」"
                : $"{processName}「{title}」";
        }
        catch (Exception ex)
        {
            return "不明(" + ex.Message + ")";
        }
    }

    private static bool IsOwnProcessWindow(IntPtr window)
    {
        GetWindowThreadProcessId(window, out uint processId);
        return processId == (uint)Environment.ProcessId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
}
