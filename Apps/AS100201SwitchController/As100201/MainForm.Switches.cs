using System.Runtime.InteropServices;

namespace PSCAutomation.Apps.AS100201SwitchController;

// Switches: 画面・接続・操作を役割ごとに分割する。
internal sealed partial class MainForm : Form
{
    private void ArduinoDisconnected()
    {
        PostToUi(() =>
        {
            _readyLedOn = false;
            _pendingWaveformSwitch = null;
            ++_waveformRequestVersion;
            AppendLog("Arduinoの通信が途切れました。自動で再接続を確認します。");
            UpdateControls();
        });
    }

    private void ArduinoStatusChanged(string message)
    {
        PostToUi(() =>
        {
            if (_lastArduinoStatus == message) return;
            _lastArduinoStatus = message;
            AppendLog(message);
        });
    }

    private void PostToUi(Action action)
    {
        if (_closing || IsDisposed || !IsHandleCreated) return;
        try
        {
            if (InvokeRequired) BeginInvoke((Action)(() => { if (!_closing && !IsDisposed) action(); }));
            else action();
        }
        catch (InvalidOperationException) when (_closing || IsDisposed || !IsHandleCreated) { }
    }

    private void ArduinoSwitchPressed(As100201Switch pressed)
    {
        long receivedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        long version = _arduino.RequestVersion;
        PostToUi(() =>
        {
            if (version != _arduino.RequestVersion || !_arduino.InputReady ||
                System.Diagnostics.Stopwatch.GetElapsedTime(receivedAt).TotalSeconds >= 1)
            {
                ArduinoDiagnosticLog.Write("SWITCH_DROPPED", "遅延または復旧前の要求を破棄 / " + pressed.Line);
                return;
            }
            double waitMs = System.Diagnostics.Stopwatch.GetElapsedTime(receivedAt).TotalMilliseconds;
            ArduinoDiagnosticLog.Write("SWITCH_TIMING", $"{pressed.Line} / 受信からUI処理開始={waitMs:F1}ms / version={version}");
            ExecuteSwitch(pressed);
        });
    }

    private void ExecuteSwitch(As100201Switch pressed)
    {
        string name = pressed.Button;
        using var observation = PcDelayObservation.Begin("UI.Switch:" + pressed.Line);
        if (pressed.Kind == As100201PressKind.Extra)
        {
            // 2 回押しの後に続けて押された分は何もしない(2026-11-09 人と決めた仕様)。
            AppendLog($"{name} の続けての押下を捨てました。");
            return;
        }
        AppendLog($"スイッチ {pressed.DisplayName} を受信");
        if (!CanExecuteSwitch(pressed, out string refusal))
        {
            AppendLog(refusal);
            return;
        }

        ////////////////////////////////////////////////////////////////////////////////
        /// スイッチ操作の具体的な内容はここに記載してある。
        /// WF1973 の位相を切り替える場合は、PC_go / CR_go のみを使用する。
        /// WAVE LOGGER の測定開始／停止は F8 / sw4 のみを使用する。
        ////////////////////////////////////////////////////////////////////////////////
        try
        {
            RefreshTemplateState();
            if (!_templateLoaded) return;
            if (pressed.IsWaveform)
            {
                // 再押下も受け付け、処理中なら最後の押下で上書きする。
                // 1 回押し・2 回押しの区別はスケッチが行い、位相は画面の設定(As100201SwitchSettings)から選ぶ。
                RequestWaveformRestart(pressed);
            }
            else if (name == "F8")
            {
                // F8 は WF1973 を変更せず WAVE LOGGER だけを開始する。
                RefreshLoggerState();
                if (_measurementState.ReadyToStart) StartMeasurement();
                else AppendLog($"WAVE LOGGER は{_measurementState.GetDisplayText(describeHardwareStop: false)}です。");
            }
            else if (name == "sw4")
            {
                // 停止は状態取得失敗時にも送信できるようにする。
                bool wasMeasuring = _measurementStarted;
                _logger.Stop();
                _measurementState.ReadAfter = 0;
                // 学習モード: 測定中だったら、測定の行を書いてから状態を読む(停止に戻っていれば xdt も保存する)。
                if (wasMeasuring) LearningMeasurementStopped(As100201LearningSession.StopBySwitch);
                RefreshLoggerState();
                AppendLog("WAVE LOGGERへ停止を要求しました。");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"{pressed.DisplayName} の実行に失敗：{ex.Message}");
            ErrorNotification.Show(this, ex.Message, $"{pressed.DisplayName} 実行エラー",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RequestWaveformRestart(As100201Switch pressed)
    {
        _pendingWaveformSwitch = pressed;
        ++_waveformRequestVersion;
        if (_waveformRestartInProgress)
        {
            AppendLog($"WF1973 再開始を予約：{pressed.DisplayName}（処理中の操作を中断）");
            return;
        }

        _ = ProcessWaveformRequestsAsync();
    }

    private async Task ProcessWaveformRequestsAsync()
    {
        _waveformRestartInProgress = true;
        UpdateControls();
        try
        {
            while (_pendingWaveformSwitch is As100201Switch pressed && !_closing && !IsDisposed && !Disposing && ConnectionsReady)
            {
                _pendingWaveformSwitch = null;
                int version = _waveformRequestVersion;
                string name = pressed.DisplayName;
                // 位相はこの時点の画面の設定。処理中に変えた値は次の押下から効く。
                int phase = _switchSettings.GetPhase(pressed);

                // VISAはSTAのUIスレッドで扱う。新しい要求が来たら残りを中断する。
                // 出力中はOFFにせず、選択した位相へ同期し直す。
                var restartTimer = System.Diagnostics.Stopwatch.StartNew();
                using var restartObservation = PcDelayObservation.Begin("WF1973.StartWithPhase:" + name);
                bool completed = await _wf1973!.StartWithPhaseAsync(
                    As100201WaveformSettings.Continuous, phase, () => version != _waveformRequestVersion || !ConnectionsReady || _closing || IsDisposed || Disposing);
                if (completed && !IsDisposed)
                {
                    _lastWaveform = (pressed, phase);
                    AppendLog($"WF1973 出力開始：{name}（位相 {phase}°）");
                    AppendLog($"処理時間：WF1973位相再開始 = {restartTimer.Elapsed.TotalMilliseconds:F1} ms");
                    // 学習モード: 押した瞬間を台帳に残す(2026-11-12。通常モードでは何もしない)。
                    LearningWaveformRestarted();
                }
            }
        }
        catch (Exception ex)
        {
            _pendingWaveformSwitch = null;
            if (!IsDisposed && !Disposing)
            {
                AppendLog($"WF1973 の再開始に失敗：{ex.Message}");
                ErrorNotification.Show(this, ex.Message, "WF1973 実行エラー",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            _waveformRestartInProgress = false;
            if (!IsDisposed && !Disposing) UpdateControls();
        }
    }
}
