using System.Runtime.InteropServices;
using System.Diagnostics;

namespace PSCAutomation.Apps.AS100201SwitchController;

// Connections: 画面・接続・操作を役割ごとに分割する。
internal sealed partial class MainForm : Form
{
    private bool _checkingConnections, _startupTimingRecorded;

    // NR本体はUSBだけで接続する(2026-11-04 移植時に決定。元はUSB／Wi-Fiを画面で選べた)。
    private void ConfigureLoggerSelection()
    {
        WaveLoggerEdition previousChoice = _loggerComboBox.SelectedItem is WaveLoggerEdition existing
            ? existing : WaveLoggerEdition.Legacy;
        _installedEditions = WaveLoggerSession.GetInstalledEditions();
        _loggerComboBox.Items.Clear();

        foreach (WaveLoggerEdition edition in _installedEditions)
        {
            _loggerComboBox.Items.Add(edition);
        }

        if (_installedEditions.Count == 0)
        {
            _loggerSelectionValueLabel.Text = "LOGGERアプリが見つかりません";
            _loggerSelectionValueLabel.ForeColor = Color.DarkRed;
            _loggerComboBox.Visible = false;
            return;
        }

        if (_installedEditions.Count == 1)
        {
            WaveLoggerEdition edition = _installedEditions[0];
            _loggerSelectionValueLabel.Text =
                $"{WaveLoggerSession.GetEditionDisplayName(edition)}（自動選択）";
            _loggerSelectionValueLabel.ForeColor = Color.DarkGreen;
            _loggerComboBox.SelectedIndex = 0;
            _loggerComboBox.Visible = false;
            return;
        }

        _loggerSelectionValueLabel.Text = "両方インストール済み：";
        _loggerSelectionValueLabel.ForeColor = SystemColors.ControlText;
        _loggerComboBox.SelectedItem = _installedEditions.Contains(previousChoice)
            ? previousChoice : WaveLoggerEdition.Legacy;
        _loggerComboBox.Visible = true;
    }

    private WaveLoggerEdition GetSelectedLoggerEdition()
    {
        if (_installedEditions.Count == 0)
        {
            throw new InvalidOperationException(
                "WAVE LOGGER XまたはWAVE LOGGERをインストールしてください。");
        }

        if (_installedEditions.Count == 1)
        {
            return _installedEditions[0];
        }

        return _loggerComboBox.SelectedItem is WaveLoggerEdition edition
            ? edition
            : throw new InvalidOperationException("使用するLOGGERを選択してください。");
    }

    private bool IsWaveLoggerXInstalled()
    {
        foreach (WaveLoggerEdition edition in _installedEditions)
        {
            if (edition == WaveLoggerEdition.WaveLoggerX)
            {
                return true;
            }
        }

        return false;
    }

    private async void InitializeClicked(object? sender, EventArgs e) => await CheckConnectionsAsync();

