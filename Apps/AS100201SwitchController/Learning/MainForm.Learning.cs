using System.Diagnostics;

namespace PSCAutomation.Apps.AS100201SwitchController;

// 学習モード(SPEC_調整操作の記録.md)の画面への組み込み。通常モードでは _panel / _learning が null で、ここは何もしない。
// タッチパネルの接続・見張り、操作ボタンの処理、測定の行と xdt の保存、書けないときの停止と「再試行」。
internal sealed partial class MainForm : Form
{
    private readonly As100201StartupMode _mode;
    private As100201PanelProtocol? _panel;
    // 本物のタッチパネルの道(USB / Wi-Fi)。開発用の代わり(--virtual-panel)のときは null。
    private As100201PanelPathLink? _panelPaths;
    private As100201PanelPath _lastPanelPath;
    private As100201PanelBattery? _panelBattery;
    private readonly As100201BatteryWatch _batteryWatch = new();
    private As100201LearningSession? _learning;
    private string? _lastPanelStatus;
    private (As100201Switch Press, int Phase)? _lastWaveform;
    private bool _retryDialogOpen;
    private readonly Label _panelValueLabel = new();
    private readonly Label _learningStateLabel = new();
    private readonly Label _saveDirectoryLabel = new();
    private readonly Button _retryButton = new();

    public bool IsLearningMode => _mode == As100201StartupMode.Learning;

    // パネルが 2 画面目以降(操作を選んでいる途中)にいる(版 4、2026-11-13 人の指示)。パネルの SELECT / HOME で変わる。
    private bool _panelSelecting;

    // 学習モードで測定を始めてよいか。パネルが途切れている間、台帳・xdt に書けない間、パネルで操作を選んでいる途中は始めない。
    private bool LearningReady => !IsLearningMode ||
        (_panel is { InputReady: true } && _learning is { Blocked: false } && !_panelSelecting);

    private void InitializeLearningMode()
    {
        if (!IsLearningMode) return;
        _panel = new As100201PanelProtocol(CreatePanelLink());
        _panel.CommandReceived += PanelCommandReceived;
        _panel.Disconnected += PanelDisconnected;
        _panel.StatusChanged += PanelStatusChanged;
        _panel.BatteryReported += PanelBatteryReported;
        _panel.SelectionChanged += PanelSelectionChanged;
        // 保存先は雛形に依存しない(2026-11-10 人の決定)ので、起動時に決まる。フォルダーは最初に書くときに作る。
        string directory = LearningDataDirectory(AppContext.BaseDirectory);
        _learning = new As100201LearningSession(new As100201CsvLedger(Path.Combine(directory, LedgerFileName)), _logger.SaveXdt)
        {
            SaveDirectory = directory
        };
        _learning.Log += AppendLog;
        InitializeLiveCapture();
        AppendLog("学習モードで起動しました。タッチパネルの操作を記録し、測定のたびに xdt と台帳を保存します。");
        AppendLog($"学習モードの保存先：{directory}（台帳 {LedgerFileName}、xdt は日付フォルダー yyyyMMdd\\yyyyMMdd_HHmmss_側.xdt）");
        if (_useVirtualPanel)
            AppendLog("開発用：タッチパネルの代わりを画面に出しています（" + As100201VirtualPanelLink.CommandLineSwitch + "）。COM ポートのタッチパネルは探しません。");
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
            Text = As100201Application.LearningModeLabel + (_useVirtualPanel
                ? "（開発用：タッチパネルの代わり）　xdt と台帳は本番と同じく保存します"
                : "　タッチパネルで調整操作を記録し、測定のたびに xdt と台帳を保存します")
        };
        if (_useVirtualPanel) banner.BackColor = Color.SteelBlue;

        // Dock は後に足したものから配置されるので、帯が最上部、その下に記録の状態、(開発用の代わりのパネル、)残りが今までの画面。
        var padding = new Panel { Dock = DockStyle.Top, Height = 8 };
        Controls.Add(padding);
        if (_useVirtualPanel) Controls.Add(BuildVirtualPanelUi());
        Controls.Add(group);
        Controls.Add(banner);

