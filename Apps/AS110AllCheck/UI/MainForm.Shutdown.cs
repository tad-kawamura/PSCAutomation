using System.Diagnostics;

namespace PSCAutomation.Apps.AS110AllCheck;

internal sealed partial class MainForm
{
    private bool _closeRequested, _shutdownStarted, _shutdownComplete;
    private int _shutdownErrors;

    // 終了の確認(2026-11-14、全体見直し・区切り 6、人の決定)。測定・漏れ試験の途中や、保存・完了していない測定結果があるときだけ聞く
    // (既定は「いいえ」)。それまでは「アプリ終了」や × を押すと、聞かずに終了処理へ進み、測定結果が失われた。
    // 何も残っていないとき(測定待ち・データ調整の結果だけ・動き検査の結果だけ)は、今までどおり聞かずに終わる。
    // 検査では、画面を出さずに答えを差し替える。
    private Func<string, bool> _confirmClose;
    private bool _closeConfirmShown;

    // 終了すると失われるものがあれば、その説明(無ければ null)。
    private string? CloseWarning()
    {
        if (_leakRunning) return "漏れ試験の途中です。終了すると、試験は中断になります（漏れ試験記録には「中断」が残ります）。";
        if (_flow.Measuring) return "測定の途中です。終了すると、この測定は破棄されます。";
        // 保存するのはデータ取りだけ(データ調整・動き検査は保存しないので、結果が残っていても聞かない)。
        if (_flow.Mode != TestMode.Production) return null;
        string serial = _flow.Serial.Length > 0 ? $"S/N {_flow.Serial} の" : "";
        if (_flow.CaptureFailed) return $"{serial}測定は、一時保存に失敗したままです。終了すると、この測定は失われます。";
        if (_flow.SaveFailed) return $"{serial}測定結果は、保存に失敗したままです。終了すると、この測定結果は失われます。";
        if (_flow.HasSaved) return $"{serial}測定結果は保存済みですが、「合格」（完了）をまだ押していません。終了すると、この S/N は完了（保護）になりません。";
        if (_flow.HasCapture) return $"{serial}測定結果を、まだ保存していません。終了すると、この測定結果は失われます。";
        return null;
    }

    private bool AskClose(string warning)
    {
        using var dialog = new DefaultNoDialog(
            warning + "\r\n\r\nアプリを終了しますか？\r\n「はい」：終了する\r\n「いいえ」：終了しない（このまま続ける）", Font, "アプリ終了の確認");
        dialog.Shown += (_, _) => ActivateJudgmentNotice(dialog, dialog.NoButton);
        return dialog.ShowDialog(this) == DialogResult.Yes;
    }

    // 想定外のエラーのときに、発振器の出力 OFF を試す(UnexpectedErrors)。結果を、知らせに出す文で返す。
    // 画面のスレッドからなら、出力の状態(出力中・不明)も合わせて更新する。別のスレッドからは、出力 OFF を送るだけにする。
    internal string EmergencyOutputOff(bool onScreenThread)
    {
        try
        {
            var generator = _generator;
            if (generator is null && !_leakOutputReady && !_generatorOutputUnknown) return "発振器は未接続のため、出力OFFは送っていません。";
            if (onScreenThread) StopLeakOutput();
            else if (generator is null) return "発振器が未接続のため、出力OFFを送れません。発振器本体で出力をOFFにしてください。";
            else generator.StopOutput();
            return "発振器へ出力OFFを送信しました。";
        }
        catch (Exception ex)
        {
            return onScreenThread ? ex.Message : "発振器の出力OFFを送信できませんでした（" + ex.Message + "）。発振器本体で出力をOFFにしてください。";
        }
    }

    internal void LogUnexpectedError(Exception ex, string output) =>
        ShutdownLog($"想定外のエラー：{ex.GetType().Name} / {ex.Message} / {output} / アプリを終了して起動し直してください。");

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (_shutdownComplete) { base.OnFormClosing(e); return; }
        // Windows の終了・サインアウトのときは聞かない(確認の画面で Windows の終了を止めないため)。
        if (!_closeRequested && e.CloseReason != CloseReason.WindowsShutDown && CloseWarning() is { } warning)
        {
            // 確認を出している間に重ねて来た終了の要求は、受けない。
            if (_closeConfirmShown) { e.Cancel = true; return; }
            _closeConfirmShown = true;
            bool close;
            try { close = _confirmClose(warning); }
            finally { _closeConfirmShown = false; }
            if (!close)
            {
                e.Cancel = true;
                AppendLog("アプリ終了：取りやめました / " + warning);
                return;
            }
            AppendLog("アプリ終了：確認のうえ終了します / " + warning);
        }
        e.Cancel = true;
        base.OnFormClosing(e);
        _closeRequested = true;
        // 合否ポップアップは持ち主なしで出しているので、メイン画面と一緒には閉じない。ここで閉じる。
        CloseJudgmentNotice();
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
            // 接続確認の前に閉じたときは、先に起動した LOGGER の参照を手放す(受け取り済みなら何もしない)。
            RunShutdownStep("LOGGER事前起動の破棄", _logger.CancelServerStartup);
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
        RunShutdownStep("一時フォルダーの印の解放", ReleaseTemporaryFolderLocks);
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
        finally
        {
            // 画面ログをファイルへ書き終える(ディスクが応答しなくても 2 秒で進める)。
            try { ScreenLogFile.Default.Flush(TimeSpan.FromSeconds(2)); } catch { }
            UnexpectedErrors.Unregister(this);
            base.OnFormClosed(e);
        }
    }
}
