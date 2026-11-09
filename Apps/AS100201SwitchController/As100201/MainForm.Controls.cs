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
        _arduinoValueLabel.Text = _arduino.InputReady ? "Arduino　接続済み" : _arduino.IsConnected ? "定期応答の確認中" : "接続待ち（自動再確認）";
        _arduinoValueLabel.ForeColor = _arduino.InputReady ? Color.DarkGreen : Color.DarkRed;
        _wfValueLabel.Text = _wf1973 is not null ? "WF1973　接続済み" : "未接続";
        _wfValueLabel.ForeColor = _wf1973 is not null ? Color.DarkGreen : Color.DarkRed;
        _templateValueLabel.Text = _templateLoaded
            ? "雛形：" + Path.GetFileName(_templatePath) : "雛形：未登録";
        _readyValueLabel.Text = _readyLedOn ? "　準備完了 LED：点灯" : "　準備完了 LED：消灯";
        _readyValueLabel.ForeColor = _readyLedOn ? Color.DarkGreen : Color.DarkRed;
        _dropPanel.BackColor = ConnectionsReady && !_templateLoaded ? Color.AliceBlue : SystemColors.Control;

        _loggerComboBox.Enabled =
            _installedEditions.Count > 1 &&
            !_hardwareDetected &&
            !_operationInProgress;
        _initializeButton.Enabled =
            !_operationInProgress && !_checkingConnections && !_waveformRestartInProgress;
        _startButton.Enabled = !_operationInProgress && _readyLedOn && ConnectionsReady && _measurementState.ReadyToStart;
        _deleteTemplateButton.Enabled = !_operationInProgress && !_waveformRestartInProgress && _templateLoaded;
        _getStateButton.Enabled = !_operationInProgress && !_waveformRestartInProgress && initialized;
        UpdateLearningControls();
    }
}
