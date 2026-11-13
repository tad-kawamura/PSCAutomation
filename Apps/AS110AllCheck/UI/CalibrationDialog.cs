using System.Globalization;

namespace PSCAutomation.Apps.AS110AllCheck;

// 入力した電流・圧力の2点校正値を、そのまま作業用XDTへ反映するための画面。
// 並びは現場の校正記録用紙と同じ縦型(2026-11-06、人の指示): 「電流」「圧力」の枠ごとに、
// 1 行目が低点・2 行目が高点、左の列が計測値(V)・右の列が表示値(mA / kPa)。
// 2026-11-13: 型式(目標の供給圧力)の選択と「GC16 で読み取る」を足した(SPEC「校正の自動読み取り」段階 1)。手入力の 8 項目はそのまま残す。
// 読み取った値は 8 欄に入れるだけで、適用は今までどおり人が「適用」を押す。読み取りが失敗・中止しても、手で入れて適用すれば測定へ進める。
internal sealed class CalibrationDialog : Form
{
    // 添字は 電流: 0=低点電圧 1=低点実測値 2=高点電圧 3=高点実測値、圧力: 4〜7 も同じ順。
    private readonly TextBox[] _values = new TextBox[8];
    private readonly ComboBox _modelBox = new();
    private readonly Label _readingLabel = new();
    private readonly Func<ProductModel, IWin32Window, AutoCalibrationResult?>? _readWithGauge;
    // GC16 で読み取った結果と、そのとき欄に入れた文字(適用の前に手で直したかを見るため)。
    private AutoCalibrationResult? _gaugeResult;
    private string[]? _gaugeTexts;
    public As110Calibration Calibration { get; private set; }
    // 読み取り方(校正記録に書く)。「適用」を押したときに決まる。
    public CalibrationReading Reading { get; private set; } = CalibrationReading.Manual(null);

    // 「GC16 で読み取る」を出さない形(前からの呼び方。画面の検査が使う)。
    public CalibrationDialog(string hardware, string templateName, As110Calibration initial)
        : this(hardware, templateName, initial, null)
    {
    }

