namespace PSCAutomation.Apps.AS110AllCheck;

// 既定のボタンが「いいえ」の確認画面。Enter・スペース・Esc・× のどれでも進まず、「はい」を選んだときだけ進める。
// 使う所: アプリ終了の確認、モード変更の確認、保護の解除での出荷データの消去、校正画面。
// LOGGER・Excel の後ろに隠れないよう最前面で出す。
// もとは漏れ判定の「人の判断で合格」の確認(LeakOverrideDialog、2026-11-13)。その機能は 2026-11-14 に人の決定でなくし、名前を変えた。
internal sealed class DefaultNoDialog : Form
{
    public Button YesButton { get; } = new() { Text = "はい", DialogResult = DialogResult.Yes, Size = new Size(110, 36), TabIndex = 1 };
    public Button NoButton { get; } = new() { Text = "いいえ", DialogResult = DialogResult.No, Size = new Size(110, 36), TabIndex = 0 };

    public DefaultNoDialog(string message, Font baseFont, string title)
    {
        Text = title;
        Font = baseFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        var label = new Label { Text = message, AutoSize = true, MaximumSize = new Size(560, 0), Location = new Point(20, 18) };
        Size text = label.GetPreferredSize(new Size(560, 0));
        int width = Math.Max(text.Width + 40, 2 * NoButton.Width + 60);
        int top = label.Top + text.Height + 20;
        NoButton.Location = new Point(width - 20 - NoButton.Width, top);
        YesButton.Location = new Point(NoButton.Left - 12 - YesButton.Width, top);
        ClientSize = new Size(width, top + NoButton.Height + 16);
        Controls.AddRange([label, NoButton, YesButton]);
        AcceptButton = NoButton;
        CancelButton = NoButton;
        ActiveControl = NoButton;
        DisplayScale.Apply(this);
    }
}
