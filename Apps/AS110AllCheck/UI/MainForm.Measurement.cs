using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PSCAutomation.Apps.AS110AllCheck;

// MainFormを役割別に分割。すべて同じFormの一部です。
// MainFormのうち、測定開始の条件確認と開始・終了の処理を担当する。
// 測定開始の操作(PC のボタン・M5Stack の A)を受け、通電・接続・雛形・LOGGERの状態を確認する。
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
        // 切断の通知は、受信の失敗・送信の失敗・シリアルのエラー通知の 3 か所から上がる。処理中・表示中は重ねて入らない
        // (2026-11-13、全体見直し・区切り 3。それまでは上がった数だけ同じ処理とポップアップが走った。下の ResetTemplate が画面を更新して
        // M5Stack へ表示を送り、その送信がまた失敗すると、ここへ入れ子で戻ってきた)。
        if (_arduinoDisconnectHandling) return;
        _arduinoDisconnectHandling = true;
        try
        {
            // 切れたと分かった時点でポートを閉じる。開いたままにすると、通信できないのに「接続済み」と表示し、表示を送るたびに同じ失敗を繰り返す。
            // 閉じた後は「M5Stack が未接続」になり、「確認」でつなぎ直す(下の案内と同じ)。
            // 「確認」の途中は閉じない(接続確認がポートを開け閉めしている最中で、つないだばかりのポートを閉じてしまうため。失敗したら接続確認が閉じる)。
            if (!_checkingConnections) CloseArduinoPort();
            _leakCancel?.Cancel();
            // 校正の読み取り中なら中止する(下で出力を止めるので、続けると 30 mA が出ていないまま読み取る。2026-11-14、全体見直し・区切り 4)。
            _autoCalibrationDialog?.Abort("M5Stack の通信が途切れた");
            try { StopLeakOutput(); } catch { }
            ResetTemplate();
            _nfCommunicationAlertShown = true;
            AppendLog("M5Stack の通信が途切れました。「確認」を押してください。");
            // 「確認」の途中で切れた(生存確認の失敗を含む)ときは、接続確認の結果にまとめて出すので、ここでは重ねて出さない。
            if (_checkingConnections) return;
            ErrorNotification.Show(this, "M5Stack の通信が途切れました。接続を確認してください。",
                "通信エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { _arduinoDisconnectHandling = false; }
    }

    // M5Stack の切断の処理中か(重ねて入らない。処理中は M5Stack へ表示も送らない)。
    private bool _arduinoDisconnectHandling;

    // 切断と分かったポートを閉じる。接続確認の外ではほかにポートを使う処理が無いので、その場で閉じ終わる。閉じるのに失敗しても切断の処理は続ける。
    private void CloseArduinoPort()
    {
        if (!_arduino.IsConnected) return;
        _arduino.DisconnectAsync().ContinueWith(
            task => ArduinoDiagnosticLog.Write("CLOSE_ERROR", "切断後のポートを閉じられません / " + task.Exception?.GetBaseException().Message),
            TaskContinuationOptions.OnlyOnFaulted);
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
            await BeginMeasurementAsync(receivedAt, requestVersion, enteredAt);
        }
        catch (Exception ex)
        {
            AppendLog("測定開始エラー：" + ex.Message);
            if (_closeRequested) RecordShutdownError("測定開始", ex);
            else ErrorNotification.Show(this, ex.Message, "測定開始エラー",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { _operationInProgress = false; ContinueRequestedClose(); }
    }

    // 測定開始の本体(押したときの確かめ → 発振器の準備 → LOGGER 開始 → トリガー)。失敗したら後始末をしてから例外を返す(表示は ExecuteMeasurement)。
    // 2026-11-13 に ExecuteMeasurement から分けた(機器なしの検査から、エラーのポップアップを出さずに呼べるように)。
    // 後始末は、どこまで進んだかで分ける(全体見直し・区切り 3)。それまでは、始める前の確かめで止まったとき(S/N が完了済み・保存先の範囲外・
    // 完了記録を読めない・参照波形が型式と合わない)も、残っていた測定結果(一時保存済み・転送済み)を破棄し、LOGGER に Stop を送っていた。
    private async Task BeginMeasurementAsync(long? receivedAt, long? requestVersion, long enteredAt)
    {
        // 発振器に触ったか(触った後の失敗は出力 OFF を試す)、工程を測定中にしたか(した後の失敗は LOGGER を止めて工程を破棄する)。
        bool generatorTouched = false, started = false;
        try
        {
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
            if (_flow.Mode != TestMode.Motion && !ReferenceLoadedForFlowModel)
                throw new InvalidOperationException("選択した型式の参照波形を読み込んでください。");
            generatorTouched = true;
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
            started = true;
            _captureDiscardedNotice = null;
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
        catch
        {
            if (generatorTouched) { try { StopLeakOutput(); } catch { } }
            if (started)
            {
                // 初回Start後の準備待ちで失敗した場合も、トリガ待ちを残さない。
                if (_logger.IsInitialized) { try { _logger.Stop(); } catch { } }
                _logger.ResetMeasurementStart();
                _measurementState.MeasurementStarted = false;
                _flow.Invalidate();
            }
            throw;
        }
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

    // 測定開始の後、LOGGER が「停止」以外になるのをまだ見ていない間 true(データ調整・データ取りだけ)。
    // 見るまでは「停止」を測定の終わりとみなさない。それまでは、開始から 0.5 秒後に「停止」と読めると終わったものとして一時保存し、
    // LOGGER が始まっていない・始まるのが遅れたときに、前回のデータ(または雛形の波形)が今回の結果になった(2026-11-14、区切り 3 の残り。人の決定)。
    private bool _awaitingLoggerStart;
    private long _loggerStartDeadline;
    private const int LoggerStartTimeoutMilliseconds = 5000;
    // 始まらなかったときの通知(検査では差し替える)。
    private Action<string> _showLoggerStartFailure;

    private void RefreshLoggerState()
    {
        long? read = null;
        if (_awaitingLoggerStart && !_flow.Measuring) _awaitingLoggerStart = false;
        if (_awaitingLoggerStart && Environment.TickCount64 >= _measurementState.ReadAfter)
        {
            long state = _logger.GetState();
            if (state == 1)
            {
                // まだ始まっていない。期限までは待ち、過ぎたら保存せずにやめる。
                if (Environment.TickCount64 >= _loggerStartDeadline) AbandonUnstartedMeasurement();
                return;
            }
            if (state > 0) _awaitingLoggerStart = false;
            read = state;
        }
        if (_measurementState.Refresh(() => read ?? _logger.GetState(), AppendLog, UseManualLoggerTrigger))
            OnMeasurementCompleted();
        if (_measurementState.State == 11 && _flow.Measuring)
        {
            _flow.Invalidate(); StopLeakOutput();
            throw new InvalidOperationException("LOGGERがエラー停止しました。設定を確認して再測定してください。");
        }
    }

    // 測定開始から期限を過ぎても LOGGER が「停止」のまま。一時保存せず、出力を止めて今回の測定をやめる。
    private void AbandonUnstartedMeasurement()
    {
        _awaitingLoggerStart = false;
        try { StopLeakOutput(); } catch { }
        if (_logger.IsInitialized) { try { _logger.Stop(); } catch { } }
        _logger.ResetMeasurementStart();
        _measurementState.MeasurementStarted = false;
        _measurementState.State = null;
        _measurementState.ReadAfter = 0;
        _flow.Invalidate();
        const string message = "LOGGER が測定を始めませんでした。今回の測定は保存していません。LOGGER の画面を確かめて、測り直してください";
        _captureDiscardedNotice = message;
        AppendLog($"測定開始エラー：{message}（開始から {LoggerStartTimeoutMilliseconds / 1000} 秒たっても LOGGER が「停止」のまま）");
        UpdateControls();
        _showLoggerStartFailure(message);
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
        // データ調整・データ取りは、LOGGER が「停止」以外になるのを見てから終わりを待つ(動き検査は保存しないので今までどおり)。
        _awaitingLoggerStart = _flow.Mode != TestMode.Motion;
        _loggerStartDeadline = Environment.TickCount64 + LoggerStartTimeoutMilliseconds;
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