        _connectionStatusPanel.RowCount = 5;
        AddConnectionStatusRow(_connectionStatusPanel, 4, "タッチパネル：", _panelValueLabel);
    }

    private void UpdateLearningControls()
    {
        if (!IsLearningMode || _panel is null) return;
        var details = new List<string>();
        if (_panelPaths?.ActivePath is As100201PanelPath path and not As100201PanelPath.None)
            details.Add(As100201PanelPathLink.PathName(path));
        if (_panelBattery is not null) details.Add(_panelBattery.DisplayText);
        string suffix = details.Count == 0 ? "" : "（" + string.Join("・", details) + "）";
        if (_panelSelecting && _panel.InputReady) suffix += "　選択中（測定できません）";
        _panelValueLabel.Text = _panel.InputReady ? "タッチパネル　接続済み" + suffix
            : _panel.IsConnected ? "定期応答の確認中" + suffix : "接続待ち（USB / Wi-Fi を自動再確認）";
        _panelValueLabel.ForeColor = _panel.InputReady ? Color.DarkGreen : Color.DarkRed;
        _saveDirectoryLabel.Text = _learning is null ? "未設定" : _learning.SaveDirectory + "　台帳：" + Path.GetFileName(_learning.LedgerLocation) + "　xdt：日付フォルダー";
        _learningStateLabel.Text = _learning?.StateText ?? "記録できません";
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
        return error is null ? null : "タッチパネル: " + error.Message + "（学習モードはタッチパネルがないと動きません。" +
            "Tab5 の電源、USB ケーブル、または PC の Wi-Fi 子機が Tab5 の Wi-Fi につながっているかを確かめてください。通常モードで起動し直せば今までどおり使えます。" +
            "Tab5 が無いまま試すときは、起動の窓で「学習モード（開発用：タッチパネルの代わり…）」を選んでください）";
    }

    // 確認が通った道が前回と違う(最初につながったときも)。両方つながっていれば USB、切れたらもう一方へ移る(2026-11-13 承認)。
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

    // パネルが操作を選び始めた(SELECT)・1 画面目に戻った(HOME)。選んでいる途中は READY LED を消し、測定を始めない。
    private void PanelSelectionChanged(bool selecting) => PostToUi(() => SetPanelSelecting(selecting));

    private void SetPanelSelecting(bool selecting)
    {
        if (_panelSelecting == selecting) return;
        _panelSelecting = selecting;
        AppendLog(selecting
            ? "タッチパネルで操作を選んでいます。選び終わるまで測定を始めません（READY LED を消します）。"
            : "タッチパネルが 1 画面目に戻りました。測定を始められます。");
        RefreshReadyLed();
        UpdateControls();
    }

    private void PanelDisconnected()
    {
        PostToUi(() =>
        {
            _panelBattery = null;
            // パネルの画面がどこにあるか分からなくなる。つなぎ直したらパネルが SELECT / HOME を送り直す。
            _panelSelecting = false;
            AppendLog("タッチパネルの通信が途切れました。自動で再接続を確認します（USB を先に、なければ Wi-Fi）。途切れている間は測定を始められません（sw4 の停止だけ受け付けます）。");
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
                if (command.Kind != As100201PanelCommandKind.Hold)
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
            // 学習モードでは起動時に作るので、ここには来ない(念のため記録せずに断る)。
            AppendLog("保存先が決まっていないため記録できません。");
            _panel.TrySend(As100201PanelProtocol.Nak("記録できません"));
            return;
        }
        try
        {
            if (_measurementStarted && _logger.IsInitialized && _templateLoaded)
            {
                // 測定中にパネルが押されたら止めて、そこまでの波形を測定の行として残す(人の案)。
                // 版 3(2026-11-13、人の指示)からは、操作ボタンを確定したときだけでなく、パネルに触れた時点(HOLD:
                // 1 画面目のボタン・暗い画面を明るくしたとき)・完了・取消でも止める。人が次の行動を決めた所までを
                // 学習データとして切り出すため。保存できず止まっているときも止める(sw4 と同じく測定の行は覚えておく)。
                _logger.Stop();
                _measurementState.ReadAfter = 0;
                AppendLog($"タッチパネル（{command.DisplayName}）のため WAVE LOGGER へ停止を要求しました。");
                LearningMeasurementStopped(As100201LearningSession.StopByPanel);
                RefreshLoggerState();
            }
            string reply = _learning.HandleCommand(command);
            _panel.TrySend(reply);
            // 確定した行(操作・完了・取消)が届いたら、パネルは 1 画面目に戻っている(HOME も届くが、念のため)。
            if (command.Kind != As100201PanelCommandKind.Hold) SetPanelSelecting(false);
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

    // WF1973 の出し直しが終わった(PC_go / CR_go)。学習モードなら「押した」行を書く(2026-11-12)。
    // 波形が実際に変わった時点で書く(処理中に次の押下で上書きされた押下・失敗した押下は書かない)。
    // 測定中なら F8 からの秒と LOGGER の点数(xdt の何点目か)も残す。LOGGER に届くまでの遅れ(平均 0.07 秒)はそのまま。
    private void LearningWaveformRestarted()
    {
        if (_learning is null) return;
        try
        {
            var (side, _, phase) = DescribeCurrentWaveform();
            double? seconds = null;
            long? count = null;
            string? problem = null;
            if (_measurementStarted && _logger.IsInitialized)
            {
                seconds = _capture?.SecondsSinceBegin(Stopwatch.GetTimestamp());
                try { count = _logger.GetDataCount(); }
                catch (Exception ex) { problem = ex.Message; }
            }
            _learning.WaveformRestarted(side, phase, seconds, count, problem);
        }
        catch (Exception ex)
        {
            // 押した行が書けなくても、波形の出し直しは済んでいるので失敗扱いにしない。
            AppendLog("押した行を記録できません：" + ex.Message);
        }
        AfterLearningChange();
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
        // 測定中に取り込んでいたら、残りを取り出して xdt の後に CSV を書く(SPEC_測定中の取り込み.md)。
        var features = FinishLiveCapture(side, phase);
        _learning.MeasurementCompleted(side, code, phase, features);
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
        DisposeLiveCapture();
        if (_panel is null) return;
        _panel.CommandReceived -= PanelCommandReceived;
        _panel.Disconnected -= PanelDisconnected;
        _panel.StatusChanged -= PanelStatusChanged;
        _panel.BatteryReported -= PanelBatteryReported;
        _panel.SelectionChanged -= PanelSelectionChanged;
        if (_panelPaths is not null) _panelPaths.PathChanged -= PanelPathChanged;
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
            readyLedOn: _readyLedOn, out refusal,
            panelSelecting: _panelSelecting);
    }

    // 台帳のファイル名(2026-11-09 人の決定)。
    public const string LedgerFileName = "調整記録.csv";
    // 保存先: exe の隣の「学習データ」(2026-11-10 人の決定。それまでは雛形と同じフォルダー)。
    // 台帳はこの直下に 1 つ、xdt は日付フォルダー(As100201LearningSession.BuildXdtPath)。
    public const string LearningDataFolderName = "学習データ";
    // 学習モードで自動で読み込む雛形の置き場: exe の隣の「雛形」(exe に入れない。人が差し替える)。
    public const string TemplateFolderName = "雛形";

    public static string LearningDataDirectory(string baseDirectory) => Path.Combine(baseDirectory, LearningDataFolderName);

    // 「雛形」フォルダーの xdt がちょうど 1 つならそれを返す。無い・2 つ以上なら null と理由。
    public static string? FindLearningTemplate(string templateFolder, out string reason)
    {
        reason = "";
        if (!Directory.Exists(templateFolder))
        {
            reason = "雛形フォルダーがありません：" + templateFolder;
            return null;
        }
        string[] files = Directory.GetFiles(templateFolder, "*.xdt");
        if (files.Length == 1) return files[0];
        reason = files.Length == 0
            ? "雛形フォルダーに xdt がありません：" + templateFolder
            : $"雛形フォルダーに xdt が {files.Length} つあり、どれを使うか決められません：" + templateFolder;
        return null;
    }

    // 接続確認が通ったあとに、決まった雛形を LOGGER で開く(2026-11-10 人の決定)。
    // 読み込めなくても接続確認は失敗にせず、理由をログに出して雛形待ちのままにする(ドロップすれば使える)。
    private string LoadLearningTemplate()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, TemplateFolderName);
        if (FindLearningTemplate(folder, out string reason) is not string path)
        {
            AppendLog("学習モードの雛形を自動で読み込めません：" + reason + "（雛形をドロップすれば使えます）");
            return "雛形待ち";
        }
        try
        {
            string result = LoadTemplate(path);
            AppendLog("学習モードの雛形を自動で読み込みました：" + Path.GetFileName(path));
            return "雛形読み込み " + result;
        }
        catch (Exception ex)
        {
            AppendLog("学習モードの雛形を自動で読み込めません：" + ex.Message + "（雛形をドロップすれば使えます）");
            return "雛形待ち";
        }
    }
}
