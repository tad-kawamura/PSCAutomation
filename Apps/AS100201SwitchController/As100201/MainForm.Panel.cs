using System.Diagnostics;

namespace PSCAutomation.Apps.AS100201SwitchController;

// タッチパネル(M5Stack Tab5)の接続・見張りと、発振器・測定のボタン(版 6、2026-11-27 人の承認。SPEC_調整操作の記録.md「版 6 の変更」)。
// 版 6 で Arduino のスイッチ箱をなくし、通常モードでも Tab5 を使う。
//   - PC / CR(PHASE:): 位相を出し直し、止まっていれば測り始める。学習モードの 1 画面目で測っている間に押したら、
//     止めて保存してから新しく測り始める(xdt 1 本に PC か CR の片方だけが入る)。ノズルの画面と通常モードでは区切らない
//   - バルブを動かす(VALVE): 測っていれば止めて保存する(パネルは操作の画面へ移る)
//   - 測り直す(REMEASURE): 保存して、同じ位相ですぐ測り直す(ノズルの画面。線が重なって見にくくなったとき)
//   - 測定停止(STOP): 通常モードの 1 画面目。PC の画面の「測定停止」も同じ(Tab5 が切れたとき用)
// 止めてから測り始めるまでは LOGGER が止まりきるのを待つので数秒かかる。その間はパネルの処理中(PanelBusy)として、
// Tab5 のボタンと PC の画面のボタン(測定停止を除く)を押せなくする。返事(ACK)は受け付けた時点で返し、測っている位相は STATE: で知らせる。
internal sealed partial class MainForm : Form
{
    private As100201PanelScreen _panelScreen = As100201PanelScreen.Home;
    // パネルが操作を選んでいる途中(操作の画面・減磁・スペーサー・取消の確認)。選び終わるまで測定を始めない(版 4)。
    private bool _panelSelecting => _panelScreen == As100201PanelScreen.Selecting;
    private bool _panelSequenceBusy;
    private CancellationTokenSource? _panelSequenceCancel;
    private string? _lastPanelStateLine;
    // LOGGER が止まりきるのを待つ上限(AI の仮決め)。データの読み出しが長くても、ふつうは数秒で止まる。
    private static readonly TimeSpan StopWaitLimit = TimeSpan.FromSeconds(20);

    // PC / CR で区切って測り直すなど、パネルのボタンの処理をしている(その間は測定停止のほかは押せない)。
    private bool PanelBusy => _panelSequenceBusy;

    private void InitializePanel()
    {
        _panel = new As100201PanelProtocol(CreatePanelLink());
        _panel.CommandReceived += PanelCommandReceived;
        _panel.Disconnected += PanelDisconnected;
        _panel.StatusChanged += PanelStatusChanged;
        _panel.BatteryReported += PanelBatteryReported;
        _panel.ScreenChanged += PanelScreenChanged;
    }

    private void UpdatePanelControls()
    {
        if (_panel is null)
        {
            _panelValueLabel.Text = "未設定";
            _panelValueLabel.ForeColor = Color.DarkRed;
            return;
        }
        var details = new List<string>();
        if (_panelPaths?.ActivePath is As100201PanelPath path and not As100201PanelPath.None)
            details.Add(As100201PanelPathLink.PathName(path));
        if (_panelBattery is not null) details.Add(_panelBattery.DisplayText);
        string suffix = details.Count == 0 ? "" : "（" + string.Join("・", details) + "）";
        if (_panel.InputReady)
        {
            if (_panelSelecting) suffix += "　選択中（測定できません）";
            else if (_panelScreen is As100201PanelScreen.NozzleP or As100201PanelScreen.NozzleR)
                suffix += $"　ノズル {(_panelScreen == As100201PanelScreen.NozzleP ? 'P' : 'R')} 側を合わせています";
            if (PanelBusy) suffix += "　処理中";
        }
        _panelValueLabel.Text = _panel.InputReady ? "タッチパネル　接続済み" + suffix
            : _panel.IsConnected ? "定期応答の確認中" + suffix : "接続待ち（USB / Wi-Fi を自動再確認）";
        _panelValueLabel.ForeColor = _panel.InputReady ? Color.DarkGreen : Color.DarkRed;
    }

