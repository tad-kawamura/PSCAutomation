using System.Runtime.InteropServices;

namespace PSCAutomation.Apps.AS100201SwitchController;

// Layout: 画面・接続・操作を役割ごとに分割する。
internal sealed partial class MainForm : Form
{
    private readonly Button _exitButton = new();
    private readonly TableLayoutPanel _connectionStatusPanel = new();

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 8
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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
            Text = As100201Application.Heading,
            Margin = new Padding(0, 0, 0, 8)
        };

        var instruction = new Label
        {
            AutoSize = true,
            Text = As100201Application.Instructions,
            Margin = new Padding(0, 0, 0, 14)
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
        // 移植時に USB だけにした(元は USB / Wi-Fi の選択とWi-Fiプロファイルの選択)。
        connectionPanel.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "USB（NR-500 / NR-X）",
            Margin = new Padding(0, 6, 4, 0)
        });

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
        ConfigureButton(_getStateButton, "状態確認", GetStateClicked);

        ConfigureButton(_exitButton, "アプリ終了", (_, _) => Close());

        buttons.Controls.AddRange(
        [
            _initializeButton,
            _startButton,
            _deleteTemplateButton,
            _getStateButton,
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


        _dropPanel.AllowDrop = true;
        _dropPanel.Dock = DockStyle.Fill;
        _dropPanel.Height = 75;
        _dropPanel.BorderStyle = BorderStyle.FixedSingle;
        _dropPanel.BackColor = Color.AliceBlue;
        _dropPanel.DragEnter += TemplateDragEnter;
        _dropPanel.DragDrop += TemplateDragDrop;
        var dropText = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            Text = "ここに雛形ファイルを1件ドラッグ" };
        dropText.AllowDrop = true;
        dropText.DragEnter += TemplateDragEnter;
        dropText.DragDrop += TemplateDragDrop;
        _dropPanel.Controls.Add(dropText);
        _templateValueLabel.AutoSize = true;
        _readyValueLabel.AutoSize = true;
        var templateStatus = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        templateStatus.Controls.Add(_templateValueLabel);
        templateStatus.Controls.Add(_readyValueLabel);

        _logTextBox.Dock = DockStyle.Fill;
        _logTextBox.Multiline = true;
        _logTextBox.ReadOnly = true;
        _logTextBox.ScrollBars = ScrollBars.Vertical;
        _logTextBox.Font = new Font("Consolas", 10);

        root.Controls.Add(title, 0, 0);
        root.Controls.Add(instruction, 0, 1);
        root.Controls.Add(loggerSelectionPanel, 0, 2);
        root.Controls.Add(connectionPanel, 0, 3);
        root.Controls.Add(buttons, 0, 4);
        root.Controls.Add(statusPanel, 0, 5);
        root.Controls.Add(_dropPanel, 0, 6);
        root.Controls.Add(templateStatus, 0, 7);
        // 2 回押しの待ち時間・位相の入力欄(試用版、2026-11-09)。
        root.RowCount = 10;
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(BuildSwitchSettingsPanel(), 0, 8);
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(_logTextBox, 0, 9);
        Controls.Add(root);
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

}
