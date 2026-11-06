using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PSCAutomation.Apps.AS110AllCheck;

// MainFormを役割別に分割。すべて同じFormの一部です。
internal sealed partial class MainForm : Form
{
    private int _heartbeatCount;
    private IReadOnlyList<WaveLoggerEdition> _installedEditions = [];
    private readonly SingleAdapterWifiManager _wifiManager = new();
    private readonly Nr500Connection _nr500Connection;
    private readonly NrXConnection _nrXConnection;
    private bool _hardwareDetected;
    private bool _checkingConnections;
    private bool _startupTimingRecorded;
    private string _detectedHardwareName = string.Empty;

    private string GetGeneratorModelName()
    {
        if (_generator is null) return "";
        // *IDN? は「メーカー,機種,シリアル番号,バージョン」の応答。
        // 接続時に取得済みの値を使うので、GUI更新で通信は追加しない。
        string[] fields = _generator.Identity.Split(',');
        return fields.Length > 1 && !string.IsNullOrWhiteSpace(fields[1])
            ? fields[1].Trim()
            : "機種名不明";
    }

    private void ConfigureConnectionSelection()
    {
        _connectionModeComboBox.Items.Add(NrConnectionMode.Usb);
        _connectionModeComboBox.Items.Add(NrConnectionMode.Wifi);
        _connectionModeComboBox.SelectedItem = NrConnectionMode.Usb;

        foreach (NrWifiProfile profile in NrWifiProfiles.All)
        {
            _wifiProfileComboBox.Items.Add(profile);
        }

        if (_wifiProfileComboBox.Items.Count > 0)
        {
            _wifiProfileComboBox.SelectedIndex = 0;
        }

        UpdateConnectionSelectionDisplay();
    }

    private NrWifiProfile GetSelectedWifiProfile()
    {
        return _wifiProfileComboBox.SelectedItem as NrWifiProfile
            ?? throw new InvalidOperationException(
                "接続するNR-XのWi-Fiプロファイルを選択してください。");
    }

    // 画面を作る前に LOGGER 本体ソフトの起動だけ先に始める。
    // 種類は ConfigureLoggerSelection の既定と同じ(1 つならそれ、両方入っていれば旧 WAVE LOGGER)。
    // 失敗しても何もしない(接続確認で従来どおり起動し、エラーもそこで出る)。
    private void BeginLoggerStartup()
    {
        try
        {
            IReadOnlyList<WaveLoggerEdition> installed = WaveLoggerSession.GetInstalledEditions();
            if (installed.Count == 0) return;
            WaveLoggerEdition edition = installed.Count == 1 ? installed[0] : WaveLoggerEdition.Legacy;
            _logger.BeginServerStartup(edition);
        }
        catch (Exception ex)
        {
            Trace.WriteLine("LOGGERの事前起動を始められません: " + ex);
        }
    }

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

