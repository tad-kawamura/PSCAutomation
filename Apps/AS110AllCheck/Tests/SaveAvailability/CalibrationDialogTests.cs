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

    // 「マルチメータで読み取る」(電流の 4 欄)と「GC16 で読み取る」(圧力の 4 欄)(2026-11-18 に 2 つに分けた): 読み取った値を欄に入れ、
    // 適用で読み取り方を返す。適用の前に手で直したら、その欄の側(電流 / 圧力)に印を付ける。片方だけでもよい。
    private static void RunGaugeReading(Assembly assembly, Type dialogType, Type calibrationType, object initial)
    {
        Type modelType = assembly.GetType("PSCAutomation.Apps.AS110AllCheck.ProductModel")!;
        Type currentType = assembly.GetType("PSCAutomation.Apps.AS110AllCheck.CurrentReadingResult")!;
        Type pressureType = assembly.GetType("PSCAutomation.Apps.AS110AllCheck.PressureReadingResult")!;
        Type zeroType = assembly.GetType("PSCAutomation.Apps.AS110AllCheck.CalibrationZeroCheck")!;
        object model = ((Array)modelType.GetField("All")!.GetValue(null)!).GetValue(0)!; // AS110-035
        object zero = Activator.CreateInstance(zeroType, 0.03, 1, true, 0.1, 0.2)!;
        object currentResult = Activator.CreateInstance(currentType, new ScalingPoints(-0.003f, 0.01f, 0.991f, 29.9f), "7351E")!;
        object pressureResult = Activator.CreateInstance(pressureType, new ScalingPoints(0.002f, 0.03f, 5.401f, 539.43f), model, zero, (TimeSpan?)TimeSpan.FromMinutes(7), "COM7")!;
        int currentCalls = 0, pressureCalls = 0;
        var ownerParameter = System.Linq.Expressions.Expression.Parameter(typeof(IWin32Window));
        var modelParameter = System.Linq.Expressions.Expression.Parameter(modelType);
        Func<object> countCurrent = () => { currentCalls++; return currentResult; };
        Func<object> countPressure = () => { pressureCalls++; return pressureResult; };
        Delegate readCurrent = System.Linq.Expressions.Expression.Lambda(typeof(Func<,>).MakeGenericType(typeof(IWin32Window), currentType),
            System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Invoke(System.Linq.Expressions.Expression.Constant(countCurrent)), currentType),
            ownerParameter).Compile();
        Delegate readPressure = System.Linq.Expressions.Expression.Lambda(typeof(Func<,,>).MakeGenericType(modelType, typeof(IWin32Window), pressureType),
            System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Invoke(System.Linq.Expressions.Expression.Constant(countPressure)), pressureType),
            modelParameter, ownerParameter).Compile();
        Form Open() => (Form)Activator.CreateInstance(dialogType, "NR-500", "今日の校正", initial, readCurrent, readPressure, "初期値は雛形の値です。")!;
        foreach (string scenario in new[] { "both", "both-edit-current", "both-edit-pressure", "current-only" })
        {
            using var dialog = Open();
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(-10000, -10000);
            dialog.Show();
            Application.DoEvents();
            var boxes = (TextBox[])dialogType.GetField("_values", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
            Descendants(dialog).OfType<Button>().Single(b => b.Text == "マルチメータで読み取る").PerformClick();
            if (boxes[1].Text != "0.01" || boxes[2].Text != "0.991" || boxes[3].Text != "29.9" || boxes[7].Text != "539.5")
                throw new Exception("The multimeter reading must fill the four current values only");
            if (scenario != "current-only")
            {
                Descendants(dialog).OfType<ComboBox>().Single().SelectedIndex = 1;
                Descendants(dialog).OfType<Button>().Single(b => b.Text == "GC16 で読み取る").PerformClick();
                if (boxes[4].Text != "0.002" || boxes[7].Text != "539.43" || boxes[3].Text != "29.9")
                    throw new Exception("The GC16 reading must fill the four pressure values only");
            }
            if (scenario == "both-edit-current") boxes[3].Text = "29.95";
            if (scenario == "both-edit-pressure") boxes[7].Text = "539.5";
            Descendants(dialog).OfType<Button>().Single(b => b.Text == "適用").PerformClick();
            object reading = dialogType.GetProperty("Reading")!.GetValue(dialog)!;
            Type readingType = reading.GetType();
            bool Flag(string name) => (bool)readingType.GetProperty(name)!.GetValue(reading)!;
            if (dialog.DialogResult != DialogResult.OK ||
                (string?)readingType.GetProperty("CurrentMethod")!.GetValue(reading) != "マルチメータで読み取り（7351E）" ||
                Flag("ReadWithGauge") != (scenario != "current-only") ||
                Flag("CurrentEditedAfterRead") != (scenario == "both-edit-current") ||
                Flag("EditedAfterRead") != (scenario == "both-edit-pressure") ||
                (scenario != "current-only" && (string?)readingType.GetProperty("GaugePort")!.GetValue(reading) != "COM7"))
                throw new Exception("The readings were not returned as the reading method: " + scenario);
        }
        if (currentCalls != 4 || pressureCalls != 3) throw new Exception("Reading delegates were not called once per click");
        Console.WriteLine("PASS calibration GUI: マルチメータで読み取る fills the current values, GC16 で読み取る the pressure values, edits are marked per side");
        RunConfirmations(dialogType, initial, readCurrent, readPressure);
    }

    // 2026-11-14(全体見直し・区切り 4): ふつうの値から外れた校正値は適用の前に聞く。読み取った値を適用せずに閉じるときも聞く。
    // どちらも「いいえ」なら校正画面に残る。確認の画面は差し替えて、聞いた回数と答えだけを見る。
    private static void RunConfirmations(Type dialogType, object initial, Delegate readCurrent, Delegate readPressure)
    {
        var asked = new List<string>();
        bool answer = false;
        Form Open(bool withReading)
        {
            var dialog = !withReading
                ? (Form)Activator.CreateInstance(dialogType, "NR-500", "AS110データ調整・データ取り用.xdt", initial)!
                : (Form)Activator.CreateInstance(dialogType, "NR-500", "今日の校正", initial, readCurrent, readPressure, "初期値は雛形の値です。")!;
            dialogType.GetProperty("Template")!.SetValue(dialog, initial);
            dialogType.GetField("_confirm", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(dialog, new Func<string, string, bool>((title, message) => { asked.Add(title + "\n" + message); return answer; }));
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(-10000, -10000);
            dialog.Show();
            Application.DoEvents();
            return dialog;
        }
        using (var dialog = Open(false))
        {
            var boxes = (TextBox[])dialogType.GetField("_values", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
            var apply = Descendants(dialog).OfType<Button>().Single(b => b.Text == "適用");
            boxes[6].Text = "5503"; // 小数点の打ち忘れ
            apply.PerformClick();
            if (asked.Count != 1 || !asked[0].Contains("圧力の 1 V あたり") || dialog.DialogResult == DialogResult.OK || !dialog.Visible ||
                !ReferenceEquals(dialogType.GetProperty("Calibration")!.GetValue(dialog), initial))
                throw new Exception("Unusual calibration values were applied without asking, or いいえ did not keep the dialog");
            answer = true;
            apply.PerformClick();
            if (asked.Count != 2 || dialog.DialogResult != DialogResult.OK)
                throw new Exception("はい did not apply the unusual calibration values");
        }
        Console.WriteLine("PASS calibration GUI asks before applying unusual values; いいえ stays, はい applies");

        asked.Clear();
        answer = false;
        using (var dialog = Open(true))
        {
            dialog.Close();
            if (asked.Count != 0 || dialog.Visible) throw new Exception("Closing without a reading asked a question");
        }
        foreach (string button in new[] { "マルチメータで読み取る", "GC16 で読み取る" })
        {
            asked.Clear();
            answer = false;
            using var dialog = Open(true);
            Descendants(dialog).OfType<ComboBox>().Single().SelectedIndex = 1;
            Descendants(dialog).OfType<Button>().Single(b => b.Text == button).PerformClick();
            dialog.Close();
            if (asked.Count != 1 || !asked[0].Contains("読み取った値を捨てますか") || !dialog.Visible)
                throw new Exception("Closing discarded the reading without asking, or いいえ closed the dialog: " + button);
            answer = true;
            dialog.Close();
            if (asked.Count != 2 || dialog.Visible) throw new Exception("はい did not close the dialog: " + button);
        }
        asked.Clear();
        using (var dialog = Open(true))
        {
            Descendants(dialog).OfType<ComboBox>().Single().SelectedIndex = 1;
            Descendants(dialog).OfType<Button>().Single(b => b.Text == "GC16 で読み取る").PerformClick();
            Descendants(dialog).OfType<Button>().Single(b => b.Text == "適用").PerformClick();
            if (asked.Count != 0 || dialog.DialogResult != DialogResult.OK || dialog.Visible)
                throw new Exception("Applying the reading asked whether to discard it");
        }
        Console.WriteLine("PASS calibration GUI asks before discarding a reading (either button); applying or closing without one does not ask");

        // 2026-11-20: 読み取りが済んだボタンは「…読み取り直す」に替わり、押すと先に聞く(「いいえ」なら読み取りを始めない)。
        asked.Clear();
        answer = false;
        using (var dialog = Open(true))
        {
            var boxes = (TextBox[])dialogType.GetField("_values", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
            Descendants(dialog).OfType<Button>().Single(b => b.Text == "マルチメータで読み取る").PerformClick();
            var again = Descendants(dialog).OfType<Button>().Single(b => b.Text == "マルチメータで読み取り直す");
            boxes[3].Text = "12345";
            again.PerformClick();
            if (asked.Count != 1 || !asked[0].Contains("電流を読み取り直しますか") || boxes[3].Text != "12345")
                throw new Exception("Reading again must ask first, and いいえ must not read");
            answer = true;
            again.PerformClick();
            if (asked.Count != 2 || boxes[3].Text != "29.9") throw new Exception("はい must read again and fill the values");
        }
        Console.WriteLine("PASS calibration GUI: a finished reading button becomes 読み取り直す and asks before reading again");
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
