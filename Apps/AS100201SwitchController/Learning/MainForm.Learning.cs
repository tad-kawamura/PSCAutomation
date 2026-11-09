using System.Diagnostics;

namespace PSCAutomation.Apps.AS100201SwitchController;

// 学習モード(SPEC_調整操作の記録.md)の画面への組み込み。通常モードでは _panel / _learning が null で、ここは何もしない。
// タッチパネルの接続・見張り、操作ボタンの処理、測定の行と xdt の保存、書けないときの停止と「再試行」。
internal sealed partial class MainForm : Form
{
    private readonly As100201StartupMode _mode;
    private As100201PanelProtocol? _panel;
    private As100201LearningSession? _learning;
    private string? _lastPanelStatus;
    private (As100201Switch Press, int Phase)? _lastWaveform;
    private bool _retryDialogOpen;
    private readonly Label _panelValueLabel = new();
    private readonly Label _learningStateLabel = new();
    private readonly Label _saveDirectoryLabel = new();
    private readonly Button _retryButton = new();

    public bool IsLearningMode => _mode == As100201StartupMode.Learning;

    // 学習モードで測定を始めてよいか。パネルが途切れている間と、台帳・xdt に書けない間は始めない。
    private bool LearningReady => !IsLearningMode ||
        (_panel is { InputReady: true } && _learning is { Blocked: false });

    private void InitializeLearningMode()
    {
        if (!IsLearningMode) return;
        _panel = new As100201PanelProtocol(new As100201SerialLink());
        _panel.CommandReceived += PanelCommandReceived;
        _panel.Disconnected += PanelDisconnected;
        _panel.StatusChanged += PanelStatusChanged;
        AppendLog("学習モードで起動しました。タッチパネルの操作を記録し、測定のたびに xdt と台帳を保存します。");
    }

