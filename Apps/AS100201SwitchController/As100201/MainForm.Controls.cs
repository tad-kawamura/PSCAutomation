namespace PSCAutomation.Apps.AS100201SwitchController;

internal sealed partial class MainForm : Form
{
    private void UpdateControls()
    {
        if (_closing || IsDisposed) return;
        bool initialized = _logger.IsInitialized;
        _statusValueLabel.Text = initialized
            ? $"初期化済み（{WaveLoggerSession.GetEditionDisplayName(_logger.ActiveEdition)}） / {_measurementState.GetDisplayText(describeHardwareStop: false)}"
            : "未初期化";
        _statusValueLabel.ForeColor = initialized ? Color.DarkGreen : Color.DarkRed;

        _hardwareValueLabel.Text = _hardwareDetected
            ? $"{_detectedHardwareName}（接続確認済み）"
            : "未確認";
        _hardwareValueLabel.ForeColor = _hardwareDetected ? Color.DarkGreen : Color.DarkRed;
        UpdatePanelControls();
        _wfValueLabel.Text = _wf1973 is not null ? "WF1973　接続済み" : "未接続";
        _wfValueLabel.ForeColor = _wf1973 is not null ? Color.DarkGreen : Color.DarkRed;
        _templateValueLabel.Text = _templateLoaded
            ? "雛形：" + Path.GetFileName(_templatePath) : "雛形：未登録";
        _readyValueLabel.Text = _readyToMeasure ? "　測定の準備：できています" : "　測定の準備：できていません";
        _readyValueLabel.ForeColor = _readyToMeasure ? Color.DarkGreen : Color.DarkRed;
        _dropPanel.BackColor = ConnectionsReady && !_templateLoaded ? Color.AliceBlue : SystemColors.Control;

        _loggerComboBox.Enabled =
            _installedEditions.Count > 1 &&
            !_hardwareDetected &&
            !_operationInProgress;
        _initializeButton.Enabled =
            !_operationInProgress && !_checkingConnections && !_waveformRestartInProgress && !PanelBusy;
        _startButton.Enabled = !_operationInProgress && !PanelBusy && _readyToMeasure && ConnectionsReady && _measurementState.ReadyToStart;
        // 測定停止は、パネルの処理中(PC / CR の区切りなど)も押せる(処理を止める。AGENTS.md「画面で待ちのある処理」)。
        _stopButton.Enabled = !_operationInProgress && _logger.IsInitialized && _templateLoaded;
        _deleteTemplateButton.Enabled = !_operationInProgress && !_waveformRestartInProgress && !PanelBusy && _templateLoaded;
        _getStateButton.Enabled = !_operationInProgress && !_waveformRestartInProgress && !PanelBusy && initialized;
        UpdateLearningControls();
        UpdateNextStep();
        // 文字が変わって行の高さが変わったら、スクロールの中の大きさを合わせ直す。
        FitLayoutRoot();
    }
}