    private NrConnectionMode GetSelectedConnectionMode()
    {
        return _connectionModeComboBox.SelectedItem is NrConnectionMode mode
            ? mode
            : NrConnectionMode.Usb;
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

    private void ConnectionModeChanged(object? sender, EventArgs e)
    {
        if (GetSelectedConnectionMode() == NrConnectionMode.Wifi &&
            IsWaveLoggerXInstalled())
        {
            _loggerComboBox.SelectedItem = WaveLoggerEdition.WaveLoggerX;
        }

        UpdateConnectionSelectionDisplay();
        UpdateControls();
    }

    private void UpdateConnectionSelectionDisplay()
    {
        bool wifiSelected = GetSelectedConnectionMode() == NrConnectionMode.Wifi;
        _wifiProfileLabel.Visible = wifiSelected;
        _wifiProfileComboBox.Visible = wifiSelected;
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
                _measurementState.MeasurementStarted = false;
                if (!_logger.IsInitialized) ConfigureLoggerSelection();
                // COM参照が残っていても、LOGGER画面が開いているとは限らない。
                // 「確認」では毎回解放し、InitializeとVisible=trueを実行し直す。
                ResetTemplate();
                _measurementState.State = null;
                _measurementState.ReadAfter = 0;
                _measurementState.ErrorLogged = false;
                _templateStateErrorLogged = false;
                if (_logger.IsInitialized || _logger.HasPendingShutdown)
                {
                    _hardwareDetected = false;
                    await _logger.ShutdownAsync(AppendLog);
                    AppendLog("LOGGERの旧接続を解除しました。COM接続を作り直します。");
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
                // 応答がなければ出力OFFを試してから接続を破棄し、続けて接続し直す。
                if (_generator is not null && !TryConfirmGenerator(out string generatorFailure))
                {
                    AppendLog($"発振器：応答を確認できません（{generatorFailure}）。出力OFFを試してから接続し直します。");
                    DiscardGenerator();
                }
                if (_generator is null)
                {
                    try
                    {
                        _generator = FunctionGeneratorConnection.Connect();
                        // 接続時に出力OFF・オフセット0Vを送信している。
                        MarkGeneratorOutputOff("再接続時に出力OFFを送信しました");
                    }
                    catch (Exception ex) { failures.Add("発振器: " + ex.Message); }
                }
                else if (_leakOutputReady || _generatorOutputUnknown)
                {
                    // 応答は回復した。出力OFFを再送し、成功したら出力状態不明を解除する。
                    try { StopLeakOutput(); }
                    catch (Exception ex)
                    {
                        DiscardGenerator();
                        failures.Add("発振器: " + ex.Message);
                    }
                }

                // 初回押下で設定通信を行わないよう、接続確認中は出力OFFのまま波形設定のみ準備する。
                if (_generator is not null)
                {
                    try
                    {
                        _generator.PrepareBurst(CurrentBurstSettings);
                        AppendLog($"発振器設定：{GetGeneratorModelName()} / {_generator.PreparationSummary}");
                    }
                    catch (Exception ex)
                    {
                        DiscardGenerator();
                        failures.Add("発振器設定: " + ex.Message);
                    }
                }
                if (_generatorOutputUnknown)
                    failures.Add("発振器出力: " + GeneratorOutputUnknownMessage("出力OFFを送信できませんでした"));

                wfTimer.Stop();
                AppendLog($"処理時間：発振器接続確認 = {wfTimer.Elapsed.TotalSeconds:F3} 秒");

                var loggerTimer = Stopwatch.StartNew();
                try
                {
                WaveLoggerEdition selectedEdition = GetSelectedLoggerEdition();
                NrConnectionMode connectionMode = GetSelectedConnectionMode();

                if (connectionMode == NrConnectionMode.Wifi &&
                    selectedEdition != WaveLoggerEdition.WaveLoggerX)
                {
                    throw new InvalidOperationException(
                        "NR-XのWi-Fi接続にはWAVE LOGGER Xが必要です。");
                }

                // 起動時に先に始めた LOGGER の起動が終わるまで待つ(UI スレッドは止めない)。
                // Initialize はこの後、起動済みの参照を受け取るだけになる。
                if (!_logger.IsInitialized && _logger.HasPendingServerStartup)
                {
                    var prestartTimer = Stopwatch.StartNew();
                    await _logger.WaitForServerStartupAsync();
                    AppendLog($"処理時間：LOGGER事前起動の完了待ち = {prestartTimer.Elapsed.TotalSeconds:F3} 秒（画面準備と重なった分は含まない）");
                }

                WaveLoggerSession.HardwareCheckResult check;
                if (connectionMode == NrConnectionMode.Wifi)
                {
                    try
                    {
                        NrWifiProfile wifiProfile = GetSelectedWifiProfile();
                        await _nrXConnection.ConnectWifiAsync(wifiProfile.WindowsProfileName);
                        MeasureConnectionStep("LOGGERサーバー起動・初期化", () => _logger.Initialize(selectedEdition, AppendLog));
                        check = MeasureConnectionStep("NR-X本体確認（Wi-Fi）", () => _nrXConnection.DetectWifi(wifiProfile, 0));

                        if (!check.IsDetected)
                        {
                            _logger.Release();
                            await _nrXConnection.RestorePreviousWifiAsync();
                        }
                    }
                    catch
                    {
                        _logger.Release();
                        try
                        {
                            await _nrXConnection.RestorePreviousWifiAsync();
                        }
                        catch
                        {
                            // 元の例外を優先し、復帰ボタンから再試行できる状態を残す。
                        }

                        throw;
                    }
                }
                else
                {
                    check = DetectUsbHardwareWithFallback(selectedEdition);
                }

                _logger.LogSessionState("本体接続確認後・雛形読み込み前", AppendLog);
                _hardwareDetected = check.IsDetected;
                _detectedHardwareName = check.IsDetected
                    ? check.HardwareName
                    : string.Empty;

                if (!_hardwareDetected)
                    throw new InvalidOperationException("NR本体を確認できません / " + check.Description);
                _loggerWindowClosed = false;
                }
                catch (Exception ex)
                {
                    failures.Add("WAVE LOGGER／NR本体: " + ex.Message);
                }

                loggerTimer.Stop();
                AppendLog($"処理時間：LOGGER／NR接続処理全体 = {loggerTimer.Elapsed.TotalSeconds:F3} 秒");

                // Arduino確認の完了まで待ってから、全機器の結果をまとめる。
                var arduinoResult = await arduinoTask;
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
                AppendLog("接続確認：正常終了 / 本体=" + _detectedHardwareName);
                AppendLog("雛形フォルダ：" + TemplateFolderPath);
                // 雛形の所在のみ確認。校正値を適用してから開く。
                if (_modeSelected) GetDetectedTemplatePath();
                RefreshReferenceCandidates();
                return $"正常終了 / 本体={_detectedHardwareName} / {(!_modeSelected ? "モードの選択" : SaveRootReady ? "校正値の入力" : "保存先の選択")}待ち";
            });
        }
        finally
        {
            _checkingConnections = false;
            UpdateControls();
        }
        // モードを先に選んでいたときは、接続確認が済んだここで校正画面を開く。
        OpenCalibrationIfPending();
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

