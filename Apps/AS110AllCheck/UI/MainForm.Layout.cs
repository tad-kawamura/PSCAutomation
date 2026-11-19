using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PSCAutomation.Apps.AS110AllCheck;

// MainFormを役割別に分割。すべて同じFormの一部です。
// MainFormのうち、GUIの配置と表示更新を担当する。
// ボタン・選択欄・接続表示・ログ欄を作成し、操作時の処理を登録する。
// 接続状況に応じて、接続情報と参照波形の選択欄を切り替える。
// 測定条件や処理状況に応じて、各ボタンの有効・無効を切り替える。
// GUIの位置・色・表示文字を変更するときは、このファイルを修正する。
internal sealed partial class MainForm : Form
{
    private readonly Label _arduinoValueLabel = new();
    private readonly Label _wfValueLabel = new();
    // 操作欄(ログ欄の上。高さが足りないときはここだけスクロールする)。
    private readonly Panel _operationViewport = new() { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
    // 画面のログ欄に残す行数。これを超えたら、古い行を画面から外す(全部の行は Logs の「画面ログ_日付.txt」に残る。ScreenLogFile)。
    private const int MaxScreenLogLines = 2000, KeptScreenLogLines = 1000;
    private int _screenLogLines;
    private readonly Button _initializeButton = new();
    private readonly Button _startButton = new();
    private readonly Label _nextStepLabel = new();
    private readonly Button _getStateButton = new();
    private readonly Button _restoreWifiButton = new();
    private readonly Button _exitButton = new();
    private readonly ComboBox _loggerComboBox = new();
    private readonly ComboBox _connectionModeComboBox = new();
    private readonly Label _wifiProfileLabel = new();
    private readonly ComboBox _wifiProfileComboBox = new();
    private readonly Label _loggerSelectionValueLabel = new();
    private readonly Label _statusValueLabel = new();
    private readonly Label _hardwareValueLabel = new();
    private readonly TextBox _logTextBox = new();
    private readonly TableLayoutPanel _connectionStatusPanel = new();
    private readonly FlowLayoutPanel _referenceSelectionPanel = new();
    private readonly ComboBox _referenceComboBox = new();
    private readonly Label _referenceStatusLabel = new();

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty,
            ColumnCount = 1,
            RowCount = 6
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            AutoSize = true,
            Font = new Font(Font.FontFamily, 15, FontStyle.Bold),
            Text = "AS110 NF弁通電確認・測定",
            Margin = new Padding(0, 0, 0, 8)
        };

