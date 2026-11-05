using System.Diagnostics;

namespace PSCAutomation.Apps.AS110AllCheck;

internal sealed partial class MainForm
{
    private bool _closeRequested, _shutdownStarted, _shutdownComplete;
    private int _shutdownErrors;

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (_shutdownComplete) { base.OnFormClosing(e); return; }
        e.Cancel = true;
        base.OnFormClosing(e);
        _closeRequested = true;
        _statusHeartbeat.Stop();
        Enabled = false;
        if (_shutdownStarted) return;
        if (_leakRunning)
        {
            _closeAfterLeak = true;
            RunShutdownStep("漏れ試験の中断要求", () => _leakCancel?.Cancel());
            return;
        }
        // COM操作に重ねて終了処理を開始しない。処理のfinallyから再度Closeする。
        if (_operationInProgress) return;
        _shutdownStarted = true;
        _operationInProgress = true;
        UseWaitCursor = true;
        try
        {
            RunShutdownStep("合否表示の終了", CloseJudgmentNotice);
            RunShutdownStep("発振器の出力停止", StopLeakOutput);
            _hardwareDetected = false;
            RunShutdownStep("測定状態の解除", ResetTemplate);
            await RunShutdownStepAsync("LOGGERの停止・終了確認", () => _logger.ShutdownAsync(ShutdownLog));
            if (_nrXConnection.WifiRestoreRequired)
                await RunShutdownStepAsync("Wi-Fi復帰", () => _nrXConnection.RestorePreviousWifiAsync());
        }
        finally
        {
            // これは終了手順の完了であり、通信できなかった機器の停止確認とは区別する。
            _shutdownComplete = true;
            _operationInProgress = false;
            UseWaitCursor = false;
            Close();
        }
    }

    private void ContinueRequestedClose()
    {
        if (!_closeRequested) { UpdateControls(); return; }
        if (!_shutdownStarted && !_operationInProgress && !_leakRunning && !IsDisposed && IsHandleCreated)
            BeginInvoke((Action)Close);
    }

    private void RunShutdownStep(string name, Action action)
    {
        try { action(); ShutdownLog($"アプリ終了：{name} 完了"); }
        catch (Exception ex) { RecordShutdownError(name, ex); }
    }

    private async Task RunShutdownStepAsync(string name, Func<Task> action)
    {
        try { await action(); ShutdownLog($"アプリ終了：{name} 完了"); }
        catch (Exception ex) { RecordShutdownError(name, ex); }
    }

    private void RecordShutdownError(string name, Exception ex)
    {
        _shutdownErrors++;
        string message=$"アプリ終了：{name} 失敗・未確認 / HRESULT=0x{ex.HResult:X8} / {ex.Message} / 後続の終了処理を継続します。";
        // 正常時のメモリ保持だけで終わらず、終了エラーは診断ファイルへ残す。
        try { ArduinoDiagnosticLog.Write("SHUTDOWN_ERROR", message); } catch { }
        ShutdownLog(message);
    }

    private void ShutdownLog(string message)
    {
        // ログ表示自体の失敗で、残りの接続解放を中断しない。
        try { if (!IsDisposed && !_logTextBox.IsDisposed) AppendLog(message); }
        catch (Exception ex) { Trace.WriteLine(message + " / ログ表示失敗：" + ex.Message); }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        RunShutdownStep("状態監視の解放", () => { _statusHeartbeat.Stop(); _statusHeartbeat.Dispose(); });
        RunShutdownStep("遅延観測の終了", StopPcDelayObservation);
        RunShutdownStep("漏れ試験の中断", () => _leakCancel?.Cancel());
        RunShutdownStep("Excelの解放", () => _excel?.Dispose());
        RunShutdownStep("一時測定ファイルの削除", () =>
        {
            if (Directory.Exists(_sessionFolder)) Directory.Delete(_sessionFolder, true);
        });
        RunShutdownStep("M5Stack接続の解放", _arduino.Dispose);
        RunShutdownStep("発振器接続の解放", () => _generator?.Dispose());
        RunShutdownStep("LOGGER接続の解放", _logger.Release);
        RunShutdownStep("一時雛形の削除", CleanupWorkingTemplate);
        ShutdownLog($"アプリ終了：終了手順完了 / 失敗・未確認={_shutdownErrors}件（機器の停止確認とは区別）");
        try
        {
            ArduinoDiagnosticLog.Write("END", $"アプリ終了手順完了 / 失敗・未確認={_shutdownErrors}件");
            ArduinoDiagnosticLog.FlushOnExit();
        }
        catch (Exception ex) { RecordShutdownError("診断ログの最終保存", ex); }
        finally { base.OnFormClosed(e); }
    }
}