    // 画面上部の「学習モード」の帯と、タッチパネルの接続状態・記録の状態・「再試行」。
    private void BuildLearningUi()
    {
        if (!IsLearningMode) return;

        var group = new GroupBox
        {
            Text = As100201Application.LearningModeLabel + "：記録の状態",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Padding = new Padding(10, 6, 10, 8)
        };
        var table = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 3
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var instruction = new Label { AutoSize = true, MaximumSize = new Size(760, 0), Text = As100201Application.LearningInstructions, Margin = new Padding(0, 2, 8, 6) };
        table.Controls.Add(instruction, 0, 0);
        table.SetColumnSpan(instruction, 2);
        table.Controls.Add(new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold), Text = "保存先：", Margin = new Padding(0, 6, 8, 4) }, 0, 1);
        _saveDirectoryLabel.AutoSize = true;
        _saveDirectoryLabel.Margin = new Padding(0, 6, 8, 4);
        table.Controls.Add(_saveDirectoryLabel, 1, 1);
        table.Controls.Add(new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold), Text = "記録：", Margin = new Padding(0, 6, 8, 4) }, 0, 2);
        _learningStateLabel.AutoSize = true;
        _learningStateLabel.Margin = new Padding(0, 6, 8, 4);
        table.Controls.Add(_learningStateLabel, 1, 2);
        _retryButton.AutoSize = true;
        _retryButton.MinimumSize = new Size(110, 36);
        _retryButton.Text = "再試行";
        _retryButton.Margin = new Padding(8, 2, 0, 2);
        _retryButton.Click += (_, _) => RetryLearningWrites();
        table.Controls.Add(_retryButton, 2, 1);
        table.SetRowSpan(_retryButton, 2);
        group.Controls.Add(table);
        // Dock=Top の GroupBox は AutoSize が効かないので、中身の高さに合わせる。
        group.AutoSize = false;
        void FitGroup() => group.Height = table.Height + group.Padding.Vertical + 22;
        table.SizeChanged += (_, _) => FitGroup();
        FitGroup();

        var banner = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.DarkOrange,
            ForeColor = Color.White,
            Font = new Font(Font.FontFamily, 13, FontStyle.Bold),
            Text = As100201Application.LearningModeLabel + "　タッチパネルで調整操作を記録し、測定のたびに xdt と台帳を保存します"
        };

        // Dock は後に足したものから配置されるので、帯が最上部、その下に記録の状態、残りが今までの画面。
        var padding = new Panel { Dock = DockStyle.Top, Height = 8 };
        Controls.Add(padding);
        Controls.Add(group);
        Controls.Add(banner);

        _connectionStatusPanel.RowCount = 5;
        AddConnectionStatusRow(_connectionStatusPanel, 4, "タッチパネル：", _panelValueLabel);
    }

    private void UpdateLearningControls()
    {
        if (!IsLearningMode || _panel is null) return;
        _panelValueLabel.Text = _panel.InputReady ? "タッチパネル　接続済み" : _panel.IsConnected ? "定期応答の確認中" : "接続待ち（自動再確認）";
        _panelValueLabel.ForeColor = _panel.InputReady ? Color.DarkGreen : Color.DarkRed;
        _saveDirectoryLabel.Text = _learning is null ? "未設定（保存先の決め方は相談中）" : _learning.SaveDirectory + "　台帳：" + Path.GetFileName(_learning.LedgerLocation);
        _learningStateLabel.Text = _learning?.StateText ?? "保存先が決まるまで記録できません";
        _learningStateLabel.ForeColor = _learning is { Blocked: false } ? Color.DarkGreen : Color.DarkRed;
        _retryButton.Enabled = _learning is { Blocked: true } && !_operationInProgress;
    }

    // 接続確認。Arduino の確認が終わってから行う(2 台が同時に同じ COM ポートを開かないため)。失敗理由を返す。
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
        return error is null ? null : "タッチパネル: " + error.Message + "（学習モードはタッチパネルがないと動きません。通常モードで起動し直せば今までどおり使えます）";
    }

    private void PanelDisconnected()
    {
        PostToUi(() =>
        {
            AppendLog("タッチパネルの通信が途切れました。自動で再接続を確認します。途切れている間は測定を始められません（sw4 の停止だけ受け付けます）。");
            RefreshReadyLed();
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
        if (_learning is null)
        {
            AppendLog("保存先が決まっていないため記録できません。");
            _panel.TrySend(As100201PanelProtocol.Nak("保存先が未設定です"));
            return;
        }
        try
        {
            if (command.Kind == As100201PanelCommandKind.Operation && !_learning.Blocked &&
                _measurementStarted && _logger.IsInitialized && _templateLoaded)
            {
                // 測定中に操作ボタンが押されたら止めて、そこまでの波形を測定の行として残す(人の案)。
                _logger.Stop();
                _measurementState.ReadAfter = 0;
                AppendLog("操作ボタンのため WAVE LOGGER へ停止を要求しました。");
                LearningMeasurementStopped(As100201LearningSession.StopByPanel);
                RefreshLoggerState();
            }
            string reply = _learning.HandleCommand(command);
            _panel.TrySend(reply);
        }
        catch (Exception ex)
        {
            AppendLog($"タッチパネル {command.DisplayName} の処理に失敗：{ex.Message}");
            _panel.TrySend(As100201PanelProtocol.Nak("処理に失敗しました"));
            ErrorNotification.Show(this, ex.Message, $"{command.DisplayName} 実行エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        AfterLearningChange();
    }

    // 直前に WF1973 へ送った側と位相(測定の行に書く)。出力は次の押下まで続くので、最後の押下をそのまま使う。
    private (string Side, string SideCode, string Phase) DescribeCurrentWaveform()
    {
        if (_lastWaveform is not (As100201Switch press, int phase)) return ("", "", "");
        string side = press.Button == "PC_go" ? "PC" : "CR";
        bool isDouble = press.Kind == As100201PressKind.Double;
        return (isDouble ? side + "（2回押し）" : side, isDouble ? side + "2" : side, phase.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // LOGGER を止めた(sw4 / 操作ボタン)。学習モードなら測定の行を書く。
    private void LearningMeasurementStopped(string trigger)
    {
        if (_learning is null) return;
        var (side, code, phase) = DescribeCurrentWaveform();
        _learning.MeasurementStopped(side, code, phase, trigger);
        AfterLearningChange();
    }

    // LOGGER の状態が停止に戻った。学習モードなら xdt を保存する(止めていなければ行も)。
    private void LearningMeasurementCompleted()
    {
        if (_learning is null) return;
        var (side, code, phase) = DescribeCurrentWaveform();
        _learning.MeasurementCompleted(side, code, phase);
        AfterLearningChange();
    }

    private void AfterLearningChange()
    {
        if (_closing || IsDisposed) return;
        RefreshReadyLed();
        UpdateControls();
        if (_learning is { Blocked: true } && !_retryDialogOpen && IsHandleCreated)
            BeginInvoke(new Action(ShowRetryDialog));
    }

    // 最前面のメッセージボックス(「再試行」付き)。保存できるまで止める。
    // 「キャンセル」で閉じても止まったままで、画面の「再試行」ボタンでやり直せる。
    private void ShowRetryDialog()
    {
        if (_retryDialogOpen || _learning is not { Blocked: true } || _closing || IsDisposed) return;
        _retryDialogOpen = true;
        try
        {
            while (_learning.Blocked && !_closing && !IsDisposed)
            {
                var result = ErrorNotification.Show(this,
                    _learning.BlockReason + "\r\n\r\n保存先：" + _learning.SaveDirectory + "\r\n台帳：" + _learning.LedgerLocation +
                    "\r\n\r\n保存できるまで READY LED を消し、PC_go / CR_go / F8 と操作ボタンを受け付けません（sw4 の停止だけ受け付けます）。" +
                    "\r\n保存先を直してから「再試行」を押してください。「キャンセル」で閉じても、画面の「再試行」ボタンでやり直せます。" +
                    "\r\n保存先を直せないときはアプリを終了し、通常モードで起動し直してください（記録なしで作業を続けます）。",
                    "学習モード：保存できません", MessageBoxButtons.RetryCancel, MessageBoxIcon.Error);
                if (result != DialogResult.Retry) break;
                RetryLearningWrites();
            }
        }
        finally { _retryDialogOpen = false; }
    }

    private void RetryLearningWrites()
    {
        if (_learning is null) return;
        bool ok = _learning.Retry();
        AppendLog(ok ? "再試行：保存できました。記録を再開します。" : "再試行：まだ保存できません。");
        RefreshReadyLed();
        UpdateControls();
    }

    private void DisposeLearningMode()
    {
        if (_panel is null) return;
        _panel.CommandReceived -= PanelCommandReceived;
        _panel.Disconnected -= PanelDisconnected;
        _panel.StatusChanged -= PanelStatusChanged;
        _panel.Dispose();
    }

    // スイッチを実行してよいか。通常モードは今までと同じ条件・同じ文言。学習モードの決まりは As100201LearningGate。
    private bool CanExecuteSwitch(As100201Switch pressed, out string refusal)
    {
        if (!IsLearningMode)
        {
            refusal = As100201LearningGate.NotReady;
            return _readyLedOn && ConnectionsReady && !_operationInProgress;
        }
        return As100201LearningGate.Allows(pressed.Button,
            baseReady: _templateLoaded && ConnectionsReady && !_operationInProgress,
            panelReady: _panel is { InputReady: true },
            hasSession: _learning is not null,
            saveBlocked: _learning is { Blocked: true },
            readyLedOn: _readyLedOn, out refusal);
    }

    // 台帳のファイル名(仮。保存先と一緒に「いずれ決める」)。
    public const string LedgerFileName = "調整記録.csv";

    // 保存先が決まったら呼ぶ。台帳は保存先の中の 1 つの CSV、xdt も同じフォルダー。
    private void CreateLearningSession(string directory)
    {
        _learning = new As100201LearningSession(new As100201CsvLedger(Path.Combine(directory, LedgerFileName)), _logger.SaveXdt)
        {
            SaveDirectory = directory
        };
        _learning.Log += AppendLog;
        AppendLog($"学習モードの保存先：{directory}（台帳 {LedgerFileName}、xdt は同じフォルダー）");
        UpdateControls();
    }
}
