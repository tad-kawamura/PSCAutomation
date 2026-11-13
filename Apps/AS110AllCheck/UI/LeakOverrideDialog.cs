namespace PSCAutomation.Apps.AS110AllCheck;

// 漏れ判定が不合格・判定できなかった後に、人の判断で合格にするかを聞く確認画面(SPEC「漏れ試験の自動判定」5)。
// 既定のボタンは「いいえ」。Enter・スペース・Esc・× のどれでも合格にならず、「はい」を選んだときだけ合格にする
// (2026-11-13、全体見直し。以前は共通のメッセージボックスで既定が「はい」になり、Enter 1 回で不合格のものが合格になった)。
// LOGGER・Excel の後ろに隠れないよう最前面で出す(M5Stack の C ボタンから出ることもある)。
internal sealed class LeakOverrideDialog : Form
{
    public Button YesButton { get; } = new() { Text = "はい", DialogResult = DialogResult.Yes, Size = new Size(110, 36), TabIndex = 1 };
    public Button NoButton { get; } = new() { Text = "いいえ", DialogResult = DialogResult.No, Size = new Size(110, 36), TabIndex = 0 };

    public LeakOverrideDialog(string message, Font baseFont)
    {
        Text = "漏れ判定：人の判断で合格";
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
    }
}
