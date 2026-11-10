using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PSCAutomation.Apps.AS110AllCheck;

// MainFormを役割別に分割。すべて同じFormの一部です。
// MainFormのうち、測定開始の条件確認と開始・終了の処理を担当する。
// Arduinoの測定要求を受け、通電・接続・雛形・LOGGERの状態を確認する。
// LOGGERの測定開始に成功した直後、発振器へトリガーを送る。
// 非通電や通信異常の通知と、測定終了時の処理も担当する。
// 将来の測定終了後の自動保存は、このファイルへ追加する予定。
internal sealed partial class MainForm : Form
{
    private readonly LoggerMeasurementState _measurementState = new();
    private bool _nfCommunicationAlertShown;
    private bool _nfNotEnergizedAlertShown;

    private void ArduinoDisconnected()
    {
        if (_closeRequested || IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke((Action)ArduinoDisconnected); return; }
        _leakCancel?.Cancel();
        try { StopLeakOutput(); } catch { }
        ResetTemplate();
        _nfCommunicationAlertShown = true;
        AppendLog("Arduino の通信が途切れました。「確認」を押してください。");
        ErrorNotification.Show(this, "Arduino の通信が途切れました。接続を確認してください。",
            "通信エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void ArduinoNfStatusChanged()
    {
        if (_closeRequested || IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke((Action)ArduinoNfStatusChanged); return; }
        UpdateControls();
    }

    private void ShowNfNotEnergizedAlert()
    {
        if (_closeRequested || IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke((Action)ShowNfNotEnergizedAlert); return; }
        // ポップアップを表示している間の再押下では重ねて表示しない。
        if (_nfNotEnergizedAlertShown) return;
        _nfNotEnergizedAlertShown = true;
        try
        {
            AppendLog("通電していません。測定を開始しません。");
            ErrorNotification.Show(this, "通電していません", "通電確認",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { _nfNotEnergizedAlertShown = false; }
    }

    private void ArduinoMeasurementRequested()
    {
        if (_flow.Mode != TestMode.Motion) return;
        // C#がArduinoの測定要求を受け取った時刻。物理押下時刻ではない。
        long receivedAt = Stopwatch.GetTimestamp();
        long requestVersion = _arduino.MeasurementRequestVersion;
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            BeginInvoke((Action)(() => ExecuteMeasurement(receivedAt, requestVersion)));
            return;
        }
        ExecuteMeasurement(receivedAt, requestVersion);
    }

    private async void ExecuteMeasurement(long? receivedAt = null, long? requestVersion = null)
    {
        if (_closeRequested || _nfNotEnergizedAlertShown || _operationInProgress || _leakRunning || _reviewStage || _flow.CaptureFailed || _flow.SaveFailed) return;
        long enteredAt = Stopwatch.GetTimestamp();
        if (!CanAcceptMeasurementRequest(receivedAt, requestVersion)) return;
        bool repeatConfirmation = NeedsRepeatConfirmation;
        if (!ConfirmRepeatAfterPass("データ調整")) return;
        // 確認への回答を新しい操作として扱い、実行直前の通信状態を再確認する。
        if (repeatConfirmation) { receivedAt = null; requestVersion = null; enteredAt = Stopwatch.GetTimestamp(); }
        if (_flow.Mode == TestMode.Motion && !MotionContactEnergized)
        {
            ShowNfNotEnergizedAlert();
            return;
        }
        if (!ConnectionsReady || _operationInProgress)
        {
            AppendLog("接続確認と雛形読み込みが完了するまで測定できません。");
            return;
        }
        try
        {
            // 準備中のCOM呼び出しも終了処理と重ねない。
            _operationInProgress = true;
            UpdateControls();
            // LOGGER画面でのファイル変更も押下時に確認する。
            RefreshTemplateState();
            long templateCheckedAt = Stopwatch.GetTimestamp();
            if (!_templateLoaded || !_calibrationApplied)
            {
                AppendLog("校正値を入力し、設定済みの雛形を読み込んでください。");
                return;
            }
            RefreshLoggerState();
            long stateCheckedAt = Stopwatch.GetTimestamp();
            if (!LoggerReadyToStart)
            {
                AppendLog($"WAVE LOGGER は{GetLoggerStateText()}です。");
                return;
            }
            // 接続確認時に波形設定は準備済み。ここでは出力ONにしない。
            ConfigureProduct();
            if (_flow.Mode != TestMode.Motion && (_selectedReferencePath is null || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(_selectedReferencePath), System.Text.RegularExpressions.Regex.Escape(_flow.Model.Name) + @"(?![A-Za-z0-9])", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                throw new InvalidOperationException("選択した型式の参照波形を読み込んでください。");
            _generator!.PrepareBurst(CurrentBurstSettings);
            long preparedAt = Stopwatch.GetTimestamp();
            if (_closeRequested || !CanAcceptMeasurementRequest(receivedAt, requestVersion)) return;
            if (_flow.Mode == TestMode.Motion && !MotionContactEnergized)
            {
                ShowNfNotEnergizedAlert();
                return;
            }

            // LOGGER開始成功後すぐにトリガーを送り、画面ログはその後に書く。
            _flow.BeginMeasurement(DateTime.Now);
            _saveNotice?.Close(); _saveNotice=null;
            // 再測定ではExcelを操作せず保持し、確認・更新はExcel転送時だけ行う。
            _capturePath = null;
            UpdateControls();
            await StartMeasurementAsync(writeLog: false);
            if (_closeRequested) return;
            long loggerStartedAt = Stopwatch.GetTimestamp();
            _generator.TriggerBurst();
            long triggerSentAt = Stopwatch.GetTimestamp();

            // 出力開始を遅らせず、各回のデータ調整では測定中のLOGGERへ前面を渡す。
            if (_flow.Mode != TestMode.Motion) ShowLoggerInFront("データ調整開始", () => _flow.Measuring);

            AppendLog("WAVE LOGGER 測定開始");
            AppendLog($"{GetGeneratorModelName()} トリガー送信：{CurrentBurstSettings.Summary}");
            AppendLog($"開始処理時間(ms)：UI待ち={ElapsedMilliseconds(receivedAt ?? enteredAt, enteredAt):F1} / " +
                $"雛形確認={ElapsedMilliseconds(enteredAt, templateCheckedAt):F1} / " +
                $"状態確認={ElapsedMilliseconds(templateCheckedAt, stateCheckedAt):F1} / " +
                $"発振器準備={ElapsedMilliseconds(stateCheckedAt, preparedAt):F1} / " +
                $"LOGGER開始={ElapsedMilliseconds(preparedAt, loggerStartedAt):F1} / " +
                $"TRG送信={ElapsedMilliseconds(loggerStartedAt, triggerSentAt):F1} / " +
                $"受信→送信完了={ElapsedMilliseconds(receivedAt ?? enteredAt, triggerSentAt):F1}");
        }
        catch (Exception ex)
        {
            try { StopLeakOutput(); } catch { }
            // 初回Start後の準備待ちで失敗した場合も、トリガ待ちを残さない。
            if (_logger.IsInitialized) { try { _logger.Stop(); } catch { } }
            _logger.ResetMeasurementStart();
            _measurementState.MeasurementStarted = false;
            _flow.Invalidate();
            AppendLog("測定開始エラー：" + ex.Message);
            if (_closeRequested) RecordShutdownError("測定開始", ex);
            else ErrorNotification.Show(this, ex.Message, "測定開始エラー",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { _operationInProgress = false; ContinueRequestedClose(); }
    }

    private bool CanAcceptMeasurementRequest(long? receivedAt, long? requestVersion)
    {
        bool oldRequest = receivedAt.HasValue &&
            (Stopwatch.GetElapsedTime(receivedAt.Value) >= TimeSpan.FromSeconds(1) ||
             requestVersion != _arduino.MeasurementRequestVersion);
        if (oldRequest || !_arduino.MeasurementInputReady)
        {
            AppendLog("測定要求を取り消しました。通信状態が不明・復旧確認中、または要求が遅延しています。復旧後にもう一度操作してください。");
            ArduinoDiagnosticLog.Write("MEASURE_DROPPED", $"UIで開始を禁止 / oldRequest={oldRequest}");
            return false;
        }
        return true;
    }

    private static double ElapsedMilliseconds(long start, long end) =>
        (end - start) * 1000.0 / Stopwatch.Frequency;

    private bool LoggerReadyToStart => _measurementState.ReadyToStart ||
        (UseManualLoggerTrigger && _measurementState.State == 3);

    private bool UseManualLoggerTrigger => _flow.Mode == TestMode.Motion &&
        _logger.ActiveEdition == WaveLoggerEdition.WaveLoggerX;

    private void RefreshLoggerState()
    {
        if (_measurementState.Refresh(_logger.GetState, AppendLog, UseManualLoggerTrigger))
            OnMeasurementCompleted();
        if (_measurementState.State == 11 && _flow.Measuring)
        {
            _flow.Invalidate(); StopLeakOutput();
            throw new InvalidOperationException("LOGGERがエラー停止しました。設定を確認して再測定してください。");
        }
    }

    private void OnMeasurementCompleted()
    {
        if (_flow.Mode == TestMode.Motion) { _flow.Invalidate(); AppendLog("動き検査終了"); return; }
        if (!_flow.Measuring) return;
        RunOperation("測定終了・一時保存", () =>
        {
            try { CaptureLatest(); return "最新1回分を一時保存しました。Excel転送できます。"; }
            catch (InvalidDataException) { _flow.Invalidate(); throw; }
            catch { _flow.CaptureError(); throw; }
        });
    }

    private string GetLoggerStateText() => _measurementState.DisplayText;

    private async Task StartMeasurementAsync(bool writeLog = true)
    {
        long result = UseManualLoggerTrigger ? await _logger.StartAsync(AppendLog, () =>
        {
            if (_closeRequested || IsDisposed || Disposing || !ConnectionsReady || !_arduino.MeasurementInputReady ||
                (_flow.Mode == TestMode.Motion && !MotionContactEnergized))
                throw new InvalidOperationException("初回準備中に接続または通電状態が変わりました。測定を開始しません。");
        }) : _logger.StartSingleAcquisition();
        if (result != 0)
            throw new InvalidOperationException($"WAVE LOGGER の測定開始に失敗（戻り値 {result}）。");
        _measurementState.MeasurementStarted = true;
        _measurementState.State = 2;
        // GetStateは約200ms間隔で更新されるため、開始直後の古い停止情報を避ける。
        _measurementState.ReadAfter = Environment.TickCount64 + 500;
        if (writeLog) AppendLog("WAVE LOGGER 測定開始");
    }

    private void StartClicked(object? sender, EventArgs e)
    {
        ExecuteMeasurement();
    }

    private void GetStateClicked(object? sender, EventArgs e)
    {
        RunOperation("状態確認", () =>
        {
            RefreshLoggerState();
            if (ConnectionsReady) RefreshTemplateState();
            return $"GetState() = {_measurementState.State} / {GetLoggerStateText()}";
        });
    }
}
