using System.Globalization;

namespace PSCAutomation.Apps.AS110AllCheck;

// 校正の読み取りの画面(段階 1。2026-11-13、SPEC「校正の自動読み取り」。2026-11-18 に 2 つに分けた)。
// 「マルチメータで読み取る」は ①②、「GC16 で読み取る」は ③④ を、開くとすぐ順に行う。
// 人への指示(ボールバルブの切り替え・マルチメータの値・レギュレーター)はこの画面の中に出す。
// 読み取れたら閉じて、校正画面の欄に入れる(適用は校正画面で人が押す)。
// 2026-11-20: 人が押す所が分かりにくかったので(人の指摘)、いまどの段か(①→② / ③→④)を上に出し、指示の枠は
// 人の操作を待っているあいだだけ色を付けて「あなたの操作です」と出す。自動で進んでいるあいだは「操作は要りません」と出す。
// いつでも「中止して手入力に戻る」で止められる。止まったときは後片付け(WF1973 の出力停止・GC16 を RM,0・7351E を離す・ボールバルブを測定の向き)をしてから、
// 校正画面の手入力に戻る(実機で確かめるまで、自動がうまく行かなくても手入力で必ず測定へ進める。人の指示)。
// 2026-11-24: 止まったあと、その場で「もう一度読み取る」を押せるようにした(現場で GC16 の USB を挿し忘れて止まったとき、
// いきなり手入力を促された。人の指摘)。挿して押せば、この読み取りの最初の段(① か ③)からやり直す。
// M5Stack の通信が切れた・WF1973 の出力を止められなかった・接続確認が済んでいないときは、この画面ではつなぎ直せないので出さず、
// 校正画面を閉じて「確認」を押すよう案内する。
internal sealed class AutoCalibrationDialog : Form, IAutoCalibrationOperator
{
    public const string RetryLabel = "もう一度読み取る";
    private readonly AutoCalibrationKind _kind;
    private readonly ProductModel? _model;
    private readonly Func<IAutoCalibrationOperator, AutoCalibrationProcedure> _createProcedure;
    private readonly CalibrationInProgressMarker _marker;
    private readonly Action<string> _log;
    // やり直してよいか(M5Stack・発振器・LOGGER の接続が今も使えるか)。止まったときに聞く。
    private readonly Func<bool> _canRetry;
    // 中止すると使えなくなるので、やり直すときに作り直す。
    private CancellationTokenSource _cancel = new();
    private readonly Label _statusLabel = new();
    private readonly Label _pressureLabel = new();
    private readonly Label _promptHeading = new();
    private readonly FlowLayoutPanel _promptPanel = new();
    private readonly Label _promptLabel = new();
    // いまどの段か(この読み取りで行う 2 つの段)。
    private readonly AutoCalibrationStage[] _stages;
    private readonly Label[] _stageLabels;
    private AutoCalibrationStage? _currentStage;
    // 人の操作を待っているあいだの、指示の枠の色。
    private static readonly Color WaitingColor = Color.FromArgb(255, 224, 130);
    // 指示の枠の幅と文字の大きさ(2026-11-20、人の指示: 指示の文字はできるだけ大きく)。
    private const int PanelWidth = 760;
    private const float PromptFontSize = 16, PromptHeadingFontSize = 18;
    private readonly Label _promptError = new();
    private readonly FlowLayoutPanel _inputPanel = new();
    private readonly TextBox _inputBox = new();
    private readonly FlowLayoutPanel _choicesPanel = new();
    private readonly TextBox _logBox = new();
    private readonly Button _stopButton = new();
    private bool _running;
    private bool _closeWhenDone;

