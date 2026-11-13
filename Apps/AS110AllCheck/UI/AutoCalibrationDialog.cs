using System.Globalization;

namespace PSCAutomation.Apps.AS110AllCheck;

// 「GC16 で読み取る」の画面(校正の自動読み取り 段階 1。2026-11-13、SPEC「校正の自動読み取り」)。
// 開くとすぐ ①〜④ を順に行い、人への指示(ボールバルブの切り替え・マルチメータの値・レギュレーター)はこの画面の中に出す。
// 読み取れたら閉じて、校正画面の 8 欄に入れる(適用は校正画面で人が押す)。
// いつでも「中止して手入力に戻る」で止められる。止まったときは後片付け(WF1973 の出力停止・GC16 を RM,0・ボールバルブを測定の向き)をしてから、
// 校正画面の手入力に戻る(実機で確かめるまで、自動がうまく行かなくても手入力で必ず測定へ進める。人の指示)。
internal sealed class AutoCalibrationDialog : Form, IAutoCalibrationOperator
{
    private readonly ProductModel _model;
    private readonly Func<IAutoCalibrationOperator, AutoCalibrationProcedure> _createProcedure;
    private readonly CalibrationInProgressMarker _marker;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cancel = new();
    private readonly Label _statusLabel = new();
    private readonly Label _pressureLabel = new();
    private readonly Label _promptLabel = new();
    private readonly Label _promptError = new();
    private readonly FlowLayoutPanel _inputPanel = new();
    private readonly TextBox _inputBox = new();
    private readonly FlowLayoutPanel _choicesPanel = new();
    private readonly TextBox _logBox = new();
    private readonly Button _stopButton = new();
    private bool _running;
    private bool _closeWhenDone;

