using System.Reflection;
using System.Windows.Forms;

// 実 MainForm で、AS110-116B のデータ取りの「供給圧力」と、350 kPa の出荷データ(データ取り２)の画面の状態を確かめる(機器・Excel なし)。
// SPEC「AS110-116B350 の出荷データ(データ取り２)」。
internal static class ShippingDataFormTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(Form form, Type type, Assembly assembly)
    {
        object Field(string name) => type.GetField(name, Private)!.GetValue(form)!;
        void SetField(string name, object? value) => type.GetField(name, Private)!.SetValue(form, value);
        void Call(string name) => type.GetMethod(name, Private)!.Invoke(form, null);
        void Configure() => type.GetMethod("ConfigureProduct", Private)!.Invoke(form, [false]);
        var getState = typeof(Control).GetMethod("GetState", Private)!;
        bool LocalVisible(Control control) => (bool)getState.Invoke(control, [2])!;
        var flow = Field("_flow"); var flowType = flow.GetType();
        bool FlowBool(string name) => (bool)flowType.GetProperty(name)!.GetValue(flow)!;
        var models = (Array)assembly.GetType("PSCAutomation.Apps.AS110AllCheck.ProductModel")!.GetField("All")!.GetValue(null)!;
        object Model(string name) => models.Cast<object>().First(m => (string)m.GetType().GetProperty("Name")!.GetValue(m)! == name);
        var modeBox = (ComboBox)Field("_modeBox");
        var serialBox = (TextBox)Field("_serialBox");
        var rootBox = (TextBox)Field("_rootBox");
        var panel = (Control)Field("_supplyPressurePanel");
        var box = (ComboBox)Field("_supplyPressureBox");
        var notice = (Label)Field("_shippingNoticeLabel");

        modeBox.SelectedIndex = 2; Call("ChangeTestMode");
        flowType.GetMethod("Invalidate")!.Invoke(flow, null);
        SetField("_selectedModel", Model("AS110-035")); Configure(); Call("UpdateProductionControls");
        if (LocalVisible(panel)) throw new Exception("Supply pressure must be hidden for a model without a shipping data stage");
        SetField("_selectedModel", Model("AS110-116B")); Configure(); Call("UpdateProductionControls");
        if (!LocalVisible(panel) || box.Items.Count != 2 || box.SelectedIndex != 0 || !((string)box.Items[1]!).Contains("AS110-116B350") || LocalVisible(notice))
            throw new Exception("AS110-116B must show the supply pressure choice at 490 kPa");
        Console.WriteLine("PASS supply pressure choice appears only for AS110-116B and starts at 490 kPa");

        string root = Path.Combine(Path.GetTempPath(), "AS110AllCheck-shipping-" + Guid.NewGuid().ToString("N"), "SN75633-75672");
        Directory.CreateDirectory(root);
        try
        {
            var storeType = assembly.GetType("PSCAutomation.Apps.AS110AllCheck.CompletionStore")!;
            object judgedStore = Activator.CreateInstance(storeType, root)!;
            using (var held = (IDisposable)storeType.GetMethod("Lock")!.Invoke(judgedStore, null)!)
                storeType.GetMethod("Append")!.Invoke(judgedStore, ["COMPLETE", "75633", "AS110-116B", "AS110-116B_SN75633_20261112_6500"]);
            rootBox.Text = root; Call("ApplySaveRootRange");
            serialBox.Text = "75634";
            SetField("_shippingDataSelected", true); Configure(); Call("UpdateProductionControls");
            string saveRoot = (string)type.GetProperty("SaveRootPath", Private)!.GetValue(form)!;
            if (!FlowBool("ShippingData") || box.SelectedIndex != 1 || !LocalVisible(notice) || !notice.Text.Contains("350 kPa"))
                throw new Exception("350 kPa must switch to shipping data with the notice: " + notice.Text);
            if (saveRoot != Path.Combine(root, "出荷データ(350kPa)")) throw new Exception("Shipping data must be saved inside the data acquisition folder: " + saveRoot);
            if ((bool)Field("_judgedPassed") || ((Button)Field("_startButton")).Enabled)
                throw new Exception("A serial not completed at 490 kPa must block shipping data");
            if (LocalVisible((Control)Field("_leakOutputButton")) || LocalVisible((Control)Field("_leakButton")) || LocalVisible((Control)Field("_leakSettingsPanel")) || ((Button)Field("_completeButton")).Text != "完了")
                throw new Exception("Shipping data must hide the leak test and show 完了");
            Console.WriteLine("PASS 350 kPa: shipping data folder, notice, no leak test, serial not passed at 490 kPa is blocked");

            serialBox.Text = "75633"; Configure(); Call("UpdateProductionControls");
            if (!(bool)Field("_judgedPassed") || (bool)Field("_serialCompleted")) throw new Exception("A serial completed at 490 kPa must be allowed for shipping data");

            // データ取りの完了記録を読めないとき(ここではロックのファイルを先に握って読めなくする)は、合格していない扱いで止め、
            // 「合格していません」ではなく、先に確かめること → 再起動の順の案内を出す(2026-11-13、人の指示)。読めるようになれば選び直しで戻る。
            using (new FileStream(Path.Combine(root, "完了記録.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Configure(); Call("UpdateProductionControls");
                // 機器なしでは「次にすること」は未接続の案内が先に出るので、出荷データの部分の案内を直接見る。
                string blocked = (string)type.GetProperty("JudgedReadFailedNextStep", Private)!.GetValue(form)!;
                if ((bool)Field("_judgedPassed") || !(bool)type.GetProperty("JudgedPassUnreadable", Private)!.GetValue(form)! || ((Button)Field("_startButton")).Enabled
                    || !blocked.Contains("読めず") || !blocked.Contains("再起動") || blocked.Contains("合格していません"))
                    throw new Exception("An unreadable completion record must block with the check-then-restart guidance: " + blocked);
                try { type.GetMethod("ConfigureProduct", Private)!.Invoke(form, [true]); throw new Exception("Starting must stop when the completion record cannot be read"); }
                catch (TargetInvocationException ex) when (ex.InnerException is IOException io)
                {
                    string m = io.Message;
                    if (!(m.Contains("1. ") && m.Contains("エクスプローラー") && m.Contains("完了記録.txt") && m.Contains("保存先選択") && m.IndexOf("アプリを再起動", StringComparison.Ordinal) > m.IndexOf("1. ", StringComparison.Ordinal)))
                        throw new Exception("The dialog must list the checks before restarting: " + m);
                }
            }
            Configure(); Call("UpdateProductionControls");
            if (!(bool)Field("_judgedPassed")) throw new Exception("Selecting again after the record becomes readable must allow shipping data");
            Console.WriteLine("PASS unreadable 490 kPa completion record: blocked with checks first, then restart; readable again is allowed");
            flowType.GetMethod("BeginMeasurement")!.Invoke(flow, [DateTime.Now]); flowType.GetMethod("CaptureSucceeded")!.Invoke(flow, null); flowType.GetMethod("TransferSucceeded")!.Invoke(flow, null);
            SetField("_lastExcelPass", true); SetField("_excelJudgmentStatus", "判定なし"); Call("UpdateProductionControls");
            if (!((Button)Field("_saveButton")).Enabled || box.Enabled) throw new Exception("Shipping data transfer must allow save and lock the supply pressure");
            flowType.GetMethod("SaveSucceeded")!.Invoke(flow, null); SetField("_reviewStage", true); Call("UpdateProductionControls");
            string signature = (string)type.GetMethod("BuildBoardDisplaySignature", Private)!.Invoke(form, null)!;
            string[] parts = signature.Split(':');
            if (!FlowBool("CanComplete") || parts[1][0] != '0' || parts[1][1] != '0' || parts[3] != "-" || parts[4] != "-" || parts[5] != "完了")
                throw new Exception("Saved shipping data must complete without leak test on M5Stack C: " + signature);
            if (!((Label)Field("_flowLabel")).Text.Contains("出荷データ")) throw new Exception("Status must show shipping data: " + ((Label)Field("_flowLabel")).Text);
            Console.WriteLine("PASS shipping data: save without judgment, then M5Stack shows -/-/完了 and complete is allowed without leak test: " + signature);

            // 画面の見た目の確認用の画像。
            static void CreateHandles(Control control) { _ = control.Handle; foreach (Control child in control.Controls) CreateHandles(child); }
            var production = (Control)Field("_productionPanel");
            SetField("_reviewStage", false); flowType.GetMethod("Invalidate")!.Invoke(flow, null); Call("UpdateProductionControls");
            CreateHandles(production); form.PerformLayout();
            string image = Path.Combine(AppContext.BaseDirectory, "production-panel-shipping.png");
            using (var bitmap = new Bitmap(Math.Max(production.Width, 1), Math.Max(production.Height, 1)))
            { production.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(image); }
            Console.WriteLine("PASS shipping data layout image: " + image);

            SetField("_shippingDataSelected", false); Configure(); Call("UpdateProductionControls");
            if (FlowBool("ShippingData") || !(bool)Field("_judgedPassed") || !LocalVisible((Control)Field("_leakButton")) || ((Button)Field("_completeButton")).Text != "合格" || box.SelectedIndex != 0)
                throw new Exception("Back to 490 kPa must restore the normal data acquisition");
            SetField("_shippingDataSelected", true); serialBox.Text = "75634"; Configure();
            modeBox.SelectedIndex = 1; Call("ChangeTestMode"); Call("UpdateProductionControls");
            if ((bool)Field("_shippingDataSelected") || FlowBool("ShippingData") || !(bool)Field("_judgedPassed"))
                throw new Exception("Changing the mode must return to 490 kPa and clear the shipping data check");
            Console.WriteLine("PASS back to 490 kPa and mode change restore the normal data acquisition");
        }
        finally
        {
            modeBox.SelectedIndex = 2; Call("ChangeTestMode");
            SetField("_shippingDataSelected", false); SetField("_reviewStage", false);
            rootBox.Text = ""; serialBox.Text = ""; Call("ApplySaveRootRange"); Configure();
            flowType.GetMethod("Invalidate")!.Invoke(flow, null); Call("UpdateProductionControls");
            try { Directory.Delete(Path.GetDirectoryName(root)!, true); } catch (IOException) { }
        }
    }
}
