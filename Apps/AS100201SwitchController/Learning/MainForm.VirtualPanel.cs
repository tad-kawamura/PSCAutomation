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
        if (!_useVirtualPanel)
        {
            // USB と Wi-Fi の 2 本(2026-11-13 承認。両方つながっていれば USB、切れたらもう一方へ)。
            _panelPaths = new As100201PanelPathLink(new As100201SerialLink(),
                new As100201NetworkLink(As100201PanelNetworkSettings.Host, As100201PanelNetworkSettings.Port));
            _panelPaths.PathChanged += PanelPathChanged;
            return _panelPaths;
        }
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
        // スペーサーのときだけ使う: 厚さ(箱の表示)と表示との差(μm)。パネルの 3・4 画面目の代わり。
        var thickness = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80, Margin = new Padding(0, 6, 4, 0) };
        foreach (string t in As100201SpacerCatalog.Thicknesses) thickness.Items.Add(t);
        thickness.SelectedItem = As100201SpacerCatalog.DefaultThickness;
        var difference = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 60, Margin = new Padding(0, 6, 8, 0) };
        foreach (string d in As100201SpacerCatalog.Differences) difference.Items.Add(d);
        difference.SelectedItem = As100201PanelCommand.NotMeasured;
        void UpdateSpacerInputs() => thickness.Enabled = difference.Enabled =
            _virtualCommandComboBox.SelectedItem is As100201PanelCommand { Operation: As100201Operation.SpacerP or As100201Operation.SpacerR };
        _virtualCommandComboBox.SelectedIndexChanged += (_, _) => UpdateSpacerInputs();
        UpdateSpacerInputs();
        commands.Controls.Add(new Label { AutoSize = true, Text = "厚さ", Margin = new Padding(0, 10, 2, 0), ForeColor = SystemColors.ControlText });
        commands.Controls.Add(thickness);
        commands.Controls.Add(new Label { AutoSize = true, Text = "差μm", Margin = new Padding(0, 10, 2, 0), ForeColor = SystemColors.ControlText });
        commands.Controls.Add(difference);
        commands.Controls.Add(VirtualButton("送る", () =>
        {
            if (_virtualCommandComboBox.SelectedItem is As100201PanelCommand selected)
                PressVirtualPanel(As100201VirtualPanelLink.WithSpacer(selected, (string)thickness.SelectedItem!, (string)difference.SelectedItem!));
        }));
        // よく使うものはボタンにも出す。HOLD はパネルに触れた(1 画面目のボタン・暗い画面)ときに送る行。
        foreach (string line in new[] { "ACT:MAG", "DONE", "UNDO", "HOLD" })
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
        // パネルが 2 画面目以降にいる状態(版 4)。入れると SELECT、外すと HOME を送る。
        var selecting = new CheckBox { AutoSize = true, Text = "選んでいる途中（SELECT）", Margin = new Padding(0, 4, 16, 0), ForeColor = SystemColors.ControlText };
        selecting.CheckedChanged += (_, _) =>
        {
            if (_virtualPanel is null) return;
            string line = selecting.Checked ? As100201PanelProtocol.SelectingLine : As100201PanelProtocol.HomeLine;
            if (!_virtualPanel.Press(line)) AppendLog("代わりのパネルは接続されていないため送れません。");
        };
        faults.Controls.Add(selecting);
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
