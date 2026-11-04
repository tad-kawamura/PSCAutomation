namespace PSCAutomation.Apps.AS100201SwitchController;

// UI上の状態更新と、Arduinoの自動再接続を進める。
// 接続が戻ってもWF1973やLOGGERの開始操作は行わない。
internal sealed partial class MainForm : Form
{
    private async void StatusHeartbeatTick(object? sender, EventArgs e)
    {
        if (_closing || _heartbeatBusy || _operationInProgress) return;
        _heartbeatBusy = true;
        try
        {
            if (!_arduino.InputReady)
            {
                _pendingWaveformSwitch = null;
                ++_waveformRequestVersion;
            }
            RefreshReadyLed();
            UpdateControls();
            await _arduino.MaintainAsync();
            if (_closing || IsDisposed || _operationInProgress) return;

            if (_logger.IsInitialized && _templateLoaded)
            {
                try
                {
                    RefreshTemplateState();
                    if (_templateLoaded) RefreshLoggerState();
                }
                catch (Exception ex)
                {
                    _measurementState.State = null;
                    if (!_measurementState.ErrorLogged)
                    {
                        _measurementState.ErrorLogged = true;
                        AppendLog("LOGGERの状態確認に失敗：" + ex.Message);
                    }
                }
            }

            // 5秒ごとの確認は、位相再開始のコマンド列へ割り込ませない。
            if (++_heartbeatCount % 10 == 0 && _wf1973 is not null && !_waveformRestartInProgress)
            {
                try { _wf1973.QueryIdentity(); }
                catch
                {
                    _wf1973.Dispose();
                    _wf1973 = null;
                    _pendingWaveformSwitch = null;
                    ++_waveformRequestVersion;
                    throw;
                }
                if (_templateLoaded && _hardwareDetected)
                {
                    long unit = _logger.GetUnitInfo(0);
                    bool alive = _logger.ActiveEdition == WaveLoggerEdition.Legacy
                        ? unit >= 0
                        : _detectedHardwareName == "NR-500" ? unit == 193 : unit is 198 or 199;
                    if (!alive)
                    {
                        _hardwareDetected = false;
                        ResetTemplate();
                        throw new InvalidOperationException($"NR本体を確認できません（GetUnitInfo={unit}）。");
                    }
                }
            }
            RefreshReadyLed();
            if (_readyLedOn) _arduino.SetReadyLed(true); // スケッチの3秒タイムアウトを更新
        }
        catch (Exception ex)
        {
            if (!_closing) AppendLog("接続状態の確認：" + ex.Message);
        }
        finally
        {
            _heartbeatBusy = false;
            if (!_closing && !IsDisposed)
            {
                RefreshReadyLed();
                UpdateControls();
            }
        }
    }
}
