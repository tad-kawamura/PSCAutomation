using System.Globalization;

namespace PSCAutomation.Apps.AS110AllCheck;

// 入力した電流・圧力の2点校正値を、そのまま作業用XDTへ反映するための画面。
internal sealed class CalibrationDialog : Form
{
    private readonly TextBox[] _values = new TextBox[8];
    public As110Calibration Calibration { get; private set; }

    public CalibrationDialog(string standName, string hardware, As110Calibration initial)
    {
        Calibration = initial;
        Text = "電流・圧力の2点校正";
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        // スタンド選択中のMainFormは最前面に固定されている。最前面でないモーダルはその背後に隠れ、
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
            AutoSize = true, MaximumSize = new Size(680, 0), Text = standName + " ／ " + hardware + "\r\n" +
                "初回値は電流がスタンド公称値、圧力が雛形の値です。実測した値を入力してください。" +
                "\r\n低点（ゼロ付近）・高点それぞれの電圧と、対応する電流／圧力を入力します。",
            Margin = new Padding(0, 0, 0, 14)
        });
        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 5, RowCount = 3 };
        string[] headers = ["", "低点電圧 (V)", "低点実測値", "高点電圧 (V)", "高点実測値"];
        for (int col = 0; col < headers.Length; col++)
            grid.Controls.Add(new Label { Text = headers[col], AutoSize = true }, col, 0);
        float[] values = [initial.Current.Input1, initial.Current.Output1, initial.Current.Input2, initial.Current.Output2,
            initial.Pressure.Input1, initial.Pressure.Output1, initial.Pressure.Input2, initial.Pressure.Output2];
        for (int row = 0; row < 2; row++)
        {
            grid.Controls.Add(new Label { Text = row == 0 ? "CH1 電流 (mA)" : "CH2 圧力 (kPa)", AutoSize = true,
                Margin = new Padding(0, 6, 12, 0) }, 0, row + 1);
            for (int col = 0; col < 4; col++)
            {
                int index = row * 4 + col;
                var box = new TextBox { Text = values[index].ToString("G", CultureInfo.InvariantCulture), Width = 115,
                    AccessibleName = (row == 0 ? "電流 " : "圧力 ") + headers[col + 1], TabIndex = index };
                _values[index] = box;
                grid.Controls.Add(box, col + 1, row + 1);
            }
        }
        root.Controls.Add(grid);
        root.Controls.Add(new Label { AutoSize = true, Margin = new Padding(0, 12, 0, 12),
            Text = "入力した値を作業用XDTへ反映してLOGGERで開き直し、適用日時と値を校正記録に残します。\r\n適用後は参照波形を選択してください。" });
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
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (ArgumentException ex)
        {
            ErrorNotification.Show(this, ex.Message, "校正値を確認してください", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
