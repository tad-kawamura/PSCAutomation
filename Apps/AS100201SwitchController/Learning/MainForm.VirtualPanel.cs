namespace PSCAutomation.Apps.AS100201SwitchController;

// 開発用: タッチパネルの代わり(--virtual-panel、2026-11-10)。Tab5 が届く前に、学習モードの PC 側を実機の LOGGER で確かめる。
// 代わりのボタンは、パネルが確定した 1 行を送ったのと同じ道(As100201PanelProtocol → PanelCommandReceived)を通る。
internal sealed partial class MainForm : Form
{
    private readonly bool _useVirtualPanel;
    private As100201VirtualPanelLink? _virtualPanel;
    private readonly ComboBox _virtualCommandComboBox = new();
    private readonly Label _virtualReplyLabel = new();

    public bool UsesVirtualPanel => _useVirtualPanel;

    private IAs100201SerialLink CreatePanelLink()
    {
        if (!_useVirtualPanel) return new As100201SerialLink();
        _virtualPanel = new As100201VirtualPanelLink();
        _virtualPanel.LineSent += VirtualPanelLineSent;
        return _virtualPanel;
    }

    private GroupBox BuildVirtualPanelUi()
    {
        ClientSize = new Size(ClientSize.Width, ClientSize.Height + 130);

        var group = new GroupBox
        {
            Text = "タッチパネルの代わり（開発用。Tab5 の操作ボタンと同じ行をアプリの中で送ります）",
            Dock = DockStyle.Top,
            Padding = new Padding(10, 6, 10, 8),
            ForeColor = Color.DarkBlue
        };
        var table = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };

        var commands = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 2, 0, 4) };
        _virtualCommandComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _virtualCommandComboBox.Width = 300;
        _virtualCommandComboBox.Margin = new Padding(0, 6, 8, 0);
        _virtualCommandComboBox.FormattingEnabled = true;
        _virtualCommandComboBox.Format += (_, e) =>
        {
            if (e.ListItem is As100201PanelCommand command) e.Value = command.DisplayName;
        };
        foreach (var command in As100201VirtualPanelLink.AllCommands)
            _virtualCommandComboBox.Items.Add(command);
        _virtualCommandComboBox.SelectedIndex = 0;
        commands.Controls.Add(_virtualCommandComboBox);
        commands.Controls.Add(VirtualButton("送る", () =>
        {
            if (_virtualCommandComboBox.SelectedItem is As100201PanelCommand selected) PressVirtualPanel(selected);
        }));
        // よく使うものはボタンにも出す。
        foreach (string line in new[] { "ACT:MAG", "DONE", "UNDO" })
        {
            var command = As100201PanelCommand.Parse(line);
            commands.Controls.Add(VirtualButton(command.DisplayName, () => PressVirtualPanel(command)));
        }

        var faults = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 0, 0, 0) };
        var paused = new CheckBox { AutoSize = true, Text = "定期応答を止める（固まった状態）", Margin = new Padding(0, 4, 16, 0), ForeColor = SystemColors.ControlText };
        paused.CheckedChanged += (_, _) =>
        {
            if (_virtualPanel is null) return;
            _virtualPanel.FramesPaused = paused.Checked;
            AppendLog(paused.Checked ? "代わりのパネル：定期応答を止めました。" : "代わりのパネル：定期応答を再開しました。");
        };
        var unplugged = new CheckBox { AutoSize = true, Text = "USB を抜いた状態", Margin = new Padding(0, 4, 16, 0), ForeColor = SystemColors.ControlText };
        unplugged.CheckedChanged += (_, _) =>
        {
            if (_virtualPanel is null) return;
            AppendLog(unplugged.Checked ? "代わりのパネル：USB を抜いた状態にしました。" : "代わりのパネル：USB を差し直しました（自動の再接続を待ちます）。");
            _virtualPanel.Unplugged = unplugged.Checked;
        };
        _virtualReplyLabel.AutoSize = true;
        _virtualReplyLabel.Margin = new Padding(0, 5, 0, 0);
        _virtualReplyLabel.Text = "パネルへの返事：（まだありません）";
        _virtualReplyLabel.ForeColor = SystemColors.ControlText;
        faults.Controls.Add(paused);
        faults.Controls.Add(unplugged);
        faults.Controls.Add(_virtualReplyLabel);

        table.Controls.Add(commands, 0, 0);
        table.Controls.Add(faults, 0, 1);
        group.Controls.Add(table);
        void FitGroup() => group.Height = table.Height + group.Padding.Vertical + 22;
        table.SizeChanged += (_, _) => FitGroup();
        FitGroup();
        return group;
    }

    private static Button VirtualButton(string text, Action action)
    {
        var button = new Button
        {
            AutoSize = true,
            MinimumSize = new Size(90, 34),
            Text = text,
            Margin = new Padding(0, 0, 8, 0),
            ForeColor = SystemColors.ControlText
        };
        button.Click += (_, _) => action();
        return button;
    }

    private void PressVirtualPanel(As100201PanelCommand command)
    {
        if (_virtualPanel is null) return;
        AppendLog($"代わりのパネル：{command.DisplayName}（{command.Line}）を送ります。");
        if (!_virtualPanel.Press(command.Line))
            AppendLog("代わりのパネルは接続されていないため送れません（接続確認・再接続を待ってください）。");
    }

    private void VirtualPanelLineSent(string line)
    {
        PostToUi(() =>
        {
            _virtualReplyLabel.Text = "パネルへの返事：" + line;
            _virtualReplyLabel.ForeColor = line.StartsWith("ACK:", StringComparison.Ordinal) ? Color.DarkGreen : Color.DarkRed;
            AppendLog("タッチパネルへの返事：" + line);
        });
    }
}