    // 発振器のボタンの名前(PC1 など)。今までのスイッチ名の 1 回押し・2 回押しに当たる。
    private static string PanelButtonCode(As100201Switch press) =>
        (press.Button == "PC_go" ? "PC" : "CR") + (press.Kind == As100201PressKind.Double ? "2" : "1");

    private static string PanelButtonName(As100201Switch press) => As100201PanelCommand.PhaseDisplayName(PanelButtonCode(press));

    // 今の状態を Tab5 へ知らせる(STATE:)。変わったときと、パネルが画面の位置を送ってくるたび(force)。
    private void SendPanelState(bool force = false)
    {
        if (_panel is null || _closing || IsDisposed) return;
        // 測っているが位相を出し直していない(PC の「測定開始（確認用）」だけで始めた)ときは M(版 7。Tab5 の測定停止を押せるように)。
        string? measuring = !_measurementStarted ? null : _lastWaveform is (As100201Switch press, _) ? PanelButtonCode(press) : "M";
        bool busy = PanelBusy || _operationInProgress || _checkingConnections;
        string line = As100201PanelProtocol.State(IsLearningMode, measuring, busy, _switchSettings);
        if (!force && line == _lastPanelStateLine) return;
        if (_panel.TrySend(line)) _lastPanelStateLine = line;
    }

    // 直前の記録(取消の対象)をパネルへ知らせる(版 5、2026-11-26。学習モードだけ)。返事は求めない。
    // 測っている最中なら、取消は止めた測定を「使わない」にするので、今の測定を対象として出す(版 6。触れても止まらなくなったため)。
    private void SendLastRecord()
    {
        if (_panel is null || _learning is null || _closing) return;
        if (_measurementStarted && _learning is { Blocked: false, SerialNumber: not null })
        {
            var (side, _, _) = DescribeCurrentWaveform();
            _panel.TrySend(As100201PanelProtocol.LastPrefix + "M:今の測定 " + (side.Length == 0 ? "側なし" : side) + "（止めて使わない）");
            return;
        }
        _panel.TrySend(_learning.LastLine());
    }

    // 接続確認(版 6 からは Arduino を探さないので、ほかの確認と重ならない)。失敗理由を返す。
    private async Task<string?> VerifyPanelAsync()
    {
        if (_panel is null) return null;
        var details = new List<string>();
        var timer = Stopwatch.StartNew();
        Exception? error = null;
        try { await Task.Run(() => _panel.VerifyAsync(details.Add)); }
        catch (Exception ex) { error = ex; }
        timer.Stop();
        foreach (string detail in details) AppendLog(detail.Replace("Arduino内訳", "タッチパネル内訳"));
        AppendLog($"処理時間：タッチパネル接続確認 = {timer.Elapsed.TotalSeconds:F3} 秒" + (error is null ? "（成功）" : "（失敗）"));
        return error is null ? null : "タッチパネル: " + error.Message + "（タッチパネルがないと測定できません。" +
            "Tab5 の電源、USB ケーブル、または PC の Wi-Fi 子機が Tab5 の Wi-Fi につながっているかを確かめてください。" +
            "Tab5 が無いまま試すときは、引数 " + As100201VirtualPanelLink.CommandLineSwitch + " で起動してください。開発用）";
    }

    // 確認が通った道が前回と違う(最初につながったときも)。両方つながっていれば USB、切れたらもう一方へ移る(2026-11-13 承認)。
    // Wi-Fi の子機をつなぎ直した・つなぎ直せなかった(2026-11-17)。
    private void PanelWifiNotice(string message) => PostToUi(() => AppendLog(message));

    // 使っていた USB が切れたら、Wi-Fi の失敗を待たずに子機をつなぎ直す(2026-11-17 人の決定 C)。
    // アプリが USB を開くと Tab5 が再起動し、子機は古いつながりのまま残っていることがほぼ確実なため。
    private void PanelPathLost(As100201PanelPath path)
    {
        if (path == As100201PanelPath.Usb) _panelWifi?.RefreshNow("使っていた USB が切れたため");
    }