    public AutoCalibrationDialog(ProductModel model, Func<IAutoCalibrationOperator, AutoCalibrationProcedure> createProcedure,
        CalibrationInProgressMarker marker, Action<string> log)
    {
        _model = model;
        _createProcedure = createProcedure;
        _marker = marker;
        _log = log;
        Text = "GC16 で読み取る（校正の自動読み取り）";
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
            AutoSize = true, Font = new Font(Font.FontFamily, 13, FontStyle.Bold), Text = "GC16 で読み取る", Margin = new Padding(0, 0, 0, 4)
        });
        root.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(600, 0), Margin = new Padding(0, 0, 0, 8),
            Text = $"型式 {model.Name} ／ 目標の供給圧力 {model.SupplyPressure} kPa\r\n" +
                "① 0 mA → ② 30 mA → ③ 大気圧でゼロ調整 → ④ 供給圧力 の順に読み取ります。指示はこの画面に出ます。"
        });
        _statusLabel.AutoSize = true;
        _statusLabel.MaximumSize = new Size(600, 0);
        _statusLabel.Font = new Font(Font, FontStyle.Bold);
        _statusLabel.Margin = new Padding(0, 0, 0, 6);
        root.Controls.Add(_statusLabel);
        _pressureLabel.AutoSize = true;
        _pressureLabel.Font = new Font(Font.FontFamily, 16, FontStyle.Bold);
        _pressureLabel.Margin = new Padding(0, 0, 0, 8);
        ShowPressure(null);
        root.Controls.Add(_pressureLabel);
        var prompt = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(8), Margin = new Padding(0, 0, 0, 8), MinimumSize = new Size(600, 0)
        };
        _promptLabel.AutoSize = true;
        _promptLabel.MaximumSize = new Size(580, 0);
        _promptLabel.Margin = new Padding(0, 0, 0, 6);
        prompt.Controls.Add(_promptLabel);
        _inputPanel.AutoSize = true;
        _inputPanel.FlowDirection = FlowDirection.LeftToRight;
        _inputPanel.Visible = false;
        _inputBox.Width = 110;
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
        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Size = new Size(600, 150);
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

    public AutoCalibrationResult? Result { get; private set; }
    // 後片付けで WF1973 の出力を止められなかった(呼び出し側で「出力状態不明」にする)。
    public bool OutputStopFailed { get; private set; }
    // 読み取りを始めたか(WF1973 を DC にしたので、呼び出し側で発振器の設定を戻す)。
    public bool Started { get; private set; }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _running = true;
        AutoCalibrationProcedure? procedure = null;
        try
        {
            try { _marker.Begin(DateTimeOffset.Now, _model.Name); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowStatus("校正中の印を書けません（途中でアプリが落ちたとき、次の起動で後片付けの知らせが出ません）：" + ex.Message);
            }
            procedure = _createProcedure(this);
            Started = true;
            Result = await procedure.RunAsync(_model, _cancel.Token);
            ShowStatus("読み取りが終わりました：" + Result.Calibration.Summary);
        }
        catch (OperationCanceledException ex)
        {
            Finish(ex.InnerException is { } cause ? "中止しました（" + cause.Message + "）。" : "中止しました。");
        }
        catch (Exception ex)
        {
            Finish("読み取りを続けられません：" + ex.Message);
        }
        finally
        {
            OutputStopFailed = procedure?.OutputStopFailed ?? false;
            _marker.Clear();
            _running = false;
        }
        if (Result is not null)
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

    // 中止・失敗: 理由を残し、閉じるボタンにする(閉じると校正画面の手入力に戻る)。
    private void Finish(string message)
    {
        ShowStatus(message);
        ShowStatus("校正画面に戻って、8 項目を手で入れて「適用」すれば測定へ進めます。");
        ShowPressure(null);
        ClearPrompt();
        _promptLabel.Text = message + "\r\n「閉じて手入力に戻る」を押してください。";
        _stopButton.Text = "閉じて手入力に戻る";
        _stopButton.Enabled = true;
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
        DialogResult = Result is null ? DialogResult.Cancel : DialogResult.OK;
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

    public void ShowPressure(double? kilopascals) =>
        _pressureLabel.Text = "GC16：" + (kilopascals is { } v ? v.ToString("0.00", CultureInfo.InvariantCulture) + " kPa" : "--- kPa") +
            $"（目標 {_model.SupplyPressure} kPa）";

    public Task<bool> ConfirmAsync(string instruction, CancellationToken cancellationToken) =>
        PromptAsync(instruction, [("OK", true), ("中止", false)], cancellationToken);

    public Task NotifyAsync(string instruction) =>
        PromptAsync(instruction, [("OK", true)], CancellationToken.None);

    public async Task<double?> AskActualCurrentAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<double?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ClearPrompt();
        _promptLabel.Text = "② WF1973 から 30 mA の指令（5 V）を出しています。デジタルマルチメータの表示（mA）を入れて「OK」を押してください。\r\n小数点は . です（例 29.9）。";
        _inputBox.Text = "";
        _inputPanel.Visible = true;
        AddChoice("OK", () =>
        {
            if (double.TryParse(_inputBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
                double.IsFinite(value) && value > 0)
                completion.TrySetResult(value);
            else
            {
                _promptError.Text = "0 より大きい数を入れてください（小数点は .）。";
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

    public Task<bool> WaitRegulatorAdjustedAsync(double targetKilopascals, CancellationToken cancellationToken) =>
        PromptAsync($"④ レギュレーターを目標 {targetKilopascals:0.#} kPa に合わせてください。上の GC16 の値を見ながら回し、合わせたら「合わせた」を押してください。",
            [("合わせた", true), ("中止", false)], cancellationToken);

    public Task<WaitChoice> AskNotSettledAsync(string message, CancellationToken cancellationToken) =>
        PromptAsync(message, [("待ち続ける", WaitChoice.KeepWaiting), ("この段をやり直す", WaitChoice.RedoStage), ("中止", WaitChoice.Abort)], cancellationToken);

    public Task<DeviationChoice> AskDeviationAsync(string message, CancellationToken cancellationToken) =>
        PromptAsync(message, [("合わせ直す", DeviationChoice.Readjust), ("このまま使う", DeviationChoice.UseAsIs), ("中止", DeviationChoice.Abort)], cancellationToken);

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
        var button = new Button { Text = label, AutoSize = true, MinimumSize = new Size(110, 34) };
        button.Click += (_, _) => clicked();
        _choicesPanel.Controls.Add(button);
    }

    private void ClearPrompt()
    {
        _promptLabel.Text = "（指示はここに出ます）";
        _inputPanel.Visible = false;
        _promptError.Visible = false;
        foreach (Control control in _choicesPanel.Controls.Cast<Control>().ToArray())
        {
            _choicesPanel.Controls.Remove(control);
            control.Dispose();
        }
    }
}