    private async Task CheckConnectionsAsync()
    {
        if (_operationInProgress) return;
        _checkingConnections = true;
        KeepPreparationInFront();
        try
        {
            await RunOperationAsync("接続確認", async () =>
            {
                var totalTimer = Stopwatch.StartNew();
                AppendLog("接続確認開始：Arduino確認と発振器／LOGGER起動を並行して進めます。");
                LogProcessStartupTime("接続確認開始まで");
                var preparationTimer = Stopwatch.StartNew();
                if (_measurementState.MeasurementStarted && _logger.IsInitialized)
                {
                    // LOGGERを閉じてCOMが無効になっていても、再接続へ進む。
                    try { _logger.Stop(); }
                    catch (Exception ex) { AppendLog("LOGGER停止確認：" + ex.Message + " / 接続を作り直します。"); }
                }
                _measurementState.MeasurementStarted = false;
                if (!_logger.IsInitialized) ConfigureLoggerSelection();
                // COM参照が残っていても、LOGGER画面が開いているとは限らない。
                // 「確認」では毎回解放し、InitializeとVisible=trueを実行し直す。
                ResetTemplate();
                _measurementState.State = null;
                _measurementState.ReadAfter = 0;
                _measurementState.ErrorLogged = false;
                if (_logger.IsInitialized)
                {
                    try { _logger.CloseActiveFile(); } catch { /* 閉じたLOGGERの古い参照なら読み直す */ }
                    _logger.Release();
                    AppendLog("LOGGERの旧接続を解除しました。サーバーを再起動・初期化します。");
                }
                _hardwareDetected = false;
                _detectedHardwareName = string.Empty;
                preparationTimer.Stop();
                AppendLog($"処理時間：接続前の準備・旧接続解除 = {preparationTimer.Elapsed.TotalSeconds:F3} 秒");

                var failures = new List<string>();
                // Arduinoの起動待ちを、発振器確認・LOGGER起動と重ねる。
                // VISA／LOGGERのCOM操作は、従来どおりUIスレッドで実行する。
                // Arduino計測は別スレッドで完結させ、UIの待ち時間を含めない。
                var arduinoTask = Task.Run(async () =>
                {
                    var timer = Stopwatch.StartNew();
                    Exception? error = null;
                    var details = new List<string>();
                    try { await _arduino.VerifyAsync(details.Add).ConfigureAwait(false); }
                    catch (Exception ex) { error = ex; }
                    timer.Stop();
                    return (Elapsed: timer.Elapsed, Error: error, Details: details);
                });
                var wfTimer = Stopwatch.StartNew();
                if (_wf1973 is null)
                {
                    try { _wf1973 = Wf1973ContinuousSession.Connect(As100201WaveformSettings.Waveform); }
                    catch (Exception ex) { failures.Add("発振器: " + ex.Message); }
                }
                else
                {
                    try
                    {
                        _wf1973.QueryIdentity();
                        // 接続済みでも「確認」では波形の形だけ送り直す(2026-11-04 決定)。
                        // 動作中に別のアプリや前面パネルで DC などにされても、ここで三角波に戻せる。
                        // 出力の ON / OFF・振幅・オフセット・位相は変えない(次の PC_go / CR_go で送る)。
                        _wf1973.SetWaveform(As100201WaveformSettings.Waveform);
                        AppendLog("WF1973：波形の形を三角波に設定しました。");
                    }
                    catch (Exception ex)
                    {
                        _wf1973.Dispose();
                        _wf1973 = null;
                        failures.Add("発振器: " + ex.Message);
                    }
                }

                wfTimer.Stop();
                AppendLog($"処理時間：発振器接続確認 = {wfTimer.Elapsed.TotalSeconds:F3} 秒");

                var loggerTimer = Stopwatch.StartNew();
                try
                {
                WaveLoggerEdition selectedEdition = GetSelectedLoggerEdition();
                WaveLoggerSession.HardwareCheckResult check = DetectUsbHardwareWithFallback(selectedEdition);

                _hardwareDetected = check.IsDetected;
                _detectedHardwareName = check.IsDetected
                    ? check.HardwareName
                    : string.Empty;

                if (!_hardwareDetected)
                    throw new InvalidOperationException("NR本体を確認できません / " + check.Description);
                }
                catch (Exception ex)
                {
                    failures.Add("WAVE LOGGER／NR本体: " + ex.Message);
                }

                loggerTimer.Stop();
                AppendLog($"処理時間：LOGGER／NR接続処理全体 = {loggerTimer.Elapsed.TotalSeconds:F3} 秒");

                // Arduino確認の完了まで待ってから、全機器の結果をまとめる。
                var arduinoResult = await arduinoTask;
                if (_closing) return "終了中";
                foreach (string detail in arduinoResult.Details) AppendLog(detail);
                AppendLog($"処理時間：Arduino接続確認 = {arduinoResult.Elapsed.TotalSeconds:F3} 秒" +
                    (arduinoResult.Error is null ? "（成功）" : "（失敗）"));
                if (arduinoResult.Error is not null)
                    failures.Insert(0, "Arduino: " + arduinoResult.Error.Message);
                totalTimer.Stop();
                AppendLog($"処理時間：接続確認全体（並行実行） = {totalTimer.Elapsed.TotalSeconds:F3} 秒");
                LogProcessStartupTime("接続確認終了まで", complete: true);

                if (failures.Count > 0)
                    throw new InvalidOperationException(string.Join(Environment.NewLine, failures));

                KeepPreparationInFront();
                return $"正常終了 / 本体={_detectedHardwareName} / 雛形待ち";
            });
        }
        finally
        {
            _checkingConnections = false;
            UpdateControls();
        }
    }