    // Wi-Fi で使っている最中に Tab5 の USB が挿されたら、空いているときに USB へ戻す(2026-11-18 人の決定)。
    // 決まりは As100201UsbReturnWatch。見張りの周期(0.5 秒)ごとに呼ぶ。
    // 空いている = 測っていない・1 画面目(ノズルの画面も含めて、ほかの画面では戻さない。USB を開くと Tab5 が再起動して 1 画面目に戻るため)。
    private async Task ReturnPanelToUsbIfIdleAsync()
    {
        if (_panel is null || _panelPaths is null || _closing) return;
        As100201PanelPath path = _panelPaths.ActivePath;
        As100201UsbState usb = path == As100201PanelPath.Wifi
            ? await Task.Run(As100201SerialLink.PanelUsbState)
            : As100201UsbState.Unknown;
        if (_closing || IsDisposed) return;
        bool idle = _panel.InputReady && !_measurementStarted && _panelScreen == As100201PanelScreen.Home &&
            !_operationInProgress && !_waveformRestartInProgress && !PanelBusy;
        if (!_usbReturnWatch.Update(path, usb, idle, Environment.TickCount64)) return;
        // 選び直しは数秒かかるので待たない(見張りの周期を止めないため)。見張り(MaintainAsync)とは同じ鍵で順番になる。
        _ = ReselectPanelForUsbAsync();
    }

    private async Task ReselectPanelForUsbAsync()
    {
        if (_panel is null || _panelPaths is null) return;
        AppendLog("タッチパネルの USB が挿されたので、USB に戻します（数秒、パネルの操作と測定の開始を受け付けません）。");
        // 選び直す間はパネルの画面がどこにあるか分からなくなる。つながったらパネルが画面の位置を送り直す。
        _panelBattery = null;
        _panelScreen = As100201PanelScreen.Home;
        RefreshReadiness();
        UpdateControls();
        bool connected = await _panel.ReselectAsync();
        if (_closing || IsDisposed) return;
        As100201PanelPath now = _panelPaths.ActivePath;
        if (!connected)
            AppendLog("タッチパネルを選び直せませんでした。自動で再接続を確認します（USB を先に、なければ Wi-Fi）。");
        else if (now == As100201PanelPath.Wifi)
            AppendLog("タッチパネルの USB では確認できず、Wi-Fi のままです。USB に戻すには、USB を挿し直すか「確認」を押してください。");
        RefreshReadiness();
        UpdateControls();
    }

    private void PanelPathChanged(As100201PanelPath path)
    {
        PostToUi(() =>
        {
            string name = As100201PanelPathLink.PathName(path);
            AppendLog(_lastPanelPath == As100201PanelPath.None
                ? $"タッチパネルは {name} でつながっています。"
                : $"タッチパネルの道を {As100201PanelPathLink.PathName(_lastPanelPath)} から {name} に切り替えました。");
            _lastPanelPath = path;
            UpdateControls();
        });
    }

    // 電池残量(30 秒ごと)。少なくなったら 1 回だけログに出す(2026-11-13 承認)。
    private void PanelBatteryReported(As100201PanelBattery battery)
    {
        PostToUi(() =>
        {
            _panelBattery = battery;
            if (_batteryWatch.Update(battery) is string warning) AppendLog(warning);
            UpdateControls();
        });
    }

    // パネルの画面の位置(HOME / SELECT / NOZZLE:)。1 秒ごとと確認の直後に届くので、そのたびに直前の記録(LAST:)と
    // 今の状態(STATE:)も送り直す(つなぎ直し・パネルの再起動のあとも、パネルの表示が PC と揃うように)。
    private void PanelScreenChanged(As100201PanelScreen screen) => PostToUi(() =>
    {
        SetPanelScreen(screen);
        SendLastRecord();
        SendPanelState(force: true);
    });

