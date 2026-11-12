namespace PSCAutomation.Apps.AS110AllCheck;

// 漏れ試験中の判定画面(SPEC「漏れ試験の自動判定」10)。LOGGER の上に最前面で出し、現在値・最大値・差を読み出すたびに更新する。
// 試験中は「合格」を出し続け、不合格・判定できないが決まったらその文字に替える。結果を出してから 0.5 秒で閉じる。
// 入力先(キーボード)は奪わない(LOGGER を前に出した後に出すため)。持ち主(メイン画面)は付けない(付けるとメイン画面も一緒に前に出る)。
internal sealed class LeakJudgeWindow : Form
{
    public const int ResultDisplayMilliseconds = 500;

    private readonly Label _resultLabel = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Top, Height = 90 };
    private readonly Label _valuesLabel = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Top, Height = 130 };
    private readonly Label _infoLabel = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Top, Height = 34 };
    private readonly double _threshold;
    private readonly int _seconds;
    private string _shown = "";
    private bool _closing;

    public LeakJudgeWindow(int seconds, double threshold, Font baseFont)
    {
        _seconds = seconds;
        _threshold = threshold;
        Text = "漏れ試験 判定";
        ClientSize = new Size(460, 270);
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        ControlBox = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.White;
        _resultLabel.Font = new Font(baseFont.FontFamily, 40, FontStyle.Bold);
        _valuesLabel.Font = new Font(baseFont.FontFamily, 20, FontStyle.Regular);
        _infoLabel.Font = new Font(baseFont.FontFamily, 11, FontStyle.Regular);
        _valuesLabel.Padding = _infoLabel.Padding = new Padding(16, 0, 0, 0);
        // Dock=Top は後から足したものほど上に並ぶ。
        Controls.AddRange([_infoLabel, _valuesLabel, _resultLabel]);
        // 画面の上の中央(LOGGER のグラフの上部が隠れる位置)。
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + 40);
        ShowResult(LeakOutcome.Pass);
        ShowValues(double.NaN, double.NaN, double.NaN, TimeSpan.Zero);
    }

    protected override bool ShowWithoutActivation => true;

    // TopMost プロパティだけだと表示のときに入力先を奪うことがあるので、作るときに最前面・入力先にならない窓にする。
    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= 0x00000008 | 0x08000000; // WS_EX_TOPMOST | WS_EX_NOACTIVATE
            return p;
        }
    }

    // 試験中も「合格」を出し続ける(人の指示)。決まったら「不合格」「判定できません」に替える。
    public void ShowResult(LeakOutcome outcome)
    {
        if (IsDisposed) return;
        _resultLabel.Text = outcome switch { LeakOutcome.Fail => "不合格", LeakOutcome.Unmeasurable => "判定できません", _ => "合格" };
        _resultLabel.ForeColor = outcome switch { LeakOutcome.Fail => Color.Firebrick, LeakOutcome.Unmeasurable => Color.DarkOrange, _ => Color.DarkGreen };
        _resultLabel.Font = new Font(_resultLabel.Font.FontFamily, outcome == LeakOutcome.Unmeasurable ? 30 : 40, FontStyle.Bold);
        // 打ち切りの停止処理(発振器・LOGGER)より先に描く。
        if (Visible) Update();
    }

    public void ShowValues(double latest, double maximum, double difference, TimeSpan elapsed)
    {
        if (IsDisposed) return;
        string text = $"現在値　{LeakJudge.Format(latest)} kPa\r\n最大値　{LeakJudge.Format(maximum)} kPa\r\n差　　　{LeakJudge.Format(difference)} kPa";
        // 同じ文字なら描き直さない(読み出しは 10 ms ごと)。
        if (text != _shown) { _shown = text; _valuesLabel.Text = text; }
        double remaining = Math.Max(0, _seconds - elapsed.TotalSeconds);
        _infoLabel.Text = $"しきい値 {LeakJudge.Format(_threshold, 1)} kPa　残り {remaining:F1} 秒 / {_seconds} 秒";
    }

    // 結果を出して閉じ始めた(CloseAfterResult を呼んだ)。
    public bool ResultShown => _closing;

    // 結果を出したまま 0.5 秒後に閉じる。
    public void CloseAfterResult()
    {
        if (IsDisposed || _closing) return;
        _closing = true;
        var timer = new System.Windows.Forms.Timer { Interval = ResultDisplayMilliseconds };
        timer.Tick += (_, _) => { timer.Stop(); timer.Dispose(); if (!IsDisposed) Close(); };
        timer.Start();
    }
}
