using System.Globalization;

namespace PSCAutomation.Apps.AS110AllCheck;

// 入力した電流・圧力の2点校正値を、今日の校正値として残すための画面。
// 値の欄の並びは現場の校正記録用紙と同じ縦型(2026-11-06、人の指示): 「電流」「圧力」の枠ごとに、
// 1 行目が低点・2 行目が高点、左の列が計測値(V)・右の列が表示値(mA / kPa)。
// 2026-11-13: 型式(目標の供給圧力)の選択と「GC16 で読み取る」を足した(SPEC「校正の自動読み取り」段階 1)。手入力の 8 項目はそのまま残す。
// 2026-11-18: 読み取りを「マルチメータで読み取る」(電流の 4 欄)と「GC16 で読み取る」(圧力の 4 欄)に分けた(SPEC「段階 1 の直しと 1 日 1 回の流れ」)。
// 片方だけ使い、残りを手で入れてもよい。押す順番も問わない。
// 読み取った値は欄に入れるだけで、適用は今までどおり人が「適用」を押す。読み取りが失敗・中止しても、手で入れて適用すれば測定へ進める。
// 2026-11-20: どこをどの順に押すかが分かりにくかったので(人の指摘)、上から「手順 1 電流」「手順 2 圧力」「手順 3 適用」の枠に分け、
// 読み取りのボタンをその値の欄と同じ枠に入れた。いちばん上に「次にすること」を出し、次に押す所に色を付ける(UpdateGuide)。
// 案内だけで、押す順番は今までどおり問わない(「適用」はいつでも押せる)。欄の数は変えていない。
// 読み取りが済んだボタンは「…読み取り直す」に替わり、押すと先に聞く(既定「いいえ」。間違って押しても発振器・GC16 が動かないように。人と決めた)。
internal sealed class CalibrationDialog : Form
{
    // 次に押す所に付ける色。
    private static readonly Color NextColor = Color.FromArgb(255, 224, 130);
    private const int ContentWidth = 600;

    // 添字は 電流: 0=低点電圧 1=低点実測値 2=高点電圧 3=高点実測値、圧力: 4〜7 も同じ順。
    private readonly TextBox[] _values = new TextBox[8];
    private readonly ComboBox _modelBox = new();
    private readonly Label _nextLabel = new();
    private readonly Label _readingLabel = new();
    private readonly Label _currentStateLabel = new();
    private readonly Label _pressureStateLabel = new();
    private readonly Label _modelLabel = new();
    private readonly Button _applyButton = new();
    private Button? _readCurrentButton;
    private Button? _readPressureButton;
    private readonly Func<IWin32Window, CurrentReadingResult?>? _readCurrent;
    private readonly Func<ProductModel, IWin32Window, PressureReadingResult?>? _readPressure;
    // 読み取った結果と、そのとき欄に入れた文字(適用の前に手で直したかを見るため)。
    private CurrentReadingResult? _currentResult;
    private string[]? _currentTexts;
    private PressureReadingResult? _pressureResult;
    private string[]? _pressureTexts;
    public As110Calibration Calibration { get; private set; }
    // 読み取り方(校正記録に書く)。「適用」を押したときに決まる。
    public CalibrationReading Reading { get; private set; } = CalibrationReading.Manual(null);
    // 雛形の値(ふつうの値から外れていないかを見るのに使う。無ければ雛形とは比べない)。
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public As110Calibration? Template { get; init; }
    // 既定が「いいえ」の確認(題・文 → 「はい」なら true)。画面の検査で差し替える。
    private Func<string, string, bool> _confirm;

    // 読み取りのボタンを出さない形(前からの呼び方。画面の検査が使う)。
    public CalibrationDialog(string hardware, string templateName, As110Calibration initial)
        : this(hardware, templateName, initial, null, null)
    {
    }