    private void SetPanelScreen(As100201PanelScreen screen)
    {
        if (_panelScreen == screen) return;
        _panelScreen = screen;
        AppendLog(screen switch
        {
            As100201PanelScreen.Selecting => "タッチパネルで操作を選んでいます。選び終わるまで測定を始めません。",
            As100201PanelScreen.NozzleP => "タッチパネル：ノズル P 側の立ち上がりを合わせる画面です（測っている間に PC を押しても区切りません）。",
            As100201PanelScreen.NozzleR => "タッチパネル：ノズル R 側の立ち上がりを合わせる画面です（測っている間に CR を押しても区切りません）。",
            _ => "タッチパネルが 1 画面目に戻りました。測定を始められます。"
        });
        RefreshReadiness();
        UpdateControls();
    }

    private void PanelDisconnected()
    {
        PostToUi(() =>
        {
            _panelBattery = null;
            // パネルの画面がどこにあるか分からなくなる。つなぎ直したらパネルが画面の位置を送り直す。
            _panelScreen = As100201PanelScreen.Home;
            _lastPanelStateLine = null;
            AppendLog("タッチパネルの通信が途切れました。自動で再接続を確認します（USB を先に、なければ Wi-Fi）。" +
                "途切れている間は測定を始められません（測っていれば続きます。止めるときは PC の画面の「測定停止」）。");
            RefreshReadiness();
            UpdateControls();
        });
    }

    private void PanelStatusChanged(string message)
    {
        PostToUi(() =>
        {
            if (_lastPanelStatus == message) return;
            _lastPanelStatus = message;
            AppendLog(message);
        });
    }

    private void PanelCommandReceived(As100201PanelCommand command)
    {
        long receivedAt = Stopwatch.GetTimestamp();
        long version = _panel!.RequestVersion;
        PostToUi(() =>
        {
            if (_panel is null) return;
            if (version != _panel.RequestVersion || !_panel.InputReady ||
                Stopwatch.GetElapsedTime(receivedAt).TotalSeconds >= 1)
            {
                ArduinoDiagnosticLog.Write("PANEL_COMMAND_DROPPED", "遅延または復旧前の操作を破棄 / " + command.Line);
                _panel.TrySend(As100201PanelProtocol.Nak("接続の確認中です。押し直してください"));
                return;
            }
            ExecutePanelCommand(command);
        });
    }

    private void ExecutePanelCommand(As100201PanelCommand command)
    {
        if (_panel is null) return;
        using var observation = PcDelayObservation.Begin("UI.Panel:" + command.Line);
        AppendLog($"タッチパネル {command.DisplayName} を受信");
        if (PanelBusy || _operationInProgress)
        {
            AppendLog($"処理中のため、{command.DisplayName} は受け付けません。");
            _panel.TrySend(As100201PanelProtocol.Nak("処理中です。終わってから押し直してください"));
            return;
        }
        switch (command.Kind)
        {
            case As100201PanelCommandKind.Phase:
                HandlePanelPhase(command);
                break;
            case As100201PanelCommandKind.Remeasure:
                HandlePanelRemeasure(command);
                break;
            case As100201PanelCommandKind.Valve:
                // 番号が入っていない間は断る(Tab5 は 1 画面目に戻り、理由を出す。2026-11-27 人の指摘)。
                if (IsLearningMode && _learning?.ValveRefusal(_measurementStarted) is string valveRefusal)
                {
                    AppendLog($"{command.DisplayName}：シリアル番号が入っていないため受け付けません（PC の画面で入れてください）。");
                    _panel.TrySend(As100201PanelProtocol.Nak(valveRefusal));
                    break;
                }
                _panel.TrySend(As100201PanelProtocol.Ack(command));
                StopForPanel(command, As100201LearningSession.StopByValve);
                if (IsLearningMode) AfterLearningChange();
                break;
            case As100201PanelCommandKind.Stop:
                _panel.TrySend(As100201PanelProtocol.Ack(command));
                // 版 7 から学習モードの 1 画面目にもある(止めて保存し、1 画面目のまま。停止のきっかけ「Tab5 の測定停止」)。
                StopMeasurement(As100201LearningSession.StopByPanelStop, "タッチパネルの測定停止");
                if (IsLearningMode) AfterLearningChange();
                ShowLoggerForPanel("タッチパネルの測定停止");
                break;
            default:
                if (!IsLearningMode)
                {
                    // 通常モードの Tab5 には測定のボタンしか出ない(版 6)。念のため記録せずに断る。
                    AppendLog($"通常モードでは {command.DisplayName} を記録しません。");
                    _panel.TrySend(As100201PanelProtocol.Nak("通常モードでは記録しません"));
                    return;
                }
                ExecuteLearningCommand(command);
                break;
        }
        SendPanelState();
        UpdateControls();
    }

