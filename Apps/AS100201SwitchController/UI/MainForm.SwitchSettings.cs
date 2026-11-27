namespace PSCAutomation.Apps.AS100201SwitchController;

// 位相を画面で変え、設定ファイルに残す(2026-11-09)。位相の入力は標準の機能(人の指示で試用版だけにしない)。
// 版 6(2026-11-27): 2 回押しをやめ、Tab5 の発振器のボタン 4 つ(PC 位相1・2、CR 位相1・2)の位相にした。
// 設定ファイルの項目名は今までのまま(1 回押し = 位相1、2 回押し = 位相2)。2 回押しの待ち時間は使わない。
internal sealed partial class MainForm : Form
{
    private readonly NumericUpDown _pcSinglePhaseInput = new();
    private readonly NumericUpDown _pcDoublePhaseInput = new();
    private readonly NumericUpDown _crSinglePhaseInput = new();
    private readonly NumericUpDown _crDoublePhaseInput = new();
    private readonly Button _resetSwitchSettingsButton = new();
    private bool _showingSwitchSettings;

    private Control BuildSwitchSettingsPanel()
    {
        var group = new GroupBox
        {
            Text = "出し直しの位相（タッチパネルの PC / CR のボタン。ボタンの名前にも出ます）",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 6, 8, 6),
            Margin = new Padding(0, 0, 0, 10)
        };
        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 2
        };
        for (int i = 0; i < panel.ColumnCount; i++) panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        foreach (var input in new[] { _pcSinglePhaseInput, _pcDoublePhaseInput, _crSinglePhaseInput, _crDoublePhaseInput })
            ConfigureNumber(input, 0, 1, As100201SwitchSettings.MinPhaseDegrees, As100201SwitchSettings.MaxPhaseDegrees);

        AddSetting(panel, 0, 0, "PC 位相1", _pcSinglePhaseInput, "°");
        AddSetting(panel, 0, 3, "PC 位相2", _pcDoublePhaseInput, "°");
        AddSetting(panel, 1, 0, "CR 位相1", _crSinglePhaseInput, "°");
        AddSetting(panel, 1, 3, "CR 位相2", _crDoublePhaseInput, "°");

        _resetSwitchSettingsButton.AutoSize = true;
        _resetSwitchSettingsButton.Text = "初期値に戻す";
        _resetSwitchSettingsButton.Margin = new Padding(16, 2, 0, 2);
        _resetSwitchSettingsButton.Click += (_, _) => ApplySwitchSettings(As100201SwitchSettings.Defaults, "初期値に戻しました");
        var outer = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, WrapContents = false };
        outer.Controls.Add(panel);
        outer.Controls.Add(_resetSwitchSettingsButton);

        group.Controls.Add(outer);
        return group;
    }

    private void ConfigureNumber(NumericUpDown input, int decimals, decimal increment, decimal minimum, decimal maximum)
    {
        input.DecimalPlaces = decimals;
        input.Increment = increment;
        input.Minimum = minimum;
        input.Maximum = maximum;
        input.Width = 70;
        input.TextAlign = HorizontalAlignment.Right;
        input.Margin = new Padding(0, 3, 4, 3);
        input.ValueChanged += SwitchSettingInputChanged;
    }

    private static void AddSetting(TableLayoutPanel panel, int row, int column, string caption, NumericUpDown input, string unit)
    {
        panel.Controls.Add(new Label
        {
            AutoSize = true,
            Text = caption,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(column == 0 ? 0 : 16, 6, 6, 0)
        }, column, row);
        panel.Controls.Add(input, column + 1, row);
        panel.Controls.Add(new Label { AutoSize = true, Text = unit, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 0) }, column + 2, row);
    }

    // 設定値を入力欄に出す(ValueChanged での保存はしない)。
    private void ShowSwitchSettings()
    {
        _showingSwitchSettings = true;
        try
        {
            _pcSinglePhaseInput.Value = _switchSettings.PcSinglePhaseDegrees;
            _pcDoublePhaseInput.Value = _switchSettings.PcDoublePhaseDegrees;
            _crSinglePhaseInput.Value = _switchSettings.CrSinglePhaseDegrees;
            _crDoublePhaseInput.Value = _switchSettings.CrDoublePhaseDegrees;
        }
        finally { _showingSwitchSettings = false; }
    }

    private void SwitchSettingInputChanged(object? sender, EventArgs e)
    {
        if (_showingSwitchSettings) return;
        ApplySwitchSettings(_switchSettings with
        {
            PcSinglePhaseDegrees = (int)_pcSinglePhaseInput.Value,
            PcDoublePhaseDegrees = (int)_pcDoublePhaseInput.Value,
            CrSinglePhaseDegrees = (int)_crSinglePhaseInput.Value,
            CrDoublePhaseDegrees = (int)_crDoublePhaseInput.Value,
        }, null);
    }

    private void ApplySwitchSettings(As100201SwitchSettings settings, string? reason)
    {
        var previous = _switchSettings;
        _switchSettings = settings;
        ShowSwitchSettings();
        if (settings == previous && reason is null) return;

        AppendLog($"位相の設定{(reason is null ? "を変更" : "：" + reason)}：" +
            $"PC 位相1 {settings.PcSinglePhaseDegrees}°・位相2 {settings.PcDoublePhaseDegrees}°、" +
            $"CR 位相1 {settings.CrSinglePhaseDegrees}°・位相2 {settings.CrDoublePhaseDegrees}°（次に押したときから効きます）");
        // Tab5 のボタンの名前を変える。
        SendPanelState();

        try
        {
            string path = _switchSettingsStore.Save(settings);
            if (path == _switchSettingsStore.FallbackPath)
                AppendLog("exe の隣に設定ファイルを書けないため、次へ保存しました：" + path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppendLog($"位相の設定を保存できません（アプリを閉じると元に戻ります）：{ex.Message}");
        }
    }
}