    // readWithGauge: 「GC16 で読み取る」を押したときに読み取りの画面を出し、結果(中止・失敗は null)を返す。null ならボタンを出さない。
    public CalibrationDialog(string hardware, string templateName, As110Calibration initial,
        Func<ProductModel, IWin32Window, AutoCalibrationResult?>? readWithGauge)
    {
        _readWithGauge = readWithGauge;
        Calibration = initial;
        Text = "スケーリング（電流・圧力の2点校正）";
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        // LOGGERの背後に隠れないよう最前面にする。MainFormを最前面に固定していたころ(2026-11-11 まで)は、最前面でないモーダルはその背後に隠れ、
        // 画面が出ないまま操作を受け付けなくなる（保護解除・転送設定と同じ対策）。
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
            AutoSize = true, Font = new Font(Font.FontFamily, 13, FontStyle.Bold), Text = "スケーリング",
            Margin = new Padding(0, 0, 0, 4)
        });
        root.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(520, 0),
            Text = hardware + " ／ " + templateName + "\r\n初期値は雛形の値です。実測した値を入力してください。",
            Margin = new Padding(0, 0, 0, 12)
        });
        root.Controls.Add(BuildReadingSection());
        float[] values = [initial.Current.Input1, initial.Current.Output1, initial.Current.Input2, initial.Current.Output2,
            initial.Pressure.Input1, initial.Pressure.Output1, initial.Pressure.Input2, initial.Pressure.Output2];
        root.Controls.Add(BuildSection("電流", "mA", 0, values));
        root.Controls.Add(BuildSection("圧力", "kPa", 4, values));
        root.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 8, 0, 12),
            Text = "1 が低点（ゼロ付近）、2 が高点です。「適用」で作業用XDTへ反映してLOGGERで開き直します。\r\n適用後は型式（参照波形）を選択してください。" });
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        var apply = new Button { Text = "適用", AutoSize = true, MinimumSize = new Size(120, 36) };
        var cancel = new Button { Text = "キャンセル", AutoSize = true, MinimumSize = new Size(100, 36), DialogResult = DialogResult.Cancel };
        apply.Click += (_, _) => ApplyValues();
        buttons.Controls.AddRange([apply, cancel]);
        root.Controls.Add(buttons);
        Controls.Add(root);
        // 入力中のEnterで誤って適用しないよう、適用はボタン操作だけにする。
        CancelButton = cancel;
    }

    // 型式(目標の供給圧力)と「GC16 で読み取る」。型式は測定の型式(参照波形)の初期値にしない(決まったこと 25)。
    private Control BuildReadingSection()
    {
        var section = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 0, 10)
        };
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty };
        row.Controls.Add(new Label { Text = "型式（供給圧力の目標）", AutoSize = true, Margin = new Padding(0, 8, 6, 3) });
        _modelBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _modelBox.Width = 220;
        _modelBox.AccessibleName = "型式（供給圧力の目標）";
        _modelBox.Items.Add("（選んでいない）");
        foreach (var model in ProductModel.All) _modelBox.Items.Add(model);
        _modelBox.SelectedIndex = 0;
        row.Controls.Add(_modelBox);
        if (_readWithGauge is not null)
        {
            var read = new Button { Text = "GC16 で読み取る", AutoSize = true, MinimumSize = new Size(140, 32), Margin = new Padding(12, 3, 3, 3) };
            read.Click += (_, _) => ReadWithGauge();
            row.Controls.Add(read);
        }
        section.Controls.Add(row);
        _readingLabel.AutoSize = true;
        _readingLabel.MaximumSize = new Size(520, 0);
        _readingLabel.Margin = new Padding(0, 4, 0, 0);
        _readingLabel.Text = _readWithGauge is null ? "型式は校正記録に目標の供給圧力として残します（選ばなくても適用できます）。" :
            "「GC16 で読み取る」は型式を選んでから押してください。読み取れなかったときは、下の 8 項目を手で入れて「適用」してください。";
        section.Controls.Add(_readingLabel);
        return section;
    }

    private ProductModel? SelectedModel => _modelBox.SelectedItem as ProductModel;

    private void ReadWithGauge()
    {
        if (_readWithGauge is null) return;
        if (SelectedModel is not { } model)
        {
            ErrorNotification.Show(this, "目標の供給圧力を出すため、型式を選んでください。", "型式を選んでください", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        AutoCalibrationResult? result = _readWithGauge(model, this);
        if (result is null)
        {
            _readingLabel.Text = "GC16 での読み取りは終わっていません。8 項目を手で入れて「適用」するか、もう一度「GC16 で読み取る」を押してください。";
            return;
        }
        var c = result.Calibration;
        float[] read = [c.Current.Input1, c.Current.Output1, c.Current.Input2, c.Current.Output2,
            c.Pressure.Input1, c.Pressure.Output1, c.Pressure.Input2, c.Pressure.Output2];
        _gaugeTexts = new string[read.Length];
        for (int i = 0; i < read.Length; i++)
            _values[i].Text = _gaugeTexts[i] = read[i].ToString("G", CultureInfo.InvariantCulture);
        _gaugeResult = result;
        _readingLabel.Text = $"GC16 で読み取りました（{result.Model.Name}、0 点 {result.Zero.Describe()}）。値を確かめて「適用」を押してください。";
    }

    // 「電流」「圧力」の 1 枠。見出し行(計測値・表示値)と、1(低点)・2(高点)の 2 行。
    private Control BuildSection(string name, string unit, int firstIndex, float[] values)
    {
        var section = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 0, 10)
        };
        section.Controls.Add(new Label
        {
            Text = name, AutoSize = true, Font = new Font(Font, FontStyle.Bold), BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(6, 2, 6, 2), Margin = new Padding(0, 0, 0, 4)
        });
        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 5, RowCount = 3, Margin = new Padding(12, 0, 0, 0) };
        grid.Controls.Add(new Label { Text = "計測値", AutoSize = true, Margin = new Padding(3, 3, 3, 3) }, 1, 0);
        grid.Controls.Add(new Label { Text = "表示値", AutoSize = true, Margin = new Padding(3, 3, 3, 3) }, 3, 0);
        for (int row = 0; row < 2; row++)
        {
            grid.Controls.Add(new Label { Text = (row + 1).ToString(), AutoSize = true, Margin = new Padding(3, 6, 8, 3) }, 0, row + 1);
            // 左の列: 計測値(V)。右の列: 表示値(mA / kPa)。
            int inputIndex = firstIndex + row * 2;
            grid.Controls.Add(MakeBox(inputIndex, values[inputIndex], $"{name} {(row == 0 ? "低点" : "高点")}電圧 (V)"), 1, row + 1);
            grid.Controls.Add(new Label { Text = "V", AutoSize = true, Margin = new Padding(3, 6, 18, 3) }, 2, row + 1);
            grid.Controls.Add(MakeBox(inputIndex + 1, values[inputIndex + 1], $"{name} {(row == 0 ? "低点" : "高点")}実測値 ({unit})"), 3, row + 1);
            grid.Controls.Add(new Label { Text = unit, AutoSize = true, Margin = new Padding(3, 6, 3, 3) }, 4, row + 1);
        }
        section.Controls.Add(grid);
        return section;
    }

    private TextBox MakeBox(int index, float value, string accessibleName)
    {
        var box = new TextBox
        {
            Text = value.ToString("G", CultureInfo.InvariantCulture), Width = 110, TextAlign = HorizontalAlignment.Right,
            AccessibleName = accessibleName, TabIndex = index
        };
        _values[index] = box;
        return box;
    }

    private void ApplyValues()
    {
        try
        {
            var parsed = new float[8];
            for (int i = 0; i < parsed.Length; i++)
            {
                if (!float.TryParse(_values[i].Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i]) ||
                    !float.IsFinite(parsed[i]))
                    throw new ArgumentException("電流・圧力の8項目に有限の数値を入力してください（小数点は .）。");
            }
            var result = new As110Calibration(new(parsed[0], parsed[1], parsed[2], parsed[3]),
                new(parsed[4], parsed[5], parsed[6], parsed[7]));
            result.Validate();
            Calibration = result;
            Reading = _gaugeResult is { } gauge
                ? new CalibrationReading(true, gauge.Model, gauge.GaugePort, gauge.SinceGaugeConfirmed, gauge.Zero.Describe(),
                    EditedAfterRead: _gaugeTexts is null || _values.Select(v => v.Text.Trim()).Where((text, i) => text != _gaugeTexts[i]).Any())
                : CalibrationReading.Manual(SelectedModel);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (ArgumentException ex)
        {
            ErrorNotification.Show(this, ex.Message, "校正値を確認してください", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
