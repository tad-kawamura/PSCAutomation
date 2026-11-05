using System.Globalization;
using PSCAutomation.Apps.AS110NoiseTest.Control;
using PSCAutomation.Apps.AS110NoiseTest.Measurement;
using PSCAutomation.Wrappers.Common;

namespace PSCAutomation.Apps.AS110NoiseTest.Ui;

// 測定の画面(specs/AS110NoiseTest/SPEC.md「画面」)。1 画面に入力欄・開始・中断・状態を置く。
// 進み具合や電流ごとの結果の表は出さない(今何 mA かは 7351E の表示で、何が起きているかは LOGGER の画面で分かるため)。
// 測ること自体は MeasurementRunner(専用の STA スレッド)が行い、この画面は入力の確認と知らせの表示だけをする。
// 前回入れた値は残さない(残すかは後で決める。SPEC.md「決めていないこと(画面)」)。
internal sealed class MainForm : Form
{
    private const string AppTitle = "AS110 ノイズ測定";

    private readonly bool _simulation;
    private readonly TemplateFiles? _templates;
    private readonly double _maxMilliamps = new CurrentControlSettings().MaxMilliamps;

    private readonly ComboBox _model = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly TextBox _serial = new() { Width = 160 };
    private readonly TextBox _memo = new() { Width = 220 };
    private readonly TextBox _start = NumberBox(), _step = NumberBox(), _end = NumberBox();
    private readonly TextBox _xdtFolder = new() { Width = 420 };
    private readonly Button _browse = new() { Text = "参照…", AutoSize = true };
    private readonly Label _excelFolder = new() { AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
    private readonly RadioButton _newRadio = new() { Text = "新規", AutoSize = true, Checked = true, Margin = new Padding(3, 5, 3, 3) };
    private readonly TextBox _newName = new() { Width = 360 };
    private readonly RadioButton _appendRadio = new() { Text = "追記", AutoSize = true, Margin = new Padding(3, 5, 3, 3) };
    private readonly ComboBox _appendList = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
    private readonly Button _startButton = new() { Text = "開始", Size = new Size(120, 36) };
    private readonly Button _cancelButton = new() { Text = "中断", Size = new Size(120, 36), Enabled = false };
    private readonly Label _status = new() { AutoSize = true, Text = "待機中", Margin = new Padding(3, 6, 3, 3) };
    private readonly System.Windows.Forms.Control[] _inputs;

    private CancellationTokenSource? _cancel;
    private bool _running;
    private bool _closeWhenDone;
    private string _defaultNewName = "";

    public MainForm(bool simulation)
    {
        _simulation = simulation;
        Text = AppTitle + (simulation ? " [模擬: 機器を使いません]" : "");
        Font = new Font("Yu Gothic UI", 10f);
        AutoScaleMode = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        _status.Font = new Font(Font.FontFamily, 12f, FontStyle.Bold);

        _inputs = [_model, _serial, _memo, _start, _step, _end, _xdtFolder, _browse, _newRadio, _newName, _appendRadio, _appendList];
        // 名前は UI オートメーション(画面の自動確認)で欄を見分けるため。
        (_model.Name, _serial.Name, _memo.Name, _start.Name, _step.Name, _end.Name) = ("model", "serial", "memo", "start", "step", "end");
        (_xdtFolder.Name, _newRadio.Name, _newName.Name, _appendRadio.Name, _appendList.Name) = ("xdtFolder", "newRadio", "newName", "appendRadio", "appendList");
        (_startButton.Name, _cancelButton.Name, _status.Name, _excelFolder.Name) = ("startButton", "cancelButton", "status", "excelFolder");
        Controls.Add(BuildLayout());

        _model.SelectedIndexChanged += (_, _) => UpdateDefaultNewName();
        _xdtFolder.TextChanged += (_, _) => UpdateExcelFolder();
        _browse.Click += (_, _) => BrowseXdtFolder();
        // 新規 / 追記 は別々の行にあるので、ラジオボタンの組を手で揃える。
        _newRadio.CheckedChanged += (_, _) => { if (_newRadio.Checked) _appendRadio.Checked = false; UpdateExcelChoice(); };
        _appendRadio.CheckedChanged += (_, _) => { if (_appendRadio.Checked) _newRadio.Checked = false; UpdateExcelChoice(); };
        _startButton.Click += (_, _) => StartMeasurement();
        _cancelButton.Click += (_, _) => RequestCancel();

        try
        {
            _templates = TemplateFiles.Find();
            foreach (string model in _templates.ReadModels()) _model.Items.Add(model);
            _model.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            _templates = null;
            _status.Text = "開始できません";
            Shown += (_, _) => ErrorNotification.Show(this, "開始できません。\n" + ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        UpdateExcelFolder();
        UpdateExcelChoice();
        SetRunning(false);
    }

    private static TextBox NumberBox() => new() { Width = 50, TextAlign = HorizontalAlignment.Right };

    private System.Windows.Forms.Control BuildLayout()
    {
        var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        void AddRow(string label, params System.Windows.Forms.Control[] controls)
        {
            var flow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
            flow.Controls.AddRange(controls);
            table.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 7, 12, 3) });
            table.Controls.Add(flow);
        }
        Label Unit(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 7, 8, 3) };