    // readCurrent: 「マルチメータで読み取る」、readPressure: 「GC16 で読み取る」を押したときに読み取りの画面を出し、結果(中止・失敗は null)を返す。
    // null ならそのボタンを出さない。initialNote は初期値の説明(今日の校正値 / 雛形の値)。
    public CalibrationDialog(string hardware, string templateName, As110Calibration initial,
        Func<IWin32Window, CurrentReadingResult?>? readCurrent, Func<ProductModel, IWin32Window, PressureReadingResult?>? readPressure,
        string initialNote = "初期値は雛形の値です。実測した値を入力してください。")
    {
        _readCurrent = readCurrent;
        _readPressure = readPressure;
        _confirm = AskDefaultNo;
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
            AutoSize = true, MaximumSize = new Size(ContentWidth, 0),
            Text = hardware + " ／ " + templateName + "\r\n" + initialNote,
            Margin = new Padding(0, 0, 0, 8)
        });
        // 次にすること(いちばん目に入る所に、色付きで出す)。
        _nextLabel.AutoSize = true;
        _nextLabel.MinimumSize = new Size(ContentWidth, 0);
        _nextLabel.MaximumSize = new Size(ContentWidth, 0);
        _nextLabel.Font = new Font(Font.FontFamily, 11, FontStyle.Bold);
        _nextLabel.BackColor = NextColor;
        _nextLabel.Padding = new Padding(8, 6, 8, 6);
        _nextLabel.Margin = new Padding(0, 0, 0, 4);
        root.Controls.Add(_nextLabel);
        _readingLabel.AutoSize = true;
        _readingLabel.MaximumSize = new Size(ContentWidth, 0);
        _readingLabel.Margin = new Padding(0, 0, 0, 8);
        _readingLabel.Text = _readCurrent is null && _readPressure is null
            ? "型式は校正記録に目標の供給圧力として残します（選ばなくても適用できます）。"
            : "色の付いた所を、上から順に押してください。読み取れなかった欄は手で入れて「適用」でも進めます。";
        root.Controls.Add(_readingLabel);
        float[] values = [initial.Current.Input1, initial.Current.Output1, initial.Current.Input2, initial.Current.Output2,
            initial.Pressure.Input1, initial.Pressure.Output1, initial.Pressure.Input2, initial.Pressure.Output2];
        root.Controls.Add(BuildCurrentStep(values));
        root.Controls.Add(BuildPressureStep(values));
        root.Controls.Add(BuildApplyStep());
        Controls.Add(root);
        _modelBox.SelectedIndexChanged += (_, _) => UpdateGuide();
        UpdateGuide();
    }

    // 手順 1: 「マルチメータで読み取る」と電流の 4 欄。
    private Control BuildCurrentStep(float[] values)
    {
        var (group, body) = MakeStep(_readCurrent is null ? "手順 1　電流の値を入れる" : "手順 1　電流を読み取る（マルチメータ）");
        if (_readCurrent is not null)
        {
            var row = MakeRow();
            _readCurrentButton = new Button { Text = "マルチメータで読み取る", AutoSize = true, MinimumSize = new Size(190, 36), Margin = new Padding(0, 0, 8, 3) };
            _readCurrentButton.Click += (_, _) => ReadCurrent();
            row.Controls.Add(_readCurrentButton);
            body.Controls.Add(row);
            ConfigureStateLabel(_currentStateLabel);
            body.Controls.Add(_currentStateLabel);
        }
        body.Controls.Add(BuildSection("電流", "mA", 0, values));
        return group;
    }

    // 手順 2: 型式(目標の供給圧力)→「GC16 で読み取る」と圧力の 4 欄。型式は測定の型式(参照波形)の初期値にしない(決まったこと 25)。
    private Control BuildPressureStep(float[] values)
    {
        var (group, body) = MakeStep(_readPressure is null ? "手順 2　圧力の値を入れる" : "手順 2　圧力を読み取る（GC16）");
        var row = MakeRow();
        _modelLabel.Text = "型式（供給圧力の目標）";
        _modelLabel.AutoSize = true;
        _modelLabel.Padding = new Padding(4, 4, 4, 4);
        _modelLabel.Margin = new Padding(0, 4, 4, 3);
        row.Controls.Add(_modelLabel);
        _modelBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _modelBox.Width = 200;
        _modelBox.Margin = new Padding(0, 7, 3, 3);
        _modelBox.AccessibleName = "型式（供給圧力の目標）";
        _modelBox.Items.Add("（選んでいない）");
        foreach (var model in ProductModel.All) _modelBox.Items.Add(model);
        _modelBox.SelectedIndex = 0;
        row.Controls.Add(_modelBox);
        if (_readPressure is not null)
        {
            row.Controls.Add(new Label { Text = "→", AutoSize = true, Margin = new Padding(4, 10, 4, 3) });
            _readPressureButton = new Button { Text = "GC16 で読み取る", AutoSize = true, MinimumSize = new Size(150, 36), Margin = new Padding(0, 0, 3, 3) };
            _readPressureButton.Click += (_, _) => ReadPressure();
            row.Controls.Add(_readPressureButton);
        }
        body.Controls.Add(row);
        if (_readPressure is not null)
        {
            ConfigureStateLabel(_pressureStateLabel);
            body.Controls.Add(_pressureStateLabel);
        }
        body.Controls.Add(BuildSection("圧力", "kPa", 4, values));
        return group;
    }

    // 手順 3: 値を確かめて「適用」。
    private Control BuildApplyStep()
    {
        var (group, body) = MakeStep("手順 3　値を確かめて「適用」を押す");
        body.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(ContentWidth - 30, 0), Margin = new Padding(0, 0, 0, 8),
            Text = "1 が低点（ゼロ付近）、2 が高点です。「適用」で今日の校正値として残し、モードの雛形に入れて LOGGER で開きます。\r\n" +
                "その日はアプリを立ち上げ直しても、校正し直さなくて済みます。" });
        var buttons = MakeRow();
        _applyButton.Text = "適用";
        _applyButton.AutoSize = true;
        _applyButton.MinimumSize = new Size(150, 40);
        _applyButton.Margin = new Padding(0, 0, 12, 3);
        var cancel = new Button { Text = "キャンセル", AutoSize = true, MinimumSize = new Size(100, 40), DialogResult = DialogResult.Cancel };
        _applyButton.Click += (_, _) => ApplyValues();
        buttons.Controls.AddRange([_applyButton, cancel]);
        body.Controls.Add(buttons);
        // 入力中のEnterで誤って適用しないよう、適用はボタン操作だけにする。
        CancelButton = cancel;
        return group;
    }

    // 手順の枠(見出し付き)と、その中身を上から並べる入れ物。
    private (GroupBox Group, FlowLayoutPanel Body) MakeStep(string title)
    {
        var group = new GroupBox
        {
            Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(ContentWidth, 0), MaximumSize = new Size(ContentWidth, 0),
            Font = new Font(Font, FontStyle.Bold), Padding = new Padding(10, 6, 10, 6), Margin = new Padding(0, 0, 0, 8)
        };
        var body = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Font = Font
        };
        group.Controls.Add(body);
        return (group, body);
    }

    private static FlowLayoutPanel MakeRow() => new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 4)
    };

    private void ConfigureStateLabel(Label label)
    {
        label.AutoSize = true;
        label.MaximumSize = new Size(ContentWidth - 30, 0);
        label.Margin = new Padding(0, 0, 0, 4);
    }

    // 「次にすること」と、次に押す所の色、読み取りの済み・まだを、今の状態に合わせる。案内だけで、押せる・押せないは変えない
    // (「GC16 で読み取る」だけは、型式を選ぶまで押せない。今までは押すと「型式を選んでください」と出た)。
    private void UpdateGuide()
    {
        bool needCurrent = _readCurrentButton is not null && _currentResult is null;
        bool needPressure = _readPressureButton is not null && _pressureResult is null;
        bool modelChosen = SelectedModel is not null;
        bool nextCurrent = needCurrent;
        bool nextModel = !nextCurrent && needPressure && !modelChosen;
        bool nextPressure = !nextCurrent && needPressure && modelChosen;
        bool nextApply = !nextCurrent && !nextModel && !nextPressure;
        _nextLabel.Text = "次にすること：" +
            (nextCurrent ? "手順 1 の「マルチメータで読み取る」を押してください"
            : nextModel ? "手順 2 で型式を選んでください"
            : nextPressure ? "手順 2 の「GC16 で読み取る」を押してください"
            : _readCurrentButton is null && _readPressureButton is null ? "電流・圧力の値を入れて、手順 3 の「適用」を押してください"
            : "手順 3 で値を確かめて「適用」を押してください");
        Highlight(_readCurrentButton, nextCurrent);
        Highlight(_readPressureButton, nextPressure);
        Highlight(_applyButton, nextApply);
        _modelLabel.BackColor = nextModel ? NextColor : Color.Transparent;
        if (_readPressureButton is not null) _readPressureButton.Enabled = modelChosen;
        // 済んだ側のボタンは、読み取り直し用だと分かる文字にする。
        if (_readCurrentButton is not null) _readCurrentButton.Text = _currentResult is null ? "マルチメータで読み取る" : "マルチメータで読み取り直す";
        if (_readPressureButton is not null) _readPressureButton.Text = _pressureResult is null ? "GC16 で読み取る" : "GC16 で読み取り直す";
        SetState(_currentStateLabel, _currentResult is null ? null : "✓ 読み取り済み：" + _currentResult.MethodText + "\r\n（値がおかしいときだけ「マルチメータで読み取り直す」を押します）", "まだ読み取っていません");
        SetState(_pressureStateLabel, _pressureResult is null ? null : $"✓ 読み取り済み：{_pressureResult.Model.Name}、0 点 {_pressureResult.Zero.Describe()}\r\n（型式を間違えた・値がおかしいときだけ「GC16 で読み取り直す」を押します）",
            modelChosen ? "まだ読み取っていません" : "まだ読み取っていません（先に型式を選びます）");
    }

    private static void Highlight(Button? button, bool next)
    {
        if (button is null) return;
        button.BackColor = next ? NextColor : SystemColors.Control;
        button.UseVisualStyleBackColor = !next;
    }

    private static void SetState(Label label, string? done, string notYet)
    {
        label.Text = done ?? notYet;
        label.ForeColor = done is null ? SystemColors.GrayText : Color.DarkGreen;
    }

    private ProductModel? SelectedModel => _modelBox.SelectedItem as ProductModel;

    private void ReadCurrent()
    {
        if (_readCurrent is null) return;
        if (_currentResult is not null && !ConfirmReadAgain("電流")) return;
        CurrentReadingResult? result = _readCurrent(this);
        if (result is null)
        {
            _readingLabel.Text = "電流の読み取りは終わっていません。電流の 4 欄を手で入れるか、もう一度「マルチメータで読み取る」を押してください。";
            return;
        }
        _currentTexts = Fill(0, result.Current);
        _currentResult = result;
        _readingLabel.Text = "電流を読み取りました。値は手順 1 の欄に入っています。";
        UpdateGuide();
    }

    private void ReadPressure()
    {
        if (_readPressure is null) return;
        if (SelectedModel is not { } model)
        {
            ErrorNotification.Show(this, "目標の供給圧力を出すため、型式を選んでください。", "型式を選んでください", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_pressureResult is not null && !ConfirmReadAgain("圧力")) return;
        PressureReadingResult? result = _readPressure(model, this);
        if (result is null)
        {
            _readingLabel.Text = "GC16 での読み取りは終わっていません。圧力の 4 欄を手で入れるか、もう一度「GC16 で読み取る」を押してください。";
            return;
        }
        _pressureTexts = Fill(4, result.Pressure);
        _pressureResult = result;
        _readingLabel.Text = "圧力を読み取りました。値は手順 2 の欄に入っています。";
        UpdateGuide();
    }

    // 読み取り済みの側をもう一度読み取る前に聞く。「いいえ」なら何もしない(機器は動かない)。
    private bool ConfirmReadAgain(string name) =>
        _confirm($"{name}を読み取り直しますか？",
            $"{name}はもう読み取ってあります。もう一度、最初から読み取りますか？\r\n" +
            "（今の値は、読み取り直しが終わるまで残ります。途中で中止すれば今の値のままです）\r\n\r\n" +
            "「はい」：読み取り直す\r\n「いいえ」：今の値のままにする");

    // 読み取った 2 点を、first から 4 欄に入れ、入れた文字を返す。
    private string[] Fill(int first, ScalingPoints points)
    {
        float[] read = [points.Input1, points.Output1, points.Input2, points.Output2];
        var texts = new string[read.Length];
        for (int i = 0; i < read.Length; i++)
            _values[first + i].Text = texts[i] = read[i].ToString("G", CultureInfo.InvariantCulture);
        return texts;
    }

    private bool EditedSince(int first, string[]? texts) =>
        texts is null || Enumerable.Range(0, texts.Length).Any(i => _values[first + i].Text.Trim() != texts[i]);

    // 「電流」「圧力」の値の欄。見出し行(計測値・表示値)と、1(低点)・2(高点)の 2 行。
    private Control BuildSection(string name, string unit, int firstIndex, float[] values)
    {
        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 5, RowCount = 3, Margin = new Padding(0, 0, 0, 2) };
        grid.Controls.Add(new Label { Text = "計測値", AutoSize = true, Margin = new Padding(3, 3, 3, 3) }, 1, 0);
        grid.Controls.Add(new Label { Text = "表示値", AutoSize = true, Margin = new Padding(3, 3, 3, 3) }, 3, 0);
        for (int row = 0; row < 2; row++)
        {
            grid.Controls.Add(new Label { Text = (row + 1) + (row == 0 ? "（低点）" : "（高点）"), AutoSize = true, Margin = new Padding(3, 6, 8, 3) }, 0, row + 1);
            // 左の列: 計測値(V)。右の列: 表示値(mA / kPa)。
            int inputIndex = firstIndex + row * 2;
            grid.Controls.Add(MakeBox(inputIndex, values[inputIndex], $"{name} {(row == 0 ? "低点" : "高点")}電圧 (V)"), 1, row + 1);
            grid.Controls.Add(new Label { Text = "V", AutoSize = true, Margin = new Padding(3, 6, 18, 3) }, 2, row + 1);
            grid.Controls.Add(MakeBox(inputIndex + 1, values[inputIndex + 1], $"{name} {(row == 0 ? "低点" : "高点")}実測値 ({unit})"), 3, row + 1);
            grid.Controls.Add(new Label { Text = unit, AutoSize = true, Margin = new Padding(3, 6, 3, 3) }, 4, row + 1);
        }
        return grid;
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

    private bool AskDefaultNo(string title, string message)
    {
        using var dialog = new DefaultNoDialog(message, Font, title);
        return dialog.ShowDialog(this) == DialogResult.Yes;
    }

    // 読み取った値を入れたまま、適用せずに閉じる(キャンセル・Esc・×)ときは聞く(2026-11-14、全体見直し・区切り 4)。
    // それまでは、数分かけて読み取った値が Esc 1 回で確認なしに消えた。Windows の終了などでは聞かない。
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult != DialogResult.OK && (_currentResult is not null || _pressureResult is not null) &&
            e.CloseReason is CloseReason.None or CloseReason.UserClosing &&
            !_confirm("読み取った値を捨てますか？",
                "読み取った値を、適用せずに捨てます。\r\n\r\n「はい」：捨てて校正画面を閉じる\r\n「いいえ」：校正画面に戻る"))
        {
            e.Cancel = true;
            DialogResult = DialogResult.None;
        }
        base.OnFormClosing(e);
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
            // ふつうの値から外れていたら、適用の前に聞く(既定は「いいえ」。2026-11-14、全体見直し・区切り 4)。
            var unusual = result.FindUnusual(Template);
            if (unusual.Count > 0 && !_confirm("校正値を確認してください",
                "入力した校正値に、ふつうの値から外れているものがあります。小数点・桁・入れた欄を確かめてください。\r\n\r\n・" +
                string.Join("\r\n・", unusual) +
                "\r\n\r\nこのまま適用しますか？\r\n「はい」：この値で適用する\r\n「いいえ」：校正画面に戻って直す"))
                return;
            Calibration = result;
            var gauge = _pressureResult;
            Reading = gauge is null && _currentResult is null
                ? CalibrationReading.Manual(SelectedModel)
                : new CalibrationReading(gauge is not null, gauge?.Model ?? SelectedModel, gauge?.GaugePort, gauge?.SinceGaugeConfirmed, gauge?.Zero.Describe(),
                    EditedAfterRead: gauge is not null && EditedSince(4, _pressureTexts),
                    CurrentMethod: _currentResult?.MethodText,
                    CurrentEditedAfterRead: _currentResult is not null && EditedSince(0, _currentTexts));
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (ArgumentException ex)
        {
            ErrorNotification.Show(this, ex.Message, "校正値を確認してください", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
