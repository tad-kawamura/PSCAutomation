namespace PSCAutomation.Apps.AS110AllCheck;

internal sealed partial class MainForm : Form
{
    private bool _repeatConfirmationShown;
    private bool NeedsRepeatConfirmation => _flow.Mode == TestMode.Production && _flow.HasTransfer && _lastExcelPass;

    private void CloseJudgmentNotice()
    {
        _saveNotice?.Close();
        _saveNotice = null;
    }

    private void ShowJudgmentNotice()
    {
        if (_closeRequested || _repeatConfirmationShown || !_flow.HasTransfer || _reviewStage || _flow.Mode == TestMode.Motion) return;
        var displayTimer=System.Diagnostics.Stopwatch.StartNew();
        CloseJudgmentNotice();
        string result = _lastExcelPass ? "合格" : "不合格";
        string guide = _lastExcelPass && _flow.Mode == TestMode.Production
            ? "M5StackまたはPC画面の「保存」を押してください。"
            : "M5StackまたはPC画面からデータ調整・Excel転送できます。";
        var notice = new Form
        {
            Text = result, ClientSize = new Size(480, 110),
            StartPosition = FormStartPosition.CenterScreen, ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false,
            MinimizeBox = false, TopMost = true, KeyPreview = true
        };
        notice.Controls.Add(new Label
        {
            Text = result, Font = new Font(Font.FontFamily, 24, FontStyle.Bold),
            ForeColor = _lastExcelPass ? Color.DarkGreen : Color.Firebrick,
            AutoSize = true, Location = new Point(15, 12)
        });
        notice.Controls.Add(new Label { Text = guide, AutoSize = true, Location = new Point(15, 70) });
        // 自動で閉じるのでOKボタンは置かない。表示中のEnterはExcelへ渡さず、ここで閉じる。
        notice.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true;
            notice.Close();
            ShowTransferredExcel();
        };
        notice.FormClosed += (_, _) => { if (ReferenceEquals(_saveNotice, notice)) _saveNotice = null; };
        _saveNotice = notice;
        // モデルレスにしてPCの操作ボタンとM5Stackからも閉じられるようにする。
        notice.Show(this);
        // Showで検査アプリがExcelより上へ来る場合も、背後の判定シートを表示し直す。
        ShowTransferredExcel();
        notice.Update();
        AppendLog($"Excel転送時間：合否ポップアップ表示処理（Excel前面表示を含む）={displayTimer.Elapsed.TotalMilliseconds:F0}ms");
        // データ調整・データ取りとも、OKを押さなくても閉じてExcelの判定シートへ戻す。
        StartJudgmentNoticeAutoClose(notice);
    }

    // 合否ポップアップを表示しておく時間。過ぎると自動で閉じてExcelを前面に出す。
    private const int JudgmentNoticeDisplayMilliseconds = 400;

    private void StartJudgmentNoticeAutoClose(Form notice)
    {
        var timer = new System.Windows.Forms.Timer { Interval = JudgmentNoticeDisplayMilliseconds };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // OK・操作ボタンで先に閉じた、または別の表示に置き換わった場合は何もしない。
            if (notice.IsDisposed || !ReferenceEquals(_saveNotice, notice)) return;
            notice.Close();
            ShowTransferredExcel();
        };
        notice.FormClosed += (_, _) => timer.Dispose();
        timer.Start();
    }

    private void ActivateJudgmentNotice(Form notice, Control input)
    {
        if (!WindowActivation.TryActivate(notice, input))
            AppendLog("ポップアップの入力先を切り替えられませんでした。");
    }

    private bool ConfirmRepeatAfterPass(string action)
    {
        if (_repeatConfirmationShown) return false;
        CloseJudgmentNotice();
        if (!NeedsRepeatConfirmation) return true;
        long revision = _flow.Revision;
        _repeatConfirmationShown = true;
        try
        {
            using var dialog = new Form
            {
                Text = "再実行の確認", ClientSize = new Size(440, 145),
                StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false,
                MinimizeBox = false, TopMost = true
            };
            dialog.Controls.Add(new Label
            {
                Text = $"合格しているけど、またやるの？\r\n「{action}」をもう一度実行しますか？",
                AutoSize = true, Location = new Point(20, 20)
            });
            var yes = new Button { Text = "はい", DialogResult = DialogResult.Yes, Location = new Point(225, 95) };
            var no = new Button { Text = "いいえ", DialogResult = DialogResult.No, Location = new Point(335, 95) };
            dialog.Controls.AddRange([yes, no]);
            dialog.AcceptButton = no;
            dialog.CancelButton = no;
            dialog.Shown += (_, _) => ActivateJudgmentNotice(dialog, no);
            return dialog.ShowDialog(this) == DialogResult.Yes &&
                _flow.Mode == TestMode.Production && _flow.Revision == revision && _flow.HasTransfer;
        }
        finally { _repeatConfirmationShown = false; }
    }
}