    // 測っていれば止めて、そこまでの波形を測定の行にする(xdt は LOGGER が停止に戻ったときに保存する)。
    private void StopForPanel(As100201PanelCommand command, string trigger)
    {
        if (!_measurementStarted || !_logger.IsInitialized || !_templateLoaded) return;
        // 保存できず止まっているときも止める(測定の行は覚えておく)。
        _logger.Stop();
        _measurementState.ReadAfter = 0;
        AppendLog($"タッチパネル（{command.DisplayName}）のため WAVE LOGGER へ停止を要求しました。");
        LearningMeasurementStopped(trigger);
        RefreshLoggerState();
        ShowLoggerForPanel($"タッチパネル（{command.DisplayName}）");
    }

    // PC / CR(版 6)。ノズルの画面では、合わせている側でないボタンは押し間違いなので受け付けない(2026-11-27 人の決定)。
    private void HandlePanelPhase(As100201PanelCommand command)
    {
        As100201Switch press = command.ToSwitch();
        string name = PanelButtonName(press);
        if ((_panelScreen == As100201PanelScreen.NozzleP && press.Button == "CR_go") ||
            (_panelScreen == As100201PanelScreen.NozzleR && press.Button == "PC_go"))
        {
            char side = _panelScreen == As100201PanelScreen.NozzleP ? 'P' : 'R';
            AppendLog($"ノズル {side} 側を合わせている間は {name} を受け付けません（押し間違い）。");
            _panel!.TrySend(As100201PanelProtocol.Nak($"{side} 側を合わせている間は {name[..2]} を押せません"));
            return;
        }
        bool measuring = _measurementStarted;
        // 測ったまま位相だけ出し直す: 通常モードと、ノズルの立ち上がりを合わせている間(区切らない。2026-11-27 人の決定)。
        bool keepMeasuring = measuring && (!IsLearningMode ||
            _panelScreen is As100201PanelScreen.NozzleP or As100201PanelScreen.NozzleR);
        string refusal;
        if (keepMeasuring ? !CanRestartWhileMeasuring(out refusal) : !CanExecuteSwitch(press, out refusal))
        {
            AppendLog($"{name}：{refusal}");
            _panel!.TrySend(As100201PanelProtocol.Nak(refusal));
            return;
        }
        _panel!.TrySend(As100201PanelProtocol.Ack(command));
        _ = RunPanelSequenceAsync(name, async cancel =>
        {
            // 学習モードの 1 画面目で測っている間は、止めて保存してから新しく測る(xdt 1 本に PC か CR の片方。2026-11-27 人の決定)。
            if (measuring && !keepMeasuring && !await StopAndWaitAsync(As100201LearningSession.StopByPhase, name, cancel)) return;
            if (!await RestartWaveformAsync(press, cancel)) return;
            if (!keepMeasuring) StartFromPanel(name);
        });
    }

    // 測り直す(版 6、ノズルの画面)。保存して、同じ位相ですぐ測り始める(WF1973 は出したまま)。
    private void HandlePanelRemeasure(As100201PanelCommand command)
    {
        if (!_measurementStarted)
        {
            AppendLog("測り直す：測定していません。");
            _panel!.TrySend(As100201PanelProtocol.Nak("測定していません"));
            return;
        }
        _panel!.TrySend(As100201PanelProtocol.Ack(command));
        _ = RunPanelSequenceAsync("測り直す", async cancel =>
        {
            if (!await StopAndWaitAsync(As100201LearningSession.StopByRemeasure, "測り直す", cancel)) return;
            StartFromPanel("測り直す");
        });
    }