        var loggerSelectionPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 12)
        };
        loggerSelectionPanel.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Text = "使用LOGGER：",
            Margin = new Padding(0, 6, 4, 0)
        });

        _loggerSelectionValueLabel.AutoSize = true;
        _loggerSelectionValueLabel.Margin = new Padding(0, 6, 8, 0);
        loggerSelectionPanel.Controls.Add(_loggerSelectionValueLabel);

        _loggerComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _loggerComboBox.FormattingEnabled = true;
        _loggerComboBox.Width = 190;
        _loggerComboBox.Format += (_, e) =>
        {
            if (e.ListItem is WaveLoggerEdition edition)
            {
                e.Value = WaveLoggerSession.GetEditionDisplayName(edition);
            }
        };
        loggerSelectionPanel.Controls.Add(_loggerComboBox);

        var connectionPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 12)
        };
        connectionPanel.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Text = "接続方式：",
            Margin = new Padding(0, 6, 4, 0)
        });

        _connectionModeComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _connectionModeComboBox.Width = 230;
        _connectionModeComboBox.FormattingEnabled = true;
        _connectionModeComboBox.Format += (_, e) =>
        {
            if (e.ListItem is NrConnectionMode mode)
            {
                e.Value = mode == NrConnectionMode.Usb
                    ? "USB"
                    : "Wi-Fi";
            }
        };
        _connectionModeComboBox.SelectedIndexChanged += ConnectionModeChanged;
        connectionPanel.Controls.Add(_connectionModeComboBox);

        _wifiProfileLabel.AutoSize = true;
        _wifiProfileLabel.Font = new Font(Font, FontStyle.Bold);
        _wifiProfileLabel.Text = "　Wi-Fiプロファイル：";
        _wifiProfileLabel.Margin = new Padding(8, 6, 4, 0);
        connectionPanel.Controls.Add(_wifiProfileLabel);

        _wifiProfileComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _wifiProfileComboBox.Width = 180;
        connectionPanel.Controls.Add(_wifiProfileComboBox);
        // 開発者モードは上段のボタン列を圧迫しないよう、接続方式の右隣に置く。
        BuildDeveloperContactControls(connectionPanel);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 12)
        };

        // 上段の操作ボタンは、下の型式・S/N・保存先の入力欄より目立たないよう一回り小さくする。
        var topButtonSize = new Size(110, 30);
        ConfigureButton(_initializeButton, "確認", InitializeClicked, topButtonSize);
        ConfigureButton(_startButton, "測定開始（確認用）", StartClicked, topButtonSize);
        ConfigureButton(_getStateButton, "状態確認", GetStateClicked, topButtonSize);
        ConfigureButton(_restoreWifiButton, "元のWi-Fiへ戻す", RestoreWifiClicked, topButtonSize);
        ConfigureButton(_exitButton, "アプリ終了", (_, _) => Close(), topButtonSize);

        buttons.Controls.AddRange(
        [
            _initializeButton,
            _startButton,
            _getStateButton,
            _restoreWifiButton,
            _exitButton
        ]);
        // 「次にすること」: データ調整(測定開始)ボタンが押せない理由と次の操作を、ボタンのすぐ下に 1 行で出す(2026-11-06、人の要望。
        // 立ち上げて設定中に何をすればいいか分からない、ボタンが押せない理由が分からないため)。押せるときは緑、押せないときは赤。
        buttons.SetFlowBreak(_exitButton, true);
        // 幅は窓の幅に合わせて折り返す(FitNextStepLabel。2026-11-14 までは 720 に固定で、窓を狭くすると右端が切れた)。
        _nextStepLabel.AutoSize = true;
        _nextStepLabel.MaximumSize = new Size(720, 0);
        _nextStepLabel.Font = new Font(Font.FontFamily, 11, FontStyle.Bold);
        _nextStepLabel.Margin = new Padding(0, 8, 0, 0);
        buttons.Controls.Add(_nextStepLabel);

        // 接続先ごとに1行。見出しと値の開始位置をそろえる。
        var statusPanel = _connectionStatusPanel;
        statusPanel.AutoSize = true;
        statusPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        statusPanel.Dock = DockStyle.Fill;
        statusPanel.ColumnCount = 2;
        statusPanel.RowCount = 4;
        statusPanel.Margin = new Padding(0, 0, 0, 10);
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddConnectionStatusRow(statusPanel, 0, "Automation Server：", _statusValueLabel);
        AddConnectionStatusRow(statusPanel, 1, "接続本体：", _hardwareValueLabel);
        AddConnectionStatusRow(statusPanel, 2, "マイコン：", _arduinoValueLabel);
        AddConnectionStatusRow(statusPanel, 3, "発振器：", _wfValueLabel);

        _referenceSelectionPanel.AutoSize = true;
        _referenceSelectionPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _referenceSelectionPanel.Dock = DockStyle.Fill;
        _referenceSelectionPanel.WrapContents = true;
        _referenceSelectionPanel.Margin = new Padding(0, 0, 0, 10);
        _referenceSelectionPanel.Visible = false;
        _referenceSelectionPanel.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Text = "型式（参照波形）：",
            Margin = new Padding(0, 6, 8, 0)
        });
        _referenceComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _referenceComboBox.Width = 260;
        _referenceComboBox.DropDownWidth = 420;
        _referenceComboBox.DropDown += ReferenceDropDown;
        _referenceComboBox.SelectionChangeCommitted += ReferenceSelectionCommitted;
        _referenceSelectionPanel.Controls.Add(_referenceComboBox);
        _referenceStatusLabel.AutoSize = true;
        _referenceStatusLabel.Margin = new Padding(8, 6, 0, 0);
        _referenceSelectionPanel.Controls.Add(_referenceStatusLabel);

        BuildCalibrationPanel();
        // 最初の画面は「校正」「測定」の 2 つのボタン(2026-11-19)。「測定」を押すとモード欄が出て、モードを選ぶと今日の校正値で雛形を読み込み、参照波形(型式)を選択する。
        var connectionOrReferencePanel = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Margin = Padding.Empty
        };
        for (int i = 0; i < 5; i++) connectionOrReferencePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        connectionOrReferencePanel.Controls.Add(statusPanel, 0, 0);
        connectionOrReferencePanel.Controls.Add(BuildHomePanel(), 0, 1);
        connectionOrReferencePanel.Controls.Add(BuildModeSelectionPanel(), 0, 2);
        connectionOrReferencePanel.Controls.Add(_calibrationPanel, 0, 3);
        connectionOrReferencePanel.Controls.Add(_referenceSelectionPanel, 0, 4);

        _logTextBox.Dock = DockStyle.Fill;
        _logTextBox.Multiline = true;
        _logTextBox.ReadOnly = true;
        _logTextBox.ScrollBars = ScrollBars.Vertical;
        _logTextBox.Font = new Font("Consolas", 10);
        _logTextBox.Margin = new Padding(0, 8, 0, 0);

        root.Controls.Add(title, 0, 0);
        root.Controls.Add(loggerSelectionPanel, 0, 1);
        root.Controls.Add(connectionPanel, 0, 2);
        root.Controls.Add(buttons, 0, 3);
        root.Controls.Add(connectionOrReferencePanel, 0, 4);
        root.Controls.Add(BuildProductionPanel(), 0, 5);

        // 操作欄だけをスクロールさせ、ログ欄の高さを常に確保する。
        var operationViewport = _operationViewport;
        operationViewport.Controls.Add(root);
        operationViewport.ClientSizeChanged += (_, _) => FitNextStepLabel();
        var frame = new TableLayoutPanel
        {
            Dock=DockStyle.Fill, Padding=new Padding(18), ColumnCount=1, RowCount=2
        };
        frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        frame.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        frame.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        frame.Controls.Add(operationViewport, 0, 0);
        frame.Controls.Add(_logTextBox, 0, 1);
        Controls.Add(frame);
    }

    private void AddConnectionStatusRow(TableLayoutPanel panel, int row, string caption, Label valueLabel)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Font = new Font(Font, FontStyle.Bold),
            Text = caption,
            Margin = new Padding(0, 5, 16, 5)
        }, 0, row);
        valueLabel.AutoSize = true;
        valueLabel.Dock = DockStyle.Fill;
        valueLabel.Margin = new Padding(0, 5, 0, 5);
        panel.Controls.Add(valueLabel, 1, row);
    }

    // ボタンは AutoSize(GrowOnly)なので、あとから MinimumSize を小さくしても縮まない。大きさは最初にここで決める。
    private static void ConfigureButton(Button button, string text, EventHandler handler, Size? minimumSize = null)
    {
        button.AutoSize = true;
        button.MinimumSize = minimumSize ?? new Size(140, 42);
        button.Text = text;
        button.Margin = new Padding(0, 0, 10, 0);
        button.Click += handler;
    }

    // 「次にすること」を、操作欄の幅で折り返す(窓の幅を変えたとき・縦のスクロールバーが出たり消えたりしたとき)。
    private void FitNextStepLabel()
    {
        int width = Math.Max(DisplayScale.Pixels(this, 200), _operationViewport.ClientSize.Width - _nextStepLabel.Margin.Horizontal - DisplayScale.Pixels(this, 4));
        if (_nextStepLabel.MaximumSize.Width != width) _nextStepLabel.MaximumSize = new Size(width, 0);
    }

    private void AppendLog(string message)
    {
        DateTime now = DateTime.Now;
        ArduinoDiagnosticLog.Write("APP", message);
        // 画面に出した行は、日ごとのファイルにも全部残す(別スレッドで書くので、ここでは待たない)。
        ScreenLogFile.Default.Write(now, message);
        _logTextBox.AppendText($"{now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        if (++_screenLogLines > MaxScreenLogLines) TrimScreenLog();
    }

    // ログ欄が際限なく増えないよう、古い行を画面から外す(1 日つけたままでも重くならないように)。外した行もファイルには残っている。
    private void TrimScreenLog()
    {
        string[] lines = _logTextBox.Lines;
        // 最後の要素は、最後の改行の後ろの空行。
        int content = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        int skip = Math.Max(0, content - KeptScreenLogLines);
        _logTextBox.Text = $"（これより前のログは、Logs フォルダーの「{ScreenLogFile.FilePrefix}日付.txt」にあります）{Environment.NewLine}" +
            string.Join(Environment.NewLine, lines.Skip(skip).Take(content - skip)) + Environment.NewLine;
        _screenLogLines = content - skip + 1;
        _logTextBox.SelectionStart = _logTextBox.TextLength;
        _logTextBox.ScrollToCaret();
    }

    private void UpdateControls()
    {
        bool initialized = _logger.IsInitialized;
        _statusValueLabel.Text = initialized
            ? $"初期化済み（{WaveLoggerSession.GetEditionDisplayName(_logger.ActiveEdition)}） / {GetLoggerStateText()}"
            : "未初期化";
        _statusValueLabel.ForeColor = initialized ? Color.DarkGreen : Color.DarkRed;

        _hardwareValueLabel.Text = _hardwareDetected
            ? $"{_detectedHardwareName}（接続確認済み）"
            : "未確認";
        _hardwareValueLabel.ForeColor = _hardwareDetected ? Color.DarkGreen : Color.DarkRed;
        _arduinoValueLabel.Text = _arduino.IsConnected ? "M5Stack　接続済み" : "未接続";
        _arduinoValueLabel.ForeColor = _arduino.IsConnected ? Color.DarkGreen : Color.DarkRed;
        _wfValueLabel.Text = (_generator is not null ? $"{GetGeneratorModelName()}　接続済み" : "未接続") +
            (_generatorOutputUnknown ? "・出力状態不明" : "");
        _wfValueLabel.ForeColor = _generator is not null && !_generatorOutputUnknown ? Color.DarkGreen : Color.DarkRed;
        bool nfOk = _arduino.MeasurementInputReady && _arduino.NfEnergized;
        bool showReferenceSelection = ConnectionsReady && !_checkingConnections && !_measurementState.ErrorLogged;
        _connectionStatusPanel.Visible = !showReferenceSelection;
        UpdateHomeControls();
        UpdateCalibrationControls(showReferenceSelection);
        _referenceSelectionPanel.Visible = showReferenceSelection && _calibrationApplied && _templateLoaded;
        _referenceComboBox.Enabled = !_operationInProgress && ConnectionsReady &&
            _templateLoaded && _calibrationApplied && LoggerReadyToStart;
        _referenceStatusLabel.Text = _selectedReferencePath is not null
            ? "読み込み操作済み（LOGGER画面で確認）"
            : _referenceComboBox.Items.Count <= 1 ? "候補なし（雛形フォルダへ.xybを追加）" : "未選択";

        bool wifiSelected = GetSelectedConnectionMode() == NrConnectionMode.Wifi;
        bool wifiAvailable = !wifiSelected || IsWaveLoggerXInstalled();

        _loggerComboBox.Enabled =
            _installedEditions.Count > 1 &&
            !_hardwareDetected &&
            !_operationInProgress &&
            !wifiSelected;
        _connectionModeComboBox.Enabled = !_hardwareDetected && !_operationInProgress;
        _wifiProfileComboBox.Enabled = !_hardwareDetected && !_operationInProgress;
        _initializeButton.Enabled =
            !_operationInProgress &&
            wifiAvailable;
        _startButton.Enabled = !_operationInProgress && nfOk && ConnectionsReady && _templateLoaded && _calibrationApplied && LoggerReadyToStart;
        _getStateButton.Enabled = !_operationInProgress && initialized;
        _restoreWifiButton.Enabled =
            !_operationInProgress && _nrXConnection.WifiRestoreRequired;
        UpdateProductionControls();
    }
}
