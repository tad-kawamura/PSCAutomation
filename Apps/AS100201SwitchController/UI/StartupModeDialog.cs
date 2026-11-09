namespace PSCAutomation.Apps.AS100201SwitchController;

// 起動時に「通常モード / 学習モード」を選ぶ窓(SPEC_調整操作の記録.md「モード」)。
// 初期選択は通常モード。× で閉じたときも通常モード(今までと同じ動きになる)。途中で変えるには起動し直す。
internal sealed class StartupModeDialog : Form
{
    private readonly RadioButton _normal = new();
    private readonly RadioButton _learning = new();

    public As100201StartupMode SelectedMode { get; private set; } = As100201StartupMode.Normal;

    public StartupModeDialog()
    {
        Text = As100201Application.WindowTitle + "　モードの選択";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(18);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Fill
        };

        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font(Font.FontFamily, 12, FontStyle.Bold),
            Text = "起動するモードを選んでください",
            Margin = new Padding(0, 0, 0, 12)
        });

        _normal.Text = "通常モード（今までどおり：スイッチで位相の出し直しと測定の開始・停止）";
        _learning.Text = "学習モード（タッチパネルで調整操作を記録し、測定のたびに xdt と台帳を保存）";
        foreach (var radio in new[] { _normal, _learning })
        {
            radio.AutoSize = true;
            radio.Margin = new Padding(4, 4, 4, 4);
            layout.Controls.Add(radio);
        }
        _normal.Checked = true;

        layout.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = "学習モードはタッチパネル（M5Stack Tab5、USB）がつながっていないと動きません。\r\n" +
                "途中でモードを変えるにはアプリを起動し直してください。",
            Margin = new Padding(4, 8, 4, 12)
        });

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        var ok = new Button { Text = "開始", AutoSize = true, MinimumSize = new Size(120, 36), DialogResult = DialogResult.OK };
        ok.Click += (_, _) => SelectedMode = _learning.Checked ? As100201StartupMode.Learning : As100201StartupMode.Normal;
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons);
        AcceptButton = ok;

        Controls.Add(layout);
    }

    // 窓を出して選ばれたモードを返す。× で閉じたら通常モード。
    public static As100201StartupMode Ask()
    {
        using var dialog = new StartupModeDialog();
        return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedMode : As100201StartupMode.Normal;
    }
}
