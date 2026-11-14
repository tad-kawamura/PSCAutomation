namespace PSCAutomation.Apps.AS110AllCheck;

// 作業用の Excel が書き換えられているときに、書き換えたところを見せてパスワードを聞く画面(2026-11-14、全体見直し・区切り 5。WorkbookEdits.cs)。
// パスワードが合ったときだけ「このまま保存する」で閉じる(DialogResult.OK)。空・違うパスワードでは閉じず、赤字で知らせる。
// Esc・×・「保存しない」は保存しない。LOGGER・Excel の後ろに隠れないよう最前面で出す。
internal sealed class WorkbookEditDialog : Form
{
    public TextBox PasswordBox { get; } = new() { UseSystemPasswordChar = true, Width = 220, TabIndex = 0 };
    public Button SaveButton { get; } = new() { Text = "このまま保存する", Size = new Size(160, 36), TabIndex = 1 };
    public Button CancelSaveButton { get; } = new() { Text = "保存しない", DialogResult = DialogResult.Cancel, Size = new Size(120, 36), TabIndex = 2 };
    public Label ErrorLabel { get; } = new() { AutoSize = true, ForeColor = Color.Red };

    public WorkbookEditDialog(string serial, IReadOnlyList<WorkbookEdit> edits, Font baseFont)
    {
        Text = "作業用 Excel が書き換えられています";
        Font = baseFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        const int width = 620;
        var guide = new Label
        {
            Location = new Point(20, 16), AutoSize = true, MaximumSize = new Size(width - 40, 0),
            Text = $"S/N {serial} の作業用 Excel で、測定値のほかに {edits.Count} か所が雛形から書き換えられています。\r\n" +
                   "書き換えたまま保存するなら、パスワードを入れてください（保存先の「" + WorkbookEdits.RecordFileName + "」に残します）。\r\n" +
                   "書き換えはこの 1 台だけに効きます（次の番号では雛形に戻ります）。今後すべてに効かせるなら、雛形フォルダーの Excel 雛形を直してください。\r\n" +
                   "心当たりが無ければ「保存しない」を押し、Excel で作業用のブックを閉じてから「Excel転送」をやり直してください（雛形から作り直します）。",
        };
        Size guideSize = guide.GetPreferredSize(new Size(width - 40, 0));
        var list = new TextBox
        {
            Location = new Point(20, guide.Top + guideSize.Height + 10), Size = new Size(width - 40, 170), Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical, TabStop = false, BackColor = SystemColors.Window, Text = WorkbookEdits.Describe(edits, 200),
        };
        var passwordLabel = new Label { Text = "パスワード", AutoSize = true, Location = new Point(20, list.Bottom + 16) };
        PasswordBox.Location = new Point(120, list.Bottom + 12);
        ErrorLabel.Location = new Point(PasswordBox.Right + 12, list.Bottom + 16);
        int top = PasswordBox.Bottom + 16;
        CancelSaveButton.Location = new Point(width - 20 - CancelSaveButton.Width, top);
        SaveButton.Location = new Point(CancelSaveButton.Left - 12 - SaveButton.Width, top);
        ClientSize = new Size(width, top + SaveButton.Height + 16);
        Controls.AddRange([guide, list, passwordLabel, PasswordBox, ErrorLabel, SaveButton, CancelSaveButton]);
        SaveButton.Click += (_, _) => TryAccept();
        AcceptButton = SaveButton;
        CancelButton = CancelSaveButton;
        ActiveControl = PasswordBox;
    }

    // パスワードが合えば OK で閉じる。合わなければ閉じずに知らせる。
    public bool TryAccept()
    {
        if (WorkbookEdits.PasswordMatches(PasswordBox.Text))
        {
            DialogResult = DialogResult.OK;
            Close();
            return true;
        }
        ErrorLabel.Text = PasswordBox.Text.Length == 0 ? "パスワードを入れてください" : "パスワードが違います";
        PasswordBox.SelectAll();
        PasswordBox.Focus();
        return false;
    }
}