        AddRow("型式", _model);
        AddRow("SN", _serial, Unit("   備忘録"), _memo);
        AddRow("電流", Unit("開始"), _start, Unit("mA   刻み"), _step, Unit("mA   終了"), _end, Unit("mA"));
        AddRow("xdt 保存先", _xdtFolder, _browse);
        AddRow("結果 Excel", _excelFolder);
        AddRow("", _newRadio, _newName);
        AddRow("", _appendRadio, _appendList);
        var spacer = new Label { AutoSize = false, Width = 1, Height = 8 };
        AddRow("", spacer);
        AddRow("", _startButton, _cancelButton);
        AddRow("状態", _status);
        return table;
    }

    // ---- 入力欄の連動 ----

    private void UpdateDefaultNewName()
    {
        string model = _model.SelectedItem as string ?? "";
        string name = MeasurementSettings.ModelForFileName(model) + "_ノイズ測定.xlsx";
        // 人が書き換えていなければ、型式に合わせて初期値を入れ替える。
        if (_newName.Text.Length == 0 || _newName.Text == _defaultNewName) _newName.Text = name;
        _defaultNewName = name;
    }

    private static string? ExcelFolderOf(string xdtFolder)
    {
        string text = xdtFolder.Trim();
        if (text.Length == 0 || !Path.IsPathFullyQualified(text)) return null;
        try { return Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(text))); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private void UpdateExcelFolder()
    {
        string? folder = ExcelFolderOf(_xdtFolder.Text);
        _excelFolder.Text = folder is null
            ? "置き場: xdt 保存先の 1 つ上のフォルダー(保存先を選ぶと決まります)"
            : $"置き場: {folder}";
        RefreshAppendList(folder);
    }

    private void RefreshAppendList(string? folder)
    {
        string? selected = _appendList.SelectedItem as string;
        _appendList.Items.Clear();
        if (folder is not null && Directory.Exists(folder))
        {
            var files = Directory.EnumerateFiles(folder, "*.xlsx")
                .Select(Path.GetFileName)
                .Where(name => name is not null && !name.StartsWith("~$", StringComparison.Ordinal))   // Excel が開いている間の一時ファイル
                .Order(StringComparer.CurrentCultureIgnoreCase);
            foreach (string? name in files) _appendList.Items.Add(name!);
        }
        if (selected is not null && _appendList.Items.Contains(selected)) _appendList.SelectedItem = selected;
        else if (_appendList.Items.Count == 1) _appendList.SelectedIndex = 0;
    }

    private void UpdateExcelChoice()
    {
        if (_running) return;
        _newName.Enabled = _newRadio.Checked;
        _appendList.Enabled = _appendRadio.Checked;
        if (_appendRadio.Checked) RefreshAppendList(ExcelFolderOf(_xdtFolder.Text));
    }

    private void BrowseXdtFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "xdt の保存先フォルダーを選んでください(結果 Excel はその 1 つ上に置きます)",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        string current = _xdtFolder.Text.Trim();
        if (current.Length > 0 && Directory.Exists(current)) dialog.InitialDirectory = current;
        if (dialog.ShowDialog(this) == DialogResult.OK) _xdtFolder.Text = dialog.SelectedPath;
    }

    // ---- 開始・中断 ----

    private void StartMeasurement()
    {
        if (_running || _templates is null) return;
        if (!TryBuildPlan(out MeasurementPlan? plan, out string error))
        {
            MessageBox.Show(this, error, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _cancel = new CancellationTokenSource();
        _status.Text = "準備中";
        SetRunning(true);
        var runner = new MeasurementRunner
        {
            ReportStatus = text => PostToUi(() => { if (_running && _cancel?.IsCancellationRequested == false) _status.Text = text; }),
            AskToContinue = text => (bool)Invoke(new Func<bool>(() =>
                MessageBox.Show(this, text, AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2)
                == DialogResult.Yes)),
            Completed = outcome => PostToUi(() => OnCompleted(outcome)),
        };
        runner.Start(new RunRequest(plan!, _templates, _simulation), _cancel.Token);
    }

    private bool TryBuildPlan(out MeasurementPlan? plan, out string error)
    {
        plan = null;
        string model = _model.SelectedItem as string ?? "";
        if (model.Length == 0) { error = "型式を選んでください。"; return false; }
        string serial = _serial.Text.Trim();
        if (serial.Length == 0) { error = "SN を入れてください。"; return false; }
        if (!TryReadInt(_start, "開始", out int start, out error)) return false;
        if (!TryReadInt(_step, "刻み", out int step, out error)) return false;
        if (!TryReadInt(_end, "終了", out int end, out error)) return false;

        string folderText = _xdtFolder.Text.Trim();
        if (folderText.Length == 0) { error = "xdt の保存先を選んでください。"; return false; }
        if (!Path.IsPathFullyQualified(folderText)) { error = @"xdt の保存先はドライブから書いてください(例 C:\AS110Noise\SN60226)。"; return false; }
        string? excelFolder = ExcelFolderOf(folderText);
        if (excelFolder is null)
        {
            error = "結果 Excel を xdt 保存先の 1 つ上のフォルダーに置くため、ドライブの直下は選べません。フォルダーの中のフォルダーを選んでください。";
            return false;
        }
        string xdtFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderText));

        string excelPath;
        if (_newRadio.Checked)
        {
            string name = _newName.Text.Trim();
            if (name.Length == 0) { error = "新規の結果 Excel の名前を入れてください。"; return false; }
            if (!name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) name += ".xlsx";
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { error = "結果 Excel の名前に使えない文字があります(\\ / : * ? \" < > | など)。"; return false; }
            excelPath = Path.Combine(excelFolder, name);
            if (File.Exists(excelPath))
            {
                error = $"同じ名前の結果 Excel がもうあります。\n{excelPath}\n\n「追記」を選ぶか、別の名前にしてください。";
                return false;
            }
        }
        else
        {
            RefreshAppendList(excelFolder);
            if (_appendList.Items.Count == 0)
            {
                error = $"追記できる結果 Excel が {excelFolder} にありません。「新規」を選んでください。";
                return false;
            }
            if (_appendList.SelectedItem is not string name) { error = "追記する結果 Excel を選んでください。"; return false; }
            excelPath = Path.Combine(excelFolder, name);
            if (!File.Exists(excelPath)) { error = $"結果 Excel が見つかりません。\n{excelPath}"; return false; }
        }

        var candidate = new MeasurementPlan(model, serial, _memo.Text.Trim(), start, step, end, xdtFolder, excelPath);
        try { candidate.Validate(_maxMilliamps); }
        catch (ArgumentException ex) { error = ex.Message; return false; }
        plan = candidate;
        error = "";
        return true;
    }

    private static bool TryReadInt(TextBox box, string name, out int value, out string error)
    {
        string text = box.Text.Trim();
        if (text.Length == 0) { value = 0; error = $"{name} mA を入れてください。"; return false; }
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            error = $"{name} mA は整数で入れてください({text})。";
            return false;
        }
        error = "";
        return true;
    }

    private bool ConfirmCancel() =>
        ErrorNotification.Show(this, "測定を中断しますか?\n(LOGGER を止め、WF1973 を 0V・出力 OFF にします。記録途中の 1 本は保存されません)",
            AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    private void RequestCancel()
    {
        if (!_running || _cancel is null || _cancel.IsCancellationRequested) return;
        if (!ConfirmCancel()) return;
        _cancel.Cancel();
        _cancelButton.Enabled = false;
        _status.Text = "中断しています…";
    }

    private void OnCompleted(RunOutcome outcome)
    {
        _cancel?.Dispose();
        _cancel = null;
        SetRunning(false);
        if (_closeWhenDone)
        {
            Close();
            return;
        }
        _status.Text = outcome.Result switch
        {
            RunResult.Finished => "終了しました",
            RunResult.Canceled => "中断しました",
            RunResult.Declined => "待機中",
            _ => "止まりました",
        };
        if (outcome.Result == RunResult.Declined) return;
        // 画面を前に出して知らせる(LOGGER の後ろに隠れないよう最前面のメッセージにする)。
        WindowActivation.TryActivate(this, _startButton);
        MessageBoxIcon icon = outcome.Result is RunResult.Finished or RunResult.Canceled ? MessageBoxIcon.Information : MessageBoxIcon.Warning;
        ErrorNotification.Show(this, outcome.Message, AppTitle, MessageBoxButtons.OK, icon);
        // 知らせを閉じたら、人に渡した結果 Excel を前に出す。
        if (outcome.Result == RunResult.Finished && outcome.ExcelWindow != IntPtr.Zero) WindowActivation.TryActivate(outcome.ExcelWindow);
        if (_appendRadio.Checked) RefreshAppendList(ExcelFolderOf(_xdtFolder.Text));
    }

    private void SetRunning(bool running)
    {
        _running = running;
        foreach (System.Windows.Forms.Control c in _inputs) c.Enabled = !running;
        _startButton.Enabled = !running && _templates is not null;
        _cancelButton.Enabled = running;
        if (!running) UpdateExcelChoice();
    }

    private void PostToUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    // 測定中に閉じようとしたら、中断と同じ確認を出す。中断したら後始末が済んでから閉じる。
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_running)
        {
            e.Cancel = true;
            if (_closeWhenDone) return;
            if (_cancel is { IsCancellationRequested: true } || ConfirmCancel())
            {
                _closeWhenDone = true;
                _cancel?.Cancel();
                _cancelButton.Enabled = false;
                _status.Text = "中断しています…(終わったら閉じます)";
            }
            return;
        }
        base.OnFormClosing(e);
    }
}
