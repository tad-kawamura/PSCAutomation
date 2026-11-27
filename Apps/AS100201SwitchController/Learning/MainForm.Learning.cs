using System.Diagnostics;

namespace PSCAutomation.Apps.AS100201SwitchController;

// 学習モード(SPEC_調整操作の記録.md)の画面への組み込み。通常モードでは _learning が null で、ここは何もしない。
// 操作ボタンの処理、測定の行と xdt の保存、書けないときの停止と「再試行」。
// タッチパネルの接続・見張りと発振器・測定のボタンは、版 6 から通常モードでも使うので MainForm.Panel.cs。
internal sealed partial class MainForm : Form
{
    private readonly As100201StartupMode _mode;
    private As100201PanelProtocol? _panel;
    // 本物のタッチパネルの道(USB / Wi-Fi)。開発用の代わり(--virtual-panel)のときは null。
    private As100201PanelPathLink? _panelPaths;
    private As100201WifiRefreshLink? _panelWifi;
    private As100201PanelPath _lastPanelPath;
    private readonly As100201UsbReturnWatch _usbReturnWatch = new();
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
    private readonly TextBox _serialTextBox = new();
    private readonly Button _serialButton = new();
    private readonly Label _serialValueLabel = new();
    private readonly Label _serialHintLabel = new();
    private Label? _learningBanner;
    // シリアル番号の確認の窓を出している(2026-11-26)。出している間は測定を始めず、パネルの操作も受け付けない。
    private bool _serialDialogOpen;

    public bool IsLearningMode => _mode == As100201StartupMode.Learning;

    // 学習モードで測定を始めてよいか。パネルが途切れている間、台帳・xdt に書けない間、パネルで操作を選んでいる途中、
    // シリアル番号が入っていない間・番号の確認の窓を出している間、参照波形を読み込めていない間(2026-11-26)は始めない。
    private bool LearningReady => !IsLearningMode ||
        (PanelReady && _learning is { Blocked: false, SerialNumber: not null } && !_panelSelecting && !_serialDialogOpen &&
         _referenceLoaded);

