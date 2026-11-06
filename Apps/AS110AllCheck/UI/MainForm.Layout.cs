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
    private static readonly bool ShowTemplateDebugControls = false;
    private readonly Button _deleteTemplateButton = new();
    private readonly Label _arduinoValueLabel = new();
    private readonly Label _wfValueLabel = new();
    private readonly Label _templateValueLabel = new();
    private readonly Panel _dropPanel = new();
    private readonly Label _dropTextLabel = new();
    private readonly Button _initializeButton = new();
    private readonly Button _startButton = new();
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
            RowCount = 7
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        // ドラッグ欄を非表示にするときは行の高さも0にして余白を残しません。
        root.RowStyles.Add(ShowTemplateDebugControls
            ? new RowStyle(SizeType.AutoSize)
            : new RowStyle(SizeType.Absolute, 0));
        root.RowStyles.Add(ShowTemplateDebugControls
            ? new RowStyle(SizeType.AutoSize)
            : new RowStyle(SizeType.Absolute, 0));

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

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 12)
        };

        ConfigureButton(_initializeButton, "確認", InitializeClicked);
        ConfigureButton(_startButton, "測定開始（確認用）", StartClicked);
        ConfigureButton(_deleteTemplateButton, "雛形を削除", DeleteTemplateClicked);
        _deleteTemplateButton.Visible = ShowTemplateDebugControls;
        ConfigureButton(_getStateButton, "状態確認", GetStateClicked);
        ConfigureButton(_restoreWifiButton, "元のWi-Fiへ戻す", RestoreWifiClicked);
        ConfigureButton(_exitButton, "アプリ終了", (_, _) => Close());
        // 上段の操作ボタンは、下の型式・S/N・保存先の入力欄より目立たないよう一回り小さくする。
        foreach (var button in new[] { _initializeButton, _startButton, _deleteTemplateButton, _getStateButton, _restoreWifiButton, _exitButton })
            button.MinimumSize = new Size(120, 34);

        BuildDeveloperContactControls(buttons);

        buttons.Controls.AddRange(
        [
            _initializeButton,
            _startButton,
            _deleteTemplateButton,
            _getStateButton,
            _restoreWifiButton,
            _exitButton
        ]);

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
            Text = "参照波形：",
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

        BuildStandSelection();
        // モードを選んでからスタンドを選び、次に参照波形を選択する。
        var connectionOrReferencePanel = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty
        };
        connectionOrReferencePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        connectionOrReferencePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        connectionOrReferencePanel.Controls.Add(statusPanel, 0, 0);
        connectionOrReferencePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        connectionOrReferencePanel.Controls.Add(BuildModeSelectionPanel(), 0, 1);
        connectionOrReferencePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        connectionOrReferencePanel.Controls.Add(_standSelectionPanel, 0, 2);
        connectionOrReferencePanel.Controls.Add(_referenceSelectionPanel, 0, 3);

        _dropPanel.Visible = ShowTemplateDebugControls;
        _dropPanel.AllowDrop = true;
        _dropPanel.Dock = DockStyle.Fill;
        _dropPanel.Height = 75;
        _dropPanel.BorderStyle = BorderStyle.FixedSingle;
        _dropPanel.BackColor = Color.AliceBlue;
        _dropPanel.DragEnter += TemplateDragEnter;
        _dropPanel.DragDrop += TemplateDragDrop;
        _dropTextLabel.Dock = DockStyle.Fill;
        _dropTextLabel.TextAlign = ContentAlignment.MiddleCenter;
        _dropTextLabel.AllowDrop = true;
        _dropTextLabel.DragEnter += TemplateDragEnter;
        _dropTextLabel.DragDrop += TemplateDragDrop;
        _dropPanel.Controls.Add(_dropTextLabel);
        _templateValueLabel.Visible = ShowTemplateDebugControls;
        _templateValueLabel.AutoSize = true;
        var templateStatus = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Visible = ShowTemplateDebugControls
        };
        templateStatus.Controls.Add(_templateValueLabel);

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
        root.Controls.Add(_dropPanel, 0, 5);
        root.Controls.Add(templateStatus, 0, 6);
        root.Controls.Add(BuildProductionPanel(), 0, 7);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowCount = 8;

        // 操作欄だけをスクロールさせ、ログ欄の高さを常に確保する。
        var operationViewport = new Panel { Dock=DockStyle.Fill, AutoScroll=true, Margin=Padding.Empty };
        operationViewport.Controls.Add(root);
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

    private static void ConfigureButton(Button button, string text, EventHandler handler)
    {
        button.AutoSize = true;
        button.MinimumSize = new Size(140, 42);
        button.Text = text;
        button.Margin = new Padding(0, 0, 10, 0);
        button.Click += handler;
    }

    private void AppendLog(string message)
    {
        ArduinoDiagnosticLog.Write("APP", message);
        _logTextBox.AppendText($"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
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
        _templateValueLabel.Text = _templateLoaded
            ? "雛形：読み込み済み（LOGGER側）" : "雛形：未登録";
        _dropTextLabel.Text = _templateLoaded
            ? "雛形読み込み済み（LOGGERで開いているファイルを使用）"
            : "「雛形」にモードと本体に対応する.xdt/.ydtを置いてください\r\n参照波形.xybは一覧から選択します";
        bool nfOk = _arduino.MeasurementInputReady && _arduino.NfEnergized;
        bool showReferenceSelection = ConnectionsReady && !_checkingConnections && !_measurementState.ErrorLogged;
        _connectionStatusPanel.Visible = !showReferenceSelection;
        UpdateStandControls(showReferenceSelection);
        _referenceSelectionPanel.Visible = showReferenceSelection && _standScalingApplied && _templateLoaded;
        _referenceComboBox.Enabled = !_operationInProgress && ConnectionsReady &&
            _templateLoaded && _standScalingApplied && LoggerReadyToStart;
        _referenceStatusLabel.Text = _selectedReferencePath is not null
            ? "読み込み操作済み（LOGGER画面で確認）"
            : _referenceComboBox.Items.Count <= 1 ? "候補なし（雛形フォルダへ.xybを追加）" : "未選択";
        _dropPanel.BackColor = ConnectionsReady && !_templateLoaded ? Color.AliceBlue : SystemColors.Control;

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
        _startButton.Enabled = !_operationInProgress && nfOk && ConnectionsReady && _templateLoaded && _standScalingApplied && LoggerReadyToStart;
        _deleteTemplateButton.Enabled = !_operationInProgress && _templateLoaded;
        _getStateButton.Enabled = !_operationInProgress && initialized;
        _restoreWifiButton.Enabled =
            !_operationInProgress && _nrXConnection.WifiRestoreRequired;
        UpdateProductionControls();
    }
}