        var nrX = MeasureConnectionStep("NR-X本体確認（USB）", () =>
            _nrXConnection.DetectUsb(0));
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
    }

    // 発振器の出力状態が不明な間は、測定・漏れ試験・合格へ進ませない。
    private bool ConnectionsReady => _arduino.IsConnected && _generator is not null &&
        !_generatorOutputUnknown && _logger.IsInitialized && _hardwareDetected;

    // 一時的な通信失敗で接続を手放さないよう、2回まで応答を確認する。
    private bool TryConfirmGenerator(out string failure)
    {
        failure = string.Empty;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try { _generator!.QueryIdentity(); return true; }
            catch (Exception ex)
            {
                failure = ex.Message;
                AppendLog($"発振器の応答確認 {attempt}回目：失敗 / {ex.Message}");
            }
        }
        return false;
    }

    // 接続を破棄する前に出力OFFを試す。失敗しても破棄は続け、出力状態不明を残す。
    // 破棄後は出力を止める手段がないため、呼び出し側で再接続または作業者へ警告する。
    private void DiscardGenerator()
    {
        var generator = _generator;
        if (generator is null) return;
        try { StopLeakOutput(); }
        catch (Exception ex) { AppendLog("発振器：接続破棄前の出力OFFに失敗 / " + ex.Message); }
        _generator = null;
        try { generator.Dispose(); }
        catch (Exception ex) { AppendLog("発振器：接続の解放に失敗 / " + ex.Message); }
    }

    private bool _loggerWindowClosed;

    private async void RestoreWifiClicked(object? sender, EventArgs e)
    {
        await RunOperationAsync("Wi-Fi復帰", async () =>
        {
            ResetTemplate();
            _hardwareDetected = false;
            _detectedHardwareName = string.Empty;
            // Wi-Fiを切り替える前に、トリガ待ちを含め停止・終了を確認する。
            await _logger.ShutdownAsync(AppendLog);

            await _nrXConnection.RestorePreviousWifiAsync();
            return "元のWi-Fiへ戻しました。";
        });
    }
    private async void StatusHeartbeatTick(object? sender, EventArgs e)
    {
        RefreshExcelJudgment();
        if (!_operationInProgress && !_loggerWindowClosed && _logger.IsInitialized)
        {
            try
            {
                if (_logger.IsSessionClosed())
                {
                    // 終了失敗時は毎秒再試行せず、「確認」で明示的に再試行する。
                    _loggerWindowClosed = true;
                    await RunOperationAsync("LOGGER終了検知", async () =>
                    {
                        _hardwareDetected = false;
                        _detectedHardwareName = string.Empty;
                        ResetTemplate();
                        await _logger.ShutdownAsync(AppendLog);
                        return "旧LOGGERを終了しました。「確認」で再接続してください。";
                    });
                    return;
                }
            }
            catch (Exception ex)
            {
                // 可視状態の取得失敗は、従来の状態監視で詳細を表示する。
                if (!_templateStateErrorLogged)
                {
                    _templateStateErrorLogged = true;
                    AppendLog($"LOGGER画面状態の取得失敗：HRESULT=0x{ex.HResult:X8} / {ex.Message}");
                }
            }
        }
        if (!_operationInProgress && _logger.IsInitialized && _templateLoaded)
        {
            try { RefreshLoggerState(); }
            catch (Exception ex)
            {
                _measurementState.State = null;
                if (!_measurementState.ErrorLogged)
                {
                    _measurementState.ErrorLogged = true;
                    AppendLog("WAVE LOGGER の状態取得に失敗：" + ex.Message);
                }
            }
        }
        if (!_operationInProgress && ConnectionsReady)
        {
            try { RefreshTemplateState(); }
            catch (Exception ex)
            {
                ResetTemplate();
                if (!_templateStateErrorLogged)
                {
                    _templateStateErrorLogged = true;
                    AppendLog("LOGGER の雛形状態を確認できません：" + ex.Message);
                }
            }
        }
        UpdateControls();
        if (_operationInProgress || !ConnectionsReady) return;
        if (!_arduino.NfStatusFresh)
        {
            // 測定は1秒で禁止。ポップアップは3秒以上停止した場合だけ。
            if (_arduino.NfCommunicationAlertDue && !_nfCommunicationAlertShown)
            {
                _nfCommunicationAlertShown = true;
                AppendLog("Arduino の NF弁状態を3秒以上受信できません。測定開始を禁止しています。");
                ErrorNotification.Show(this, "NF弁の通電状態を3秒以上受信できません。Arduino の接続を確認してください。\n受信が復旧したら状態を自動更新します。",
                    "通信エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return;
        }
        if (_nfCommunicationAlertShown)
            AppendLog("Arduino の通電状態の受信が再開しました。");
        _nfCommunicationAlertShown = false;
        try
        {
            if (++_heartbeatCount % 5 == 0 && _generator is not null && !_flow.Measuring)
            {
                // 1回の応答失敗では接続を破棄しない。出力OFFを送れる状態を保ち、
                // 漏れ試験出力中は下のResetTemplateで停止を試す。再確認は「確認」で行う。
                try { _generator.QueryIdentity(); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("発振器の応答がありません / " + ex.Message, ex);
                }
                // ファイルを閉じた直後のGetUnitInfo=-2を本体の切断と扱わない。
                // 雛形が開いているときだけNR本体を確認する。
                if (!_templateLoaded) return;
                long unit = _logger.GetUnitInfo(0);
                bool hardwareAlive = _logger.ActiveEdition == WaveLoggerEdition.Legacy
                    ? unit >= 0
                    : _detectedHardwareName == "NR-500" ? unit == 193 : unit is 198 or 199;
                if (!hardwareAlive)
                    throw new InvalidOperationException($"NR 本体を確認できません（GetUnitInfo={unit}）。");
            }
        }
        catch (Exception ex)
        {
            _hardwareDetected = false;
            ResetTemplate();
            AppendLog("接続確認に失敗しました。「確認」を押してください。：" + ex.Message);
        }
    }

}
