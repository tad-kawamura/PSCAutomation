namespace PSCAutomation.Apps.AS110AllCheck;

// 最初の画面(2026-11-19、人の指示。SPEC「最初の画面を「校正」「測定」の 2 つのボタンにする」)。
// 接続確認のあと、モード欄は出さず、大きなボタン「校正」「測定」の 2 つだけを出す(いきなり校正画面が開くと作業者が迷うため)。
//   「校正」: 今の校正画面(スケーリング)を開く。今日の校正が済んでいても押せる(やり直し)。NR-X は校正しないので押せない。
//   「測定」: 今のモード選択などの画面へ進む。NR-500 でその日の校正がまだなら押せない(灰色)。
// 「適用」のあとは最初の画面に戻り、「測定」が押せるようになる(自動では測定の画面へ進まない。AI の案 4)。
// 測定の画面から最初の画面へ戻るボタンは作らない(校正し直しは測定の画面の「校正し直す」。AI の案 5)。
// 校正画面は自動では開かない。測定の画面に入ったあとで日付が変わったときだけ、今までどおり校正画面を開く(AI の案 6)。
internal sealed partial class MainForm : Form
{
    private readonly TableLayoutPanel _homePanel = new();
    private readonly Button _homeCalibrateButton = new();
    private readonly Button _homeMeasureButton = new();
    private readonly Label _homeCalibrateLabel = new();
    private readonly Label _homeMeasureLabel = new();
    // 「測定」を押して測定の画面に入った(戻らない)。
    private bool _measurementEntered;
    // モード欄・校正欄などを出す(「測定」を押した後。モードを選んであれば測定の画面に入っている)。
    private bool MeasurementScreen => _measurementEntered || _modeSelected;
    // 「測定」を押せる: 接続確認が済み、NR-500 なら今日の校正値がある(NR-X は校正しない)。
    private bool CanEnterMeasurement => ConnectionsReady && !_checkingConnections && !_operationInProgress &&
        (!CalibrationSupported || ValidTodayCalibration is not null);

    private Control BuildHomePanel()
    {
        _homePanel.AutoSize = true;
        _homePanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _homePanel.Dock = DockStyle.Fill;
        _homePanel.ColumnCount = 2;
        _homePanel.RowCount = 2;
        _homePanel.Margin = new Padding(0, 4, 0, 10);
        _homePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _homePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _homePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _homePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var font = new Font(Font.FontFamily, 22, FontStyle.Bold);
        foreach (var (button, text, handler, column) in new (Button, string, EventHandler, int)[]
        {
            (_homeCalibrateButton, "校正", (_, _) => HomeCalibrateClicked(), 0),
            (_homeMeasureButton, "測定", (_, _) => HomeMeasureClicked(), 1),
        })
        {
            button.Text = text;
            button.Font = font;
            button.Size = new Size(220, 140);
            button.Margin = new Padding(0, 0, 24, 6);
            button.Click += handler;
            _homePanel.Controls.Add(button, column, 0);
        }
        foreach (var (label, column) in new[] { (_homeCalibrateLabel, 0), (_homeMeasureLabel, 1) })
        {
            label.AutoSize = true;
            label.MaximumSize = new Size(220, 0);
            label.Margin = new Padding(0, 0, 24, 0);
            label.Font = new Font(Font.FontFamily, 10);
            _homePanel.Controls.Add(label, column, 1);
        }
        return _homePanel;
    }

    private void HomeCalibrateClicked()
    {
        if (MeasurementScreen) return;
        CalibrateToday();
    }

    private void HomeMeasureClicked()
    {
        if (MeasurementScreen) return;
        if (!CanEnterMeasurement)
        {
            // 日付が変わった(押せる表示のまま夜を越えた)など。
            UpdateControls();
            return;
        }
        _measurementEntered = true;
        AppendLog("測定：モードを選ぶ画面に進みました。");
        UpdateControls();
    }

    private void UpdateHomeControls()
    {
        bool home = !MeasurementScreen;
        _homePanel.Visible = home;
        _modeSelectionPanel.Visible = !home;
        if (!home) return;
        bool idle = ConnectionsReady && !_checkingConnections && !_operationInProgress;
        var today = ValidTodayCalibration;
        bool supported = CalibrationSupported;
        _homeCalibrateButton.Enabled = idle && supported;
        _homeMeasureButton.Enabled = CanEnterMeasurement;
        // 押せる「測定」は緑で目立たせる(校正が済んだことが分かるように)。
        _homeMeasureButton.BackColor = _homeMeasureButton.Enabled ? Color.Honeydew : SystemColors.Control;
        _homeMeasureButton.UseVisualStyleBackColor = !_homeMeasureButton.Enabled;
        _homeCalibrateLabel.ForeColor = today is null && supported ? Color.Red : Color.DarkGreen;
        _homeCalibrateLabel.Text = !ConnectionsReady ? "接続確認が済むと押せます"
            : !supported ? "NR-X は校正しません"
            : today is null ? "今日の校正がまだです"
            : $"今日の校正：済み（{today.CalibratedAt.LocalDateTime:HH:mm}）";
        _homeMeasureLabel.ForeColor = SystemColors.ControlText;
        _homeMeasureLabel.Text = !ConnectionsReady ? "接続確認が済むと押せます"
            : _homeMeasureButton.Enabled ? "モードを選んで測定します"
            : "今日の校正が済むと押せます";
    }

    // 最初の画面の「次にすること」(押せる「測定」があれば緑、校正がまだなら赤)。最初の画面でなければ false。
    private bool UpdateHomeNextStep()
    {
        if (MeasurementScreen || !ConnectionsReady || _checkingConnections) return false;
        if (CalibrationSupported && ValidTodayCalibration is null)
        {
            _nextStepLabel.ForeColor = Color.Red;
            _nextStepLabel.Text = "次にすること：「校正」を押して今日の校正をしてください（済むと「測定」を押せます）";
        }
        else
        {
            _nextStepLabel.ForeColor = Color.Green;
            _nextStepLabel.Text = "次にすること：「測定」を押してください" + (CalibrationSupported ? "（校正し直すなら「校正」）" : "");
        }
        return true;
    }
}