    private void InitializeLearningMode()
    {
        if (!IsLearningMode) return;
        // 保存先は雛形に依存しない(2026-11-10 人の決定)ので、起動時に決まる。フォルダーは最初に書くときに作る。
        string directory = LearningDataDirectory(AppContext.BaseDirectory);
        _learning = new As100201LearningSession(new As100201CsvLedger(Path.Combine(directory, LedgerFileName)), _logger.SaveXdt)
        {
            SaveDirectory = directory
        };
        _learning.Log += AppendLog;
        InitializeLiveCapture();
        AppendLog("学習モードで起動しました。タッチパネルの操作を記録し、測定のたびに xdt と台帳を保存します。");
        AppendLog($"学習モードの保存先：{directory}（台帳 {LedgerFileName}、xdt はシリアル番号のフォルダー SN145\\yyyyMMdd_HHmmss_SN145_側.xdt）");
        // 台帳の列の形が今と違う(番号の列を足す前の台帳)・読めないときは、書き足さずに止める(2026-11-26)。
        if (_learning.CheckLedger())
            AppendLog("シリアル番号を画面の「シリアル番号」に入れて「決定」を押してください。入れるまで測定とパネルの操作を受け付けません。");
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
        // Dock は Top(2026-11-26)。Fill だと表の高さが枠の高さに縛られ、最初の高さ(100)のまま枠も伸びず、
        // 保存先・記録・シリアル番号の行が枠の外に切れて見えなかった(人の画面で、番号を入れる欄が出ていなかった)。
        var table = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            ColumnCount = 3,
            RowCount = 4
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        // 上の説明文(雛形の自動読み込み・操作ボタン・保存のしかた)は、人の指示でなくした(2026-11-26)。
        table.Controls.Add(new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold), Text = "保存先：", Margin = new Padding(0, 6, 8, 4) }, 0, 0);
        _saveDirectoryLabel.AutoSize = true;
        _saveDirectoryLabel.Margin = new Padding(0, 6, 8, 4);
        table.Controls.Add(_saveDirectoryLabel, 1, 0);
        table.Controls.Add(new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold), Text = "記録：", Margin = new Padding(0, 6, 8, 4) }, 0, 1);
        _learningStateLabel.AutoSize = true;
        _learningStateLabel.Margin = new Padding(0, 6, 8, 4);
        table.Controls.Add(_learningStateLabel, 1, 1);
        _retryButton.AutoSize = true;
        _retryButton.MinimumSize = new Size(110, 36);
        _retryButton.Text = "再試行";
        _retryButton.Margin = new Padding(8, 2, 0, 2);
        _retryButton.Click += (_, _) => RetryLearningWrites();
        table.Controls.Add(_retryButton, 2, 0);
        table.SetRowSpan(_retryButton, 2);
        // シリアル番号(2026-11-26 人の決定): PC の画面に数字を入れて「決定」(Enter でも決定)。
        table.Controls.Add(new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold), Text = "シリアル番号：", Margin = new Padding(0, 10, 8, 4) }, 0, 2);
        var serialRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        serialRow.Controls.Add(new Label { AutoSize = true, Text = "SN", Font = new Font(Font.FontFamily, 12, FontStyle.Bold), Margin = new Padding(0, 8, 2, 0) });
        _serialTextBox.Width = 110;
        _serialTextBox.MaxLength = 8;
        _serialTextBox.Font = new Font(Font.FontFamily, 12);
        _serialTextBox.Margin = new Padding(0, 4, 8, 4);
        _serialTextBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            DecideSerial();
        };
        serialRow.Controls.Add(_serialTextBox);
        _serialButton.AutoSize = true;
        _serialButton.MinimumSize = new Size(90, 34);
        _serialButton.Text = "決定";
        _serialButton.Margin = new Padding(0, 2, 16, 2);
        _serialButton.Click += (_, _) => DecideSerial();
        serialRow.Controls.Add(_serialButton);
        _serialValueLabel.AutoSize = true;
        _serialValueLabel.Font = new Font(Font.FontFamily, 14, FontStyle.Bold);
        _serialValueLabel.Margin = new Padding(0, 6, 0, 0);
        serialRow.Controls.Add(_serialValueLabel);
        table.Controls.Add(serialRow, 1, 2);
        table.SetColumnSpan(serialRow, 2);
        // 打ち間違えたときの直し方(2026-11-27 人の決定「案内だけ」)。変えられる・変えられないで文を替える(UpdateLearningControls)。
        _serialHintLabel.AutoSize = true;
        _serialHintLabel.ForeColor = Color.DimGray;
        _serialHintLabel.Margin = new Padding(0, 0, 0, 4);
        table.Controls.Add(_serialHintLabel, 1, 3);
        table.SetColumnSpan(_serialHintLabel, 2);
        group.Controls.Add(table);
        // Dock=Top の GroupBox は AutoSize が効かないので、中身の高さに合わせる。
        group.AutoSize = false;
        // 枠の見出しと余白の分(枠の高さ - 中身を置ける高さ)を足す。
        void FitGroup()
        {
            int height = table.Height + (group.Height - group.DisplayRectangle.Height);
            if (group.Height != height) group.Height = height;
        }
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
        _learningBanner = banner;

        // Dock は後に足したものから配置されるので、帯が最上部、その下に「次にすること」、記録の状態、
        // (開発用の代わりのパネル、)残りが今までの画面。
        var padding = new Panel { Dock = DockStyle.Top, Height = 8 };
        Controls.Add(padding);
        if (_useVirtualPanel) Controls.Add(BuildVirtualPanelUi());
        Controls.Add(group);
        Controls.Add(BuildNextStepLabel());
        Controls.Add(banner);
    }

    private void UpdateLearningControls()
    {
        if (!IsLearningMode || _panel is null) return;
        _saveDirectoryLabel.Text = _learning is null ? "未設定" : _learning.SaveDirectory + "　台帳：" + Path.GetFileName(_learning.LedgerLocation) + "　xdt：シリアル番号のフォルダー";
        _learningStateLabel.Text = _learning?.StateText ?? "記録できません";
        _learningStateLabel.ForeColor = _learning is { Blocked: false, SerialNumber: not null } ? Color.DarkGreen : Color.DarkRed;
        _retryButton.Enabled = _learning is { Blocked: true } && !_operationInProgress && !_serialDialogOpen && !PanelBusy;
        string? serial = _learning?.SerialNumber;
        _serialValueLabel.Text = serial is null ? "今の番号：なし（入れるまで測定できません）" : "今の番号：" + serial;
        _serialValueLabel.ForeColor = serial is null ? Color.DarkRed : Color.DarkGreen;
        bool serialEditable = SerialInputAllowed;
        _serialTextBox.Enabled = serialEditable;
        _serialButton.Enabled = serialEditable;
        _serialHintLabel.Text = serial is null
            ? "番号を入れて「決定」（Enter でも決定）"
            : serialEditable
                ? "打ち間違えたら：正しい番号を入れ直して「決定」で切り替わります（間違えた番号で測った記録は、その番号のフォルダーと台帳に残ります）"
                : "番号を変えるには：測定を止めて、Tab5 を 1 画面目に戻してください（測っている間・操作を選んでいる間は変えられません）";
        if (_learningBanner is not null)
        {
            string head = As100201Application.LearningModeLabel + "　" + (serial ?? "シリアル番号なし");
            _learningBanner.Text = head + (_useVirtualPanel
                ? "（開発用：タッチパネルの代わり）　xdt と台帳は本番と同じく保存します"
                : "　タッチパネルで調整操作を記録し、測定のたびに xdt と台帳を保存します");
        }
    }

    // シリアル番号を変えてよいか。測定中・パネルで選んでいる途中・ほかの処理中・保存の停止中・確認の窓を出している間は変えない
    // (番号を記録の途中で変えないため。AI の仮決め)。
    private bool SerialInputAllowed =>
        _learning is { Blocked: false } && !_measurementStarted && !_panelSelecting && _panelScreen is As100201PanelScreen.Home &&
        !_operationInProgress && !_waveformRestartInProgress && !PanelBusy && !_serialDialogOpen && !_closing;

    // 「決定」(Enter)。数字をシリアル番号にし、台帳にもうある番号なら続きにするか確かめてから決める(2026-11-26 人の決定)。
    private void DecideSerial()
    {
        if (_learning is null || !SerialInputAllowed) return;
        if (!As100201LearningSession.TryFormatSerial(_serialTextBox.Text, out string serial, out string error))
        {
            AppendLog(error);
            ErrorNotification.Show(this, error, "シリアル番号", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _serialTextBox.Focus();
            _serialTextBox.SelectAll();
            return;
        }
        if (serial == _learning.SerialNumber)
        {
            AppendLog($"シリアル番号は {serial} のままです。");
            _serialTextBox.Clear();
            return;
        }
        _serialDialogOpen = true;
        RefreshReadiness();
        UpdateControls();
        try
        {
            As100201LearningSession.SerialHistory? history;
            try { history = _learning.FindSerial(serial); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppendLog($"台帳を読めないため、シリアル番号 {serial} を決められません：{ex.Message}");
                ErrorNotification.Show(this, $"台帳を読めないため、シリアル番号を決められません。\r\n\r\n{ex.Message}", "シリアル番号", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (history is not null)
            {
                var culture = System.Globalization.CultureInfo.InvariantCulture;
                var answer = ErrorNotification.Show(this,
                    $"{serial} は前に記録があります。\r\n\r\n" +
                    $"{(history.Completed ? "完了済み" : "未完了")}・測定 {history.MeasurementCount} 回・操作 {history.OperationCount} 回" +
                    $"（最後の記録 {history.LastTime.ToString("yyyy-MM-dd HH:mm", culture)}）\r\n\r\n" +
                    "続きとして記録しますか？\r\n「いいえ」で番号を入れ直せます。",
                    "シリアル番号の確認", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes)
                {
                    AppendLog($"{serial} の続きにするのをやめました。番号を入れ直してください。");
                    _serialTextBox.Focus();
                    _serialTextBox.SelectAll();
                    return;
                }
            }
            if (_learning.SelectSerial(serial, history)) _serialTextBox.Clear();
        }
        finally
        {
            _serialDialogOpen = false;
            AfterLearningChange();
        }
    }

    // 学習モードのパネルの行(操作・完了・取消・ノズルの戻る)を記録する。測定中なら先に止めて、そこまでの波形を測定の行にする
    // (人が次の行動を決めた所までを学習データとして切り出す。版 6 からは、ふつうは「バルブを動かす」でもう止まっている)。
    // 「合わせた」なら停止のきっかけは「合わせた」、ノズルの「戻る」なら「戻る」。
    private void ExecuteLearningCommand(As100201PanelCommand command)
    {
        if (_panel is null) return;
        if (_learning is null)
        {
            // 学習モードでは起動時に作るので、ここには来ない(念のため記録せずに断る)。
            AppendLog("保存先が決まっていないため記録できません。");
            _panel.TrySend(As100201PanelProtocol.Nak("記録できません"));
            return;
        }
        if (_serialDialogOpen)
        {
            // 番号の確認の窓を出している間は、どの番号で記録するか決まっていないので受け付けない(2026-11-26)。
            AppendLog($"シリアル番号の確認中のため、{command.DisplayName} は記録しません。");
            _panel.TrySend(As100201PanelProtocol.Nak("PC でシリアル番号を確認中です"));
            return;
        }
        try
        {
            string trigger = command switch
            {
                { NozzleStep: As100201NozzleStep.Aligned } => As100201LearningSession.StopByAligned,
                { Kind: As100201PanelCommandKind.NozzleCancel } => As100201LearningSession.StopByNozzleBack,
                _ => As100201LearningSession.StopByPanel
            };
            StopForPanel(command, trigger);
            char? aligningBefore = _learning.AligningNozzleSide;
            string reply = _learning.HandleCommand(command);
            _panel.TrySend(reply);
            // 「合わせた」を取り消したら、パネルを合わせている画面へ戻す(版 6)。
            if (command.Kind == As100201PanelCommandKind.Undo && aligningBefore is null &&
                _learning.AligningNozzleSide is char side)
                _panel.TrySend(As100201PanelProtocol.GotoNozzle(side));
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
    // 版 6 で 2 回押しをやめ、位相ごとのボタンにした。台帳の「側」は PC / PC（位相2）/ CR / CR（位相2）、xdt の名前は PC / PC2 / CR / CR2。
    private (string Side, string SideCode, string Phase) DescribeCurrentWaveform()
    {
        if (_lastWaveform is not (As100201Switch press, int phase)) return ("", "", "");
        string side = press.Button == "PC_go" ? "PC" : "CR";
        bool second = press.Kind == As100201PressKind.Double;
        return (second ? side + "（位相2）" : side, second ? side + "2" : side, phase.ToString(System.Globalization.CultureInfo.InvariantCulture));
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

    // LOGGER を止めた(PC / CR・バルブを動かす・操作ボタン・PC の測定停止など)。学習モードなら測定の行を書く。
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
        // 台帳に書いたら(測定・操作・完了・取消・番号)、取消の対象が変わるのでパネルへ知らせる(版 5)。
        SendLastRecord();
        SendPanelState();
        RefreshReadiness();
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
                    "\r\n\r\n保存できるまで、タッチパネルの PC / CR と操作ボタンを受け付けません（測定停止だけ受け付けます）。" +
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
        RefreshReadiness();
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
        _panel.ScreenChanged -= PanelScreenChanged;
        if (_panelPaths is not null)
        {
            _panelPaths.PathChanged -= PanelPathChanged;
            _panelPaths.PathLost -= PanelPathLost;
        }
        if (_panelWifi is not null) _panelWifi.Notice -= PanelWifiNotice;
        _panel.Dispose();
    }

    // 測定を始めてよいか(PC_go / CR_go / F8 の名前で聞く)。学習モードの決まりは As100201LearningGate。
    // 版 6 からは通常モードでもタッチパネルが要る(Tab5 が使えないときは測定できない。2026-11-27 人の決定)。
    private bool CanExecuteSwitch(As100201Switch pressed, out string refusal)
    {
        if (!IsLearningMode)
        {
            refusal = PanelReady ? As100201LearningGate.NotReady : As100201LearningGate.PanelLost;
            return _readyToMeasure && ConnectionsReady && PanelReady && !_operationInProgress;
        }
        return As100201LearningGate.Allows(pressed.Button,
            baseReady: _templateLoaded && ConnectionsReady && !_operationInProgress,
            panelReady: PanelReady,
            hasSession: _learning is not null,
            saveBlocked: _learning is { Blocked: true },
            readyLedOn: _readyToMeasure, out refusal,
            panelSelecting: _panelSelecting,
            serialMissing: _learning is { SerialNumber: null });
    }

    // 台帳のファイル名(2026-11-09 人の決定)。
    public const string LedgerFileName = "調整記録.csv";
    // 保存先: exe の隣の「学習データ」(2026-11-10 人の決定。それまでは雛形と同じフォルダー)。
    // 台帳はこの直下に 1 つ、xdt はシリアル番号のフォルダー(As100201LearningSession.BuildXdtPath。2026-11-26 から)。
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
