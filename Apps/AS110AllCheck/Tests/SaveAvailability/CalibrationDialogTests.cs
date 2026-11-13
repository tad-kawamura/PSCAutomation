using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using PSCAutomation.Wrappers.LoggerFiles;

internal static class CalibrationDialogTests
{
    public static void Run(Assembly assembly)
    {
        var dialogType = assembly.GetType("PSCAutomation.Apps.AS110AllCheck.CalibrationDialog")!;
        var calibrationType = assembly.GetType("PSCAutomation.Apps.AS110AllCheck.As110Calibration")!;
        var current = new ScalingPoints(-0.002f, 0.01f, 0.998f, 30.02f);
        var pressure = new ScalingPoints(-0.001f, 0.2f, 5.503f, 539.5f);
        object initial = Activator.CreateInstance(calibrationType, current, pressure)!;
        Form Dialog() => (Form)Activator.CreateInstance(dialogType, "NR-500", "AS110データ調整・データ取り用.xdt", initial)!;
        object Field(Form form, string name) => dialogType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        void Show(Form dialog)
        {
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(-10000, -10000);
            dialog.Show();
            Application.DoEvents();
        }
        foreach (bool cancel in new[] { false, true })
        {
            using var dialog = Dialog();
            Show(dialog);
            var boxes = (TextBox[])Field(dialog, "_values");
            if (boxes.Length != 8 || boxes[6].Text != "5.503" || boxes[7].Text != "539.5")
                throw new Exception("Calibration initial values changed or were rounded");
            // 入力欄は8項目と、目標の供給圧力の型式(2026-11-13 追加)だけ。機器識別名などの入力・前回値の呼び出しは持たない。
            if (Descendants(dialog).OfType<TextBox>().Count() != 8 ||
                Descendants(dialog).OfType<ComboBox>().Select(c => c.AccessibleName).SequenceEqual(new[] { "型式（供給圧力の目標）" }) == false ||
                Descendants(dialog).OfType<Button>().Select(b => b.Text).Order().SequenceEqual(new[] { "キャンセル", "適用" }.Order()) == false)
                throw new Exception("Calibration dialog has inputs other than the eight scaling values");
            if (dialog.AcceptButton is not null) throw new Exception("Enter applies calibration without the button");
            foreach (Control control in Descendants(dialog))
            {
                if (control.Right > control.Parent!.ClientSize.Width || control.Bottom > control.Parent.ClientSize.Height)
                    throw new Exception("Calibration dialog clips " + control.GetType().Name);
            }
            if (!cancel)
            {
                string screenshot = Path.Combine(AppContext.BaseDirectory, "calibration-dialog.png");
                using var bitmap = new Bitmap(dialog.Width, dialog.Height);
                dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(screenshot);
                boxes[0].Text = "0.001";
                boxes[1].Text = "-0.02";
                boxes[6].Text = "5.498";
                boxes[7].Text = "540.5";
                Descendants(dialog).OfType<Button>().Single(b => b.Text == "適用").PerformClick();
                object result = dialogType.GetProperty("Calibration")!.GetValue(dialog)!;
                if (dialog.DialogResult != DialogResult.OK ||
                    (ScalingPoints)calibrationType.GetProperty("Current")!.GetValue(result)! != current with { Input1 = 0.001f, Output1 = -0.02f } ||
                    (ScalingPoints)calibrationType.GetProperty("Pressure")!.GetValue(result)! != pressure with { Input2 = 5.498f, Output2 = 540.5f })
                    throw new Exception("GUI calibration did not retain measured values");
                Console.WriteLine("PASS calibration GUI has only the eight values and applies both channels with fractional endpoints; layout image: " + screenshot);
            }
            else
            {
                boxes[6].Text = "9";
                ((Button)dialog.CancelButton!).PerformClick();
                if (dialog.DialogResult != DialogResult.Cancel ||
                    !ReferenceEquals(dialogType.GetProperty("Calibration")!.GetValue(dialog), initial))
                    throw new Exception("Cancel modified calibration");
                Console.WriteLine("PASS calibration cancel keeps previous values");
            }
        }
        RunGaugeReading(assembly, dialogType, calibrationType, initial);
    }

    // 「GC16 で読み取る」(2026-11-13): 読み取った値を 8 欄に入れ、適用で読み取り方を返す。適用の前に手で直したら、その印を付ける。
    private static void RunGaugeReading(Assembly assembly, Type dialogType, Type calibrationType, object initial)
    {
        Type modelType = assembly.GetType("PSCAutomation.Apps.AS110AllCheck.ProductModel")!;
        Type resultType = assembly.GetType("PSCAutomation.Apps.AS110AllCheck.AutoCalibrationResult")!;
        Type zeroType = assembly.GetType("PSCAutomation.Apps.AS110AllCheck.CalibrationZeroCheck")!;
        object model = ((Array)modelType.GetField("All")!.GetValue(null)!).GetValue(0)!; // AS110-035
        object read = Activator.CreateInstance(calibrationType, new ScalingPoints(-0.003f, 0f, 0.991f, 29.9f), new ScalingPoints(0.002f, 0.03f, 5.401f, 539.43f))!;
        object zero = Activator.CreateInstance(zeroType, 0.03, 1, true, 0.1, 0.2)!;
        object result = Activator.CreateInstance(resultType, read, model, zero, (TimeSpan?)TimeSpan.FromMinutes(7), "COM7")!;
        Type delegateType = typeof(Func<,,>).MakeGenericType(modelType, typeof(IWin32Window), resultType);
        int calls = 0;
        var modelParameter = System.Linq.Expressions.Expression.Parameter(modelType);
        var ownerParameter = System.Linq.Expressions.Expression.Parameter(typeof(IWin32Window));
        Func<object> count = () => { calls++; return result; };
        var body = System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Invoke(System.Linq.Expressions.Expression.Constant(count)), resultType);
        Delegate readWithGauge = System.Linq.Expressions.Expression.Lambda(delegateType, body, modelParameter, ownerParameter).Compile();
        foreach (bool edit in new[] { false, true })
        {
            using var dialog = (Form)Activator.CreateInstance(dialogType, "NR-500", "AS110データ調整・データ取り用.xdt", initial, readWithGauge)!;
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(-10000, -10000);
            dialog.Show();
            Application.DoEvents();
            var combo = Descendants(dialog).OfType<ComboBox>().Single();
            combo.SelectedIndex = 1;
            Descendants(dialog).OfType<Button>().Single(b => b.Text == "GC16 で読み取る").PerformClick();
            var boxes = (TextBox[])dialogType.GetField("_values", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
            if (boxes[2].Text != "0.991" || boxes[3].Text != "29.9" || boxes[7].Text != "539.43")
                throw new Exception("GC16 reading did not fill the eight values");
            if (edit) boxes[3].Text = "29.95";
            Descendants(dialog).OfType<Button>().Single(b => b.Text == "適用").PerformClick();
            object reading = dialogType.GetProperty("Reading")!.GetValue(dialog)!;
            Type readingType = reading.GetType();
            if (dialog.DialogResult != DialogResult.OK || !(bool)readingType.GetProperty("ReadWithGauge")!.GetValue(reading)! ||
                (bool)readingType.GetProperty("EditedAfterRead")!.GetValue(reading)! != edit ||
                (string?)readingType.GetProperty("GaugePort")!.GetValue(reading) != "COM7")
                throw new Exception("GC16 reading was not returned as the reading method");
        }
        if (calls != 2) throw new Exception("GC16 reading delegate was not called once per click");
        Console.WriteLine("PASS calibration GUI GC16 reading fills the eight values and marks edits before apply");
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