    // 測ったまま位相を出し直してよいか(発振器と LOGGER がつながっている)。
    private bool CanRestartWhileMeasuring(out string refusal)
    {
        refusal = As100201LearningGate.NotReady;
        return ConnectionsReady && _templateLoaded && !_operationInProgress;
    }

    // パネルのボタンの処理(数秒かかる)。その間は PanelBusy にし、測定停止のほかは押せなくする。
    // PC の画面の「測定停止」で止められる(cancel)。失敗したらログと Tab5 の下の帯に出す。
    private async Task RunPanelSequenceAsync(string name, Func<CancellationToken, Task> body)
    {
        using var cancel = new CancellationTokenSource();
        _panelSequenceCancel = cancel;
        _panelSequenceBusy = true;
        SendPanelState();
        UpdateControls();
        try { await body(cancel.Token); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            AppendLog($"{name}：途中で止めました。");
        }
        catch (Exception ex)
        {
            AppendLog($"{name} の処理に失敗：{ex.Message}");
            _panel?.TrySend(As100201PanelProtocol.Message('E', $"{name} の処理に失敗しました：{ex.Message}"));
            if (!_closing && !IsDisposed)
                ErrorNotification.Show(this, ex.Message, $"{name} 実行エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _panelSequenceBusy = false;
            if (ReferenceEquals(_panelSequenceCancel, cancel)) _panelSequenceCancel = null;
            if (!_closing && !IsDisposed)
            {
                SendPanelState();
                RefreshReadiness();
                UpdateControls();
                // 位相の出し直し・測り始め・測り直しで LOGGER の画面が変わるので、毎回前に出す(2026-11-27 人の指示)。
                ShowLoggerForPanel(name);
            }
        }
    }

    // 止めて、LOGGER が停止に戻るまで待つ(戻った時点で xdt を保存する)。止まりきらなければ false。
    private async Task<bool> StopAndWaitAsync(string trigger, string name, CancellationToken cancel)
    {
        _logger.Stop();
        _measurementState.ReadAfter = 0;
        AppendLog($"{name}：WAVE LOGGER へ停止を要求しました（保存してから測り直します）。");
        LearningMeasurementStopped(trigger);
        var timer = Stopwatch.StartNew();
        while (true)
        {
            RefreshLoggerState();
            if (!_measurementStarted) return true;
            if (timer.Elapsed >= StopWaitLimit)
            {
                string message = $"WAVE LOGGER が {StopWaitLimit.TotalSeconds:F0} 秒たっても止まりきらないため、新しい測定を始めませんでした（{_measurementState.GetDisplayText(describeHardwareStop: false)}）。";
                AppendLog($"{name}：{message}");
                _panel?.TrySend(As100201PanelProtocol.Message('E', "測り始められませんでした：LOGGER が止まりきりません"));
                return false;
            }
            await Task.Delay(100, cancel);
        }
    }

    // パネルのボタンから測り始める(F8 と同じ決まりで確かめる)。始められなければ理由をログと Tab5 に出す。
    private void StartFromPanel(string name)
    {
        if (_closing || IsDisposed) return;
        RefreshReadiness();
        if (!CanExecuteSwitch(As100201Switch.Parse("F8"), out string refusal))
        {
            AppendLog($"{name}：測定を始めません：{refusal}");
            _panel?.TrySend(As100201PanelProtocol.Message('E', "測り始められませんでした：" + refusal));
            return;
        }
        RefreshTemplateState();
        if (!_templateLoaded) return;
        RefreshLoggerState();
        if (!_measurementState.ReadyToStart)
        {
            string state = _measurementState.GetDisplayText(describeHardwareStop: false);
            AppendLog($"{name}：WAVE LOGGER は{state}のため、測定を始めません。");
            _panel?.TrySend(As100201PanelProtocol.Message('E', $"測り始められませんでした：LOGGER が{state}です"));
            return;
        }
        StartMeasurement();
    }
}
