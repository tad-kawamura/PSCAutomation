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
        Form Dialog() => (Form)Activator.CreateInstance(dialogType, "門馬さんの隣のスタンド", "NR-500", initial)!;
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
            // 入力欄は8項目だけ。機器識別名などの入力・前回値の呼び出しは持たない。
            if (Descendants(dialog).OfType<TextBox>().Count() != 8 || Descendants(dialog).OfType<ComboBox>().Any() ||
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