    // 選択したLOGGERで確認し、旧LOGGERで見つからなければLOGGER Xを試す。
    // NR-Xの本体確認はLOGGER XのAutomation Serverで行う必要がある。
    private WaveLoggerSession.HardwareCheckResult DetectUsbHardwareWithFallback(
        WaveLoggerEdition selectedEdition)
    {
        string previousFailure;
        try
        {
            var check = DetectUsbHardware(selectedEdition);
            if (check.IsDetected) return check;
            previousFailure = check.Description;
            if (selectedEdition != WaveLoggerEdition.Legacy)
                return check;
        }
        catch (Exception ex) when (selectedEdition == WaveLoggerEdition.Legacy &&
            IsWaveLoggerXInstalled())
        {
            previousFailure = ex.Message;
        }

        AppendLog("旧WAVE LOGGERで本体を確認できません：" + previousFailure);
        if (!IsWaveLoggerXInstalled())
            throw new InvalidOperationException(
                "旧WAVE LOGGERでNR-500を確認できません / " + previousFailure +
                "。NR-Xを確認するにはWAVE LOGGER Xのインストールが必要です。");

        AppendLog("本体確認を継続：WAVE LOGGER Xへ自動切替し、NR-500 → NR-Xの順に確認します。");
        _logger.Release();
        _loggerComboBox.SelectedItem = WaveLoggerEdition.WaveLoggerX;
        try
        {
            var check = DetectUsbHardware(WaveLoggerEdition.WaveLoggerX);
            if (!check.IsDetected)
                throw new InvalidOperationException(
                    "NR-500／NR-Xを確認できません / 旧LOGGER: " + previousFailure +
                    " / LOGGER X: " + check.Description);
            AppendLog("LOGGER自動切替：成功 / WAVE LOGGER X / 本体=" + check.HardwareName);
            return check;
        }
        catch
        {
            // 不完全な初期化状態を残さず、次の「確認」で接続し直す。
            _logger.Release();
            throw;
        }
    }

    private WaveLoggerSession.HardwareCheckResult DetectUsbHardware(WaveLoggerEdition edition)
    {
        AppendLog("USB本体確認：使用ソフト=" + WaveLoggerSession.GetEditionDisplayName(edition));
        if (!_logger.IsInitialized)
            MeasureConnectionStep("LOGGERサーバー起動・初期化", () =>
                _logger.Initialize(edition, AppendLog));

        var nr500 = MeasureConnectionStep("NR-500本体確認（USB）", () =>
            _nr500Connection.DetectUsb(0));
        AppendLog("NR-500確認結果：" + (nr500.IsDetected ? "検出" : "未検出") + " / " + nr500.Description);
        if (nr500.IsDetected || edition == WaveLoggerEdition.Legacy) return nr500;

        // 元は NrXConnection.DetectUsb(0)。Wi-Fi を使わないので同じ呼び出しを直接行う。
        var nrX = MeasureConnectionStep("NR-X本体確認（USB）", () =>
            _logger.DetectNrX(0, NrXConnectionMethod.Usb));
        AppendLog("NR-X確認結果：" + (nrX.IsDetected ? "検出" : "未検出") + " / " + nrX.Description);
        return nrX;
    }

    private void MeasureConnectionStep(string name, Action action)
    {
        var timer = Stopwatch.StartNew();
        try { action(); }
        finally
        {
            timer.Stop();
            AppendLog($"処理時間：{name} = {timer.Elapsed.TotalSeconds:F3} 秒");
        }
    }

    private void LogProcessStartupTime(string stage, bool complete = false)
    {
        if (_startupTimingRecorded) return;
        try
        {
            using var process = Process.GetCurrentProcess();
            double seconds = (DateTime.Now - process.StartTime).TotalSeconds;
            AppendLog($"起動時間：プロセス起動から{stage} = {seconds:F3} 秒（初期化・画面準備を含む）");
            if (complete && seconds >= 10)
                ArduinoDiagnosticLog.Write("STARTUP_SLOW", $"プロセス起動から接続確認終了={seconds:F3}秒 / 接続処理のAPPログで内訳を確認");
        }
        catch (Exception ex)
        {
            AppendLog("起動時間の取得不可：" + ex.Message);
        }
        finally { if (complete) _startupTimingRecorded = true; }
    }

    private T MeasureConnectionStep<T>(string name, Func<T> action)
    {
        var timer = Stopwatch.StartNew();
        try { return action(); }
        finally
        {
            timer.Stop();
            AppendLog($"処理時間：{name} = {timer.Elapsed.TotalSeconds:F3} 秒");
        }
    }}