    // model は「GC16 で読み取る」のときの型式(目標の供給圧力)。「マルチメータで読み取る」は null。
    // canRetry を渡さないときは、止まったあといつでもやり直せる(機器なしの確認用)。
    public AutoCalibrationDialog(AutoCalibrationKind kind, ProductModel? model, Func<IAutoCalibrationOperator, AutoCalibrationProcedure> createProcedure,
        CalibrationInProgressMarker marker, Action<string> log, Func<bool>? canRetry = null)
    {
        if (kind == AutoCalibrationKind.Pressure) ArgumentNullException.ThrowIfNull(model);
        _kind = kind;
        _model = model;
        _createProcedure = createProcedure;
        _marker = marker;
        _log = log;
        _canRetry = canRetry ?? (() => true);
        string title = kind == AutoCalibrationKind.Current ? "マルチメータで読み取る" : "GC16 で読み取る";
        Text = title + "（校正の自動読み取り）";
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        // 校正画面(最前面)の背後に隠れないようにする。
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var root = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16)
        };
        root.Controls.Add(new Label
        {
            AutoSize = true, Font = new Font(Font.FontFamily, 13, FontStyle.Bold), Text = title, Margin = new Padding(0, 0, 0, 4)
        });
        root.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(PanelWidth, 0), Margin = new Padding(0, 0, 0, 8),
            Text = kind == AutoCalibrationKind.Current
                ? "① 0 mA → ② 30 mA の順に、NR-500 CH1 の電圧（LOGGER）とマルチメータ（7351E）の電流を読み取ります。指示はこの画面に出ます。"
                : $"型式 {model!.Name} ／ 目標の供給圧力 {model.SupplyPressure} kPa\r\n" +
                  "③ 大気圧でゼロ調整 → ④ 供給圧力 の順に、NR-500 CH2 の電圧（LOGGER）と GC16 の圧力を読み取ります。指示はこの画面に出ます。"
        });
        _stages = kind == AutoCalibrationKind.Current
            ? [AutoCalibrationStage.CurrentLow, AutoCalibrationStage.CurrentHigh]
            : [AutoCalibrationStage.PressureLow, AutoCalibrationStage.PressureHigh];
        _stageLabels = new Label[_stages.Length];
        var stageRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
        for (int i = 0; i < _stages.Length; i++)
        {
            if (i > 0) stageRow.Controls.Add(new Label { Text = "→", AutoSize = true, Margin = new Padding(4, 6, 4, 0) });
            _stageLabels[i] = new Label { AutoSize = true, BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(8, 4, 8, 4), Margin = Padding.Empty };
            stageRow.Controls.Add(_stageLabels[i]);
        }
        root.Controls.Add(stageRow);
        UpdateStages(finished: false);
        _statusLabel.AutoSize = true;
        _statusLabel.MaximumSize = new Size(PanelWidth, 0);
        _statusLabel.Font = new Font(Font, FontStyle.Bold);
        _statusLabel.Margin = new Padding(0, 0, 0, 6);
        root.Controls.Add(_statusLabel);
        _pressureLabel.AutoSize = true;
        _pressureLabel.Font = new Font(Font.FontFamily, 16, FontStyle.Bold);
        _pressureLabel.Margin = new Padding(0, 0, 0, 8);
        _pressureLabel.Visible = kind == AutoCalibrationKind.Pressure;
        ShowPressure(null);
        root.Controls.Add(_pressureLabel);
        var prompt = _promptPanel;
        prompt.AutoSize = true;
        prompt.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        prompt.FlowDirection = FlowDirection.TopDown;
        prompt.WrapContents = false;
        prompt.BorderStyle = BorderStyle.FixedSingle;
        prompt.Padding = new Padding(8);
        prompt.Margin = new Padding(0, 0, 0, 8);
        prompt.MinimumSize = new Size(PanelWidth, 0);
        _promptHeading.AutoSize = true;
        _promptHeading.Font = new Font(Font.FontFamily, PromptHeadingFontSize, FontStyle.Bold);
        _promptHeading.Margin = new Padding(0, 0, 0, 6);
        prompt.Controls.Add(_promptHeading);
        _promptLabel.AutoSize = true;
        _promptLabel.Font = new Font(Font.FontFamily, PromptFontSize);
        _inputPanel.Font = _promptLabel.Font;
        _promptError.Font = _promptLabel.Font;
        _promptLabel.MaximumSize = new Size(PanelWidth - 20, 0);
        _promptLabel.Margin = new Padding(0, 0, 0, 6);
        prompt.Controls.Add(_promptLabel);
        _inputPanel.AutoSize = true;
        _inputPanel.FlowDirection = FlowDirection.LeftToRight;
        _inputPanel.Visible = false;
        _inputBox.Width = 180;
        _inputBox.TextAlign = HorizontalAlignment.Right;
        _inputBox.AccessibleName = "マルチメータの電流 (mA)";
        _inputPanel.Controls.AddRange([_inputBox, new Label { Text = "mA", AutoSize = true, Margin = new Padding(3, 6, 3, 3) }]);
        prompt.Controls.Add(_inputPanel);
        _promptError.AutoSize = true;
        _promptError.ForeColor = Color.DarkRed;
        _promptError.Visible = false;
        prompt.Controls.Add(_promptError);
        _choicesPanel.AutoSize = true;
        _choicesPanel.FlowDirection = FlowDirection.LeftToRight;
        prompt.Controls.Add(_choicesPanel);
        root.Controls.Add(prompt);
        root.Controls.Add(new Label { Text = "経過（見るだけ）", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 2) });
        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Size = new Size(PanelWidth, 150);
        _logBox.Margin = new Padding(0, 0, 0, 8);
        root.Controls.Add(_logBox);
        _stopButton.Text = "中止して手入力に戻る";
        _stopButton.AutoSize = true;
        _stopButton.MinimumSize = new Size(160, 36);
        _stopButton.Click += (_, _) => StopOrClose();
        root.Controls.Add(_stopButton);
        Controls.Add(root);
        ClearPrompt();
    }

    public CurrentReadingResult? CurrentResult { get; private set; }
    public PressureReadingResult? PressureResult { get; private set; }
    private bool Succeeded => CurrentResult is not null || PressureResult is not null;
    // 後片付けで WF1973 の出力を止められなかった(呼び出し側で「出力状態不明」にする)。
    public bool OutputStopFailed { get; private set; }
    // 読み取りを始めたか(WF1973 を DC にしたので、呼び出し側で発振器の設定を戻す)。
    public bool Started { get; private set; }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RunProcedureAsync().Forget();
    }

    // 画面が出たら読み取りを始め、終わったら結果に合わせて閉じる。止まったあと「もう一度読み取る」でも呼ぶ。
    private async Task RunProcedureAsync()
    {
        if (_running) return;
        _running = true;
        if (_cancel.IsCancellationRequested)
        {
            _cancel.Dispose();
            _cancel = new CancellationTokenSource();
        }
        _abortReason = null;
        _currentStage = null;
        UpdateStages(finished: false);
        ClearPrompt();
        _stopButton.Text = "中止して手入力に戻る";
        _stopButton.Enabled = true;
        _stopButton.UseVisualStyleBackColor = true;
        AutoCalibrationProcedure? procedure = null;
        string? failure = null;
        try
        {
            try { _marker.Begin(DateTimeOffset.Now, _model?.Name ?? "（電流の読み取り）"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowStatus("校正中の印を書けません（途中でアプリが落ちたとき、次の起動で後片付けの知らせが出ません）：" + ex.Message);
            }
            procedure = _createProcedure(this);
            Started = true;
            if (_kind == AutoCalibrationKind.Current)
            {
                CurrentResult = await procedure.RunCurrentAsync(_cancel.Token);
                UpdateStages(finished: true);
                ShowStatus("読み取りが終わりました：" + As110Calibration.DescribePoints(CurrentResult.Current, "mA"));
            }
            else
            {
                PressureResult = await procedure.RunPressureAsync(_model!, _cancel.Token);
                UpdateStages(finished: true);
                ShowStatus("読み取りが終わりました：" + As110Calibration.DescribePoints(PressureResult.Pressure, "kPa"));
            }
        }
        catch (OperationCanceledException ex)
        {
            failure = _abortReason is { } reason ? reason + "ため、読み取りを中止しました。" :
                ex.InnerException is { } cause ? "中止しました（" + cause.Message + "）。" : "中止しました。";
        }
        catch (Exception ex)
        {
            failure = "読み取りを続けられません：" + ex.Message;
        }
        finally
        {
            // 前の回で止められなかったときは、やり直して止められても「止められなかった」を残す(呼び出し側で出力状態不明にする)。
            OutputStopFailed |= procedure?.OutputStopFailed ?? false;
            _marker.Clear();
            _running = false;
        }
        if (failure is not null) Finish(failure);
        if (Succeeded)
        {
            DialogResult = DialogResult.OK;
            Close();
        }
        else if (_closeWhenDone)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }

    // 中止・失敗: 理由を残し、やり直せるなら「もう一度読み取る」を出す。閉じると校正画面の手入力に戻る。
    private void Finish(string message)
    {
        bool canRetry = !_closeWhenDone && _abortReason is null && !OutputStopFailed && _canRetry();
        ShowStatus(message);
        ShowPressure(null);
        ClearPrompt();
        SetWaiting(true);
        if (canRetry)
        {
            string devices = _kind == AutoCalibrationKind.Current ? "マルチメータ（7351E）の" : "GC16 の";
            string first = AutoCalibrationProcedure.StageName(_stages[0]);
            ShowStatus($"{devices} USB・電源・ケーブルを確かめて「{RetryLabel}」を押すと、{first}からやり直します。" +
                "値を手で入れるときは「閉じて手入力に戻る」を押してください。");
            _promptLabel.Text = message + "\r\n\r\n" +
                $"{devices} USB・電源・ケーブルを確かめて、「{RetryLabel}」を押してください（{first}からやり直します）。\r\n" +
                "値を手で入れるときは、下の「閉じて手入力に戻る」を押してください。";
            AddChoice(RetryLabel, () =>
            {
                // 止まっているあいだに M5Stack などが切れたら、やり直さずに「確認」へ案内する。
                if (!_canRetry())
                {
                    Finish("M5Stack・発振器・LOGGER の接続が使えなくなりました。");
                    return;
                }
                ShowStatus("もう一度読み取ります。");
                RunProcedureAsync().Forget();
            });
            _stopButton.UseVisualStyleBackColor = true;
        }
        else
        {
            const string reconnect = "この画面ではつなぎ直せません。校正画面を閉じてメイン画面の「確認」を押し、もう一度「校正」を押してください" +
                "（校正画面で値を手で入れて「適用」しても、測定へ進めます）。";
            ShowStatus(reconnect);
            _promptLabel.Text = message + "\r\n\r\n" + reconnect + "\r\n下の「閉じて手入力に戻る」を押してください。";
            _stopButton.BackColor = WaitingColor;
            _stopButton.UseVisualStyleBackColor = false;
        }
        _stopButton.Text = "閉じて手入力に戻る";
        _stopButton.Enabled = true;
    }

    // 画面の外で起きた異常(M5Stack の通信切れ)で読み取りを中止する(2026-11-14、全体見直し・区切り 4)。
    // 通信切れの処理は発振器の出力を止めるので、続けると 30 mA が出ていないまま読み取った。後片付けは「中止」と同じ。
    private string? _abortReason;
    public void Abort(string reason)
    {
        if (!_running || _cancel.IsCancellationRequested) return;
        _abortReason = reason;
        _stopButton.Enabled = false;
        _stopButton.Text = "中止しています…";
        _cancel.Cancel();
    }

    private void StopOrClose()
    {
        if (_running)
        {
            _stopButton.Enabled = false;
            _stopButton.Text = "中止しています…";
            _cancel.Cancel();
            return;
        }
        DialogResult = Succeeded ? DialogResult.OK : DialogResult.Cancel;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 読み取り中に × で閉じたときは、中止して後片付けが終わってから閉じる。
        if (_running)
        {
            e.Cancel = true;
            _closeWhenDone = true;
            StopOrClose();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cancel.Dispose();
        base.Dispose(disposing);
    }

    public void ShowStatus(string text)
    {
        _statusLabel.Text = text;
        string line = $"{DateTime.Now:HH:mm:ss} {text}";
        _logBox.AppendText((_logBox.TextLength > 0 ? "\r\n" : "") + line.Replace("\r\n", " "));
        _log(text.Replace("\r\n", " "));
    }

    public void ShowStage(AutoCalibrationStage stage)
    {
        _currentStage = stage;
        UpdateStages(finished: false);
    }

    // 段の表示: 済んだ段は ✓(緑)、いまの段は ▶(太字)、これからの段は灰色。
    private void UpdateStages(bool finished)
    {
        for (int i = 0; i < _stages.Length; i++)
        {
            bool done = finished || (_currentStage is { } current && _stages[i] < current);
            bool now = !finished && _currentStage == _stages[i];
            _stageLabels[i].Text = (done ? "✓ " : now ? "▶ " : "") + AutoCalibrationProcedure.StageName(_stages[i]);
            _stageLabels[i].Font = new Font(Font, now ? FontStyle.Bold : FontStyle.Regular);
            _stageLabels[i].ForeColor = done ? Color.DarkGreen : now ? SystemColors.ControlText : SystemColors.GrayText;
            _stageLabels[i].BackColor = now ? Color.White : Color.Transparent;
        }
    }

    // 指示の枠: 人の操作を待っているあいだは色を付けて知らせ、自動で進んでいるあいだは「操作は要りません」と出す。
    private void SetWaiting(bool waiting)
    {
        _promptPanel.BackColor = waiting ? WaitingColor : SystemColors.Control;
        _promptHeading.Text = waiting ? "▶ あなたの操作です" : "自動で進めています（操作は要りません）";
        _promptHeading.ForeColor = waiting ? SystemColors.ControlText : SystemColors.GrayText;
    }

    public void ShowPressure(double? kilopascals) =>
        _pressureLabel.Text = "GC16：" + (kilopascals is { } v ? v.ToString("0.00", CultureInfo.InvariantCulture) + " kPa" : "--- kPa") +
            (_model is null ? "" : $"（目標 {_model.SupplyPressure} kPa）");

    public Task<bool> ConfirmAsync(string instruction, CancellationToken cancellationToken) =>
        PromptAsync(instruction, [("OK", true), ("中止", false)], cancellationToken);

    public Task NotifyAsync(string instruction) =>
        PromptAsync(instruction, [("OK", true)], CancellationToken.None);

    // ①② マルチメータが見つからないとき、人が表示(mA)を入れる。① は 0 付近・負もありうるので数なら受け付ける。② は 0 より大きい数。
    public async Task<double?> AskActualCurrentAsync(AutoCalibrationStage stage, CancellationToken cancellationToken)
    {
        bool high = stage == AutoCalibrationStage.CurrentHigh;
        var completion = new TaskCompletionSource<double?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ClearPrompt();
        _promptLabel.Text = (high ? "② WF1973 から 30 mA の指令（5 V）を出しています。" : "① WF1973 の出力を 0 mA にしています。") +
            "デジタルマルチメータの表示（mA）を入れて「OK」を押してください。\r\n小数点は . です（例 " + (high ? "29.9" : "0.01") + "）。";
        _inputBox.Text = "";
        _inputPanel.Visible = true;
        SetWaiting(true);
        AddChoice("OK", () =>
        {
            if (double.TryParse(_inputBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
                double.IsFinite(value) && (!high || value > 0))
                completion.TrySetResult(value);
            else
            {
                _promptError.Text = (high ? "0 より大きい数" : "数") + "を入れてください（小数点は .）。";
                _promptError.Visible = true;
            }
        });
        AddChoice("中止", () => completion.TrySetResult(null));
        _inputBox.Focus();
        using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
        {
            try { return await completion.Task; }
            finally { ClearPrompt(); }
        }
    }

    public Task<CurrentMeterMissingChoice> AskCurrentMeterMissingAsync(string message, CancellationToken cancellationToken) =>
        PromptAsync(message, [("もう一度探す", CurrentMeterMissingChoice.Retry), ("電流を手で入れる", CurrentMeterMissingChoice.Manual),
            ("中止", CurrentMeterMissingChoice.Abort)], cancellationToken);

    public Task<bool> WaitRegulatorAdjustedAsync(double targetKilopascals, CancellationToken cancellationToken) =>
        // どこの圧力を合わせるのか迷わないよう「GC16 の表示を」と書く。押したあとはアプリが待って読むことも書く(③ と同じ形。2026-11-25、人の指示)。
        PromptAsync($"レギュレータを回して、GC16 の表示を {targetKilopascals:0.#} kPa にして下さい。\r\n\r\n" +
            "　1. 上の GC16 の値を見ながら、レギュレータを回す\r\n" +
            $"　2. {targetKilopascals:0.#} kPa になったら、下の「設定した」を押す\r\n\r\n" +
            "「設定した」を押すと、値が落ち着くのを待って\r\n読み取りをアプリが自動で行います。",
            [("設定した", true), ("中止", false)], cancellationToken);

    public Task<WaitChoice> AskNotSettledAsync(string message, CancellationToken cancellationToken) =>
        PromptAsync(message, [("待ち続ける", WaitChoice.KeepWaiting), ("この段をやり直す", WaitChoice.RedoStage), ("中止", WaitChoice.Abort)], cancellationToken);

    public Task<DeviationChoice> AskDeviationAsync(string message, string readjustLabel, CancellationToken cancellationToken) =>
        PromptAsync(message, [(readjustLabel, DeviationChoice.Readjust), ("このまま使う", DeviationChoice.UseAsIs), ("中止", DeviationChoice.Abort)], cancellationToken);

    public Task<bool> AskZeroRetryAsync(string message, CancellationToken cancellationToken) =>
        PromptAsync(message, [("もう一度", true), ("中止", false)], cancellationToken);

    public Task<bool> AskResumeAsync(string message, CancellationToken cancellationToken) =>
        PromptAsync(message, [("再開", true), ("中止", false)], cancellationToken);

    // 指示とボタンを出し、押されたボタンの値を返す。キャンセルされたら指示を引っ込める。
    private async Task<T> PromptAsync<T>(string message, (string Label, T Value)[] choices, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        ClearPrompt();
        _promptLabel.Text = message;
        SetWaiting(true);
        foreach (var (label, value) in choices)
            AddChoice(label, () => completion.TrySetResult(value));
        using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
        {
            try { return await completion.Task; }
            finally { ClearPrompt(); }
        }
    }

    private void AddChoice(string label, Action clicked)
    {
        // 押す所が分かるよう、ボタンは白地にする(枠の色と見分ける)。「中止」だけは今までどおりの色。
        var button = new Button { Text = label, AutoSize = true, MinimumSize = new Size(170, 52), Margin = new Padding(0, 3, 12, 3) };
        if (label != "中止")
        {
            button.BackColor = Color.White;
            button.Font = new Font(Font.FontFamily, PromptFontSize, FontStyle.Bold);
        }
        else
        {
            button.BackColor = SystemColors.Control;
            button.Font = new Font(Font.FontFamily, PromptFontSize);
        }
        button.Click += (_, _) => clicked();
        _choicesPanel.Controls.Add(button);
    }

    private void ClearPrompt()
    {
        _promptLabel.Text = "そのままお待ちください。操作が要るときは、この枠に色が付いて指示が出ます。";
        SetWaiting(false);
        _inputPanel.Visible = false;
        _promptError.Visible = false;
        foreach (Control control in _choicesPanel.Controls.Cast<Control>().ToArray())
        {
            _choicesPanel.Controls.Remove(control);
            control.Dispose();
        }
    }
}

// 読み取りの種類(2026-11-18): 電流(①②、マルチメータ)と圧力(③④、GC16)。
internal enum AutoCalibrationKind { Current, Pressure }
