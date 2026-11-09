namespace PSCAutomation.Apps.AS100201SwitchController;

// 2 回押し(試用版、2026-11-09): 待ち時間と位相を画面で変え、設定ファイルに残す。
// 値が決まったらこの入力欄を外して固定値に戻す予定(人の方針)。
internal sealed partial class MainForm : Form
{
    private readonly NumericUpDown _doubleWaitInput = new();
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
            Text = "2回押し（試用）　PC_go / CR_go を素早く2回押すと、2回押しの位相で出し直します",
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
            RowCount = 3
        };
        for (int i = 0; i < panel.ColumnCount; i++) panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        ConfigureNumber(_doubleWaitInput, 2, 0.01m,
            As100201SwitchSettings.MinDoubleWaitMilliseconds / 1000m, As100201SwitchSettings.MaxDoubleWaitMilliseconds / 1000m);
        foreach (var input in new[] { _pcSinglePhaseInput, _pcDoublePhaseInput, _crSinglePhaseInput, _crDoublePhaseInput })
            ConfigureNumber(input, 0, 1, As100201SwitchSettings.MinPhaseDegrees, As100201SwitchSettings.MaxPhaseDegrees);

        AddSetting(panel, 0, 0, "待ち時間（離してから）", _doubleWaitInput, "秒");
        AddSetting(panel, 1, 0, "PC_go　1回押し", _pcSinglePhaseInput, "°");
        AddSetting(panel, 1, 3, "2回押し", _pcDoublePhaseInput, "°");
        AddSetting(panel, 2, 0, "CR_go　1回押し", _crSinglePhaseInput, "°");
        AddSetting(panel, 2, 3, "2回押し", _crDoublePhaseInput, "°");

        _resetSwitchSettingsButton.AutoSize = true;
        _resetSwitchSettingsButton.Text = "初期値に戻す";
        _resetSwitchSettingsButton.Margin = new Padding(16, 2, 0, 2);
        _resetSwitchSettingsButton.Click += (_, _) => ApplySwitchSettings(As100201SwitchSettings.Defaults, "初期値に戻しました");
        panel.Controls.Add(_resetSwitchSettingsButton, 3, 0);
        panel.SetColumnSpan(_resetSwitchSettingsButton, 3);

        group.Controls.Add(panel);
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
            _doubleWaitInput.Value = _switchSettings.DoubleWaitMilliseconds / 1000m;
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
        ApplySwitchSettings(new As100201SwitchSettings
        {
            DoubleWaitMilliseconds = (int)Math.Round(_doubleWaitInput.Value * 1000m),
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

        string Phases(As100201SwitchSettings s) =>
            $"PC_go {s.PcSinglePhaseDegrees}°/{s.PcDoublePhaseDegrees}°、CR_go {s.CrSinglePhaseDegrees}°/{s.CrDoublePhaseDegrees}°";
        if (settings.DoubleWaitMilliseconds != previous.DoubleWaitMilliseconds || reason is not null)
        {
            try { _arduino.SetDoubleWait(settings.DoubleWaitMilliseconds); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or UnauthorizedAccessException)
            {
                // 送れなかったときは、次の接続確認の後に送り直される。
                AppendLog($"2回押しの待ち時間を Arduino へ送れませんでした（再接続後に送ります）：{ex.Message}");
            }
        }
        AppendLog($"2回押しの設定{(reason is null ? "を変更" : "：" + reason)}：待ち時間 {settings.DoubleWaitMilliseconds / 1000.0:F2} 秒、{Phases(settings)}（1回押し/2回押し）");

        try
        {
            string path = _switchSettingsStore.Save(settings);
            if (path == _switchSettingsStore.FallbackPath)
                AppendLog("exe の隣に設定ファイルを書けないため、次へ保存しました：" + path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppendLog($"2回押しの設定を保存できません（アプリを閉じると元に戻ります）：{ex.Message}");
        }
    }

    private void ArduinoDoubleWaitApplied(int milliseconds)
    {
        PostToUi(() =>
        {
            AppendLog($"2回押しの待ち時間 {milliseconds / 1000.0:F2} 秒を Arduino に反映");
            if (milliseconds != _switchSettings.DoubleWaitMilliseconds)
                AppendLog($"Arduino の待ち時間が画面の値（{_switchSettings.DoubleWaitMilliseconds / 1000.0:F2} 秒）と違います。");
        });
    }
}
