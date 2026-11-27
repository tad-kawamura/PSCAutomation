using System.Runtime.InteropServices;

namespace PSCAutomation.Apps.AS100201SwitchController;

// Switches: 測定の開始・停止と、WF1973 の位相の出し直し。
// 版 6(2026-11-27、人の決定)で Arduino のスイッチ箱をなくした。押すのはタッチパネル(Tab5、MainForm.Panel.cs)と
// PC の画面の「測定開始（確認用）」「測定停止」。中で使う名前は今までのスイッチ名(PC_go / CR_go / F8 / sw4)のまま
// (位相の設定・台帳の「側」・受け付けの決まり As100201LearningGate をそのまま使うため)。
internal sealed partial class MainForm : Form
{
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

    // PC の画面の「測定停止」(版 6)。Tab5 が切れていても、パネルの処理中でも押せる。処理中なら止める。
    private void StopClicked(object? sender, EventArgs e)
    {
        _panelSequenceCancel?.Cancel();
        StopMeasurement(As100201LearningSession.StopByPcButton, "PC の測定停止");
    }

    // LOGGER を止める(状態が読めなくても送る)。測定中だったら測定の行を書いてから状態を読む(停止に戻っていれば xdt も保存する)。
    private void StopMeasurement(string trigger, string name)
    {
        if (!_logger.IsInitialized)
        {
            AppendLog($"{name}：WAVE LOGGER が初期化されていないため止めるものがありません。");
            return;
        }
        try
        {
            bool wasMeasuring = _measurementStarted;
            _logger.Stop();
            _measurementState.ReadAfter = 0;
            if (wasMeasuring) LearningMeasurementStopped(trigger);
            RefreshLoggerState();
            AppendLog($"{name}：WAVE LOGGER へ停止を要求しました。");
        }
        catch (Exception ex)
        {
            AppendLog($"{name} の実行に失敗：{ex.Message}");
            ErrorNotification.Show(this, ex.Message, $"{name} 実行エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SendPanelState();
            UpdateControls();
        }
    }

    // PC の画面の「測定開始（確認用）」(F8 と同じ。WF1973 は変えずに LOGGER だけ始める)。
    private void ExecuteSwitch(As100201Switch pressed)
    {
        using var observation = PcDelayObservation.Begin("UI.Switch:" + pressed.Line);
        AppendLog($"{pressed.DisplayName} を実行");
        if (!CanExecuteSwitch(pressed, out string refusal))
        {
            AppendLog(refusal);
            return;
        }
        try
        {
            RefreshTemplateState();
            if (!_templateLoaded) return;
            RefreshLoggerState();
            if (_measurementState.ReadyToStart) StartMeasurement();
            else AppendLog($"WAVE LOGGER は{_measurementState.GetDisplayText(describeHardwareStop: false)}です。");
        }
        catch (Exception ex)
        {
            AppendLog($"{pressed.DisplayName} の実行に失敗：{ex.Message}");
            ErrorNotification.Show(this, ex.Message, $"{pressed.DisplayName} 実行エラー",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { SendPanelState(); }
    }

    // WF1973 を選んだ位相で出し直す(出力中は OFF にせず、選んだ位相へ同期し直す)。終わったら true。
    // VISA は STA の UI スレッドで扱う。cancel が止まれと言ったら残りを送らずに false。
    private async Task<bool> RestartWaveformAsync(As100201Switch pressed, CancellationToken cancel)
    {
        if (_wf1973 is null) throw new InvalidOperationException("発振器（WF1973）が接続されていません。");
        int version = ++_waveformRequestVersion;
        int phase = _switchSettings.GetPhase(pressed);
        string name = PanelButtonName(pressed);
        _waveformRestartInProgress = true;
        UpdateControls();
        try
        {
            var restartTimer = System.Diagnostics.Stopwatch.StartNew();
            using var restartObservation = PcDelayObservation.Begin("WF1973.StartWithPhase:" + pressed.Line);
            bool completed = await _wf1973.StartWithPhaseAsync(As100201WaveformSettings.Continuous, phase,
                () => cancel.IsCancellationRequested || version != _waveformRequestVersion || _closing || IsDisposed || Disposing);
            if (!completed || IsDisposed) return false;
            _lastWaveform = (pressed, phase);
            AppendLog($"WF1973 出力開始：{name}（位相 {phase}°）");
            AppendLog($"処理時間：WF1973位相再開始 = {restartTimer.Elapsed.TotalMilliseconds:F1} ms");
            // 学習モード: 押した瞬間を台帳に残す(2026-11-12。通常モードでは何もしない)。
            LearningWaveformRestarted();
            return true;
        }
        finally
        {
            _waveformRestartInProgress = false;
            if (!IsDisposed && !Disposing) UpdateControls();
        }
    }
}
