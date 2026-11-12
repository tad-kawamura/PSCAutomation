using PSCAutomation.Apps.AS110AllCheck;

// AS110-116B350 の出荷データ(データ取り２。SPEC「AS110-116B350 の出荷データ」)の工程状態。画面・機器・Excel なし。
internal static class ShippingDataTests
{
    private static void Check(bool b, string name) { if (!b) throw new Exception(name); Console.WriteLine("PASS " + name); }

    public static void Run()
    {
        var judged = ProductModel.All.First(m => m.Name == "AS110-116B");
        var shipping = ShippingDataModels.For(judged);
        Check(shipping?.Name == "AS110-116B350" && shipping.SupplyPressure == 350, "AS110-116B has the 350 kPa shipping data model");
        Check(ProductModel.All.Where(m => m.Name != "AS110-116B").All(m => ShippingDataModels.For(m) is null), "other models have no shipping data stage");
        Check(ShippingDataModels.FolderName(shipping!) == "出荷データ(350kPa)", "shipping data folder name");

        var flow = new ProductionWorkflow { Mode = TestMode.Production };
        flow.ConfigureFor("075633", shipping!, judged);
        Check(flow.ShippingData && flow.Model == shipping && flow.TemplateModel == judged, "shipping data uses the judged model's Excel template");
        flow.BeginMeasurement(new DateTime(2026, 11, 12)); flow.CaptureSucceeded(); flow.TransferSucceeded();
        Check(flow.BaseName == "AS110-116B350_SN075633_20261112_6500", "shipping data file name uses AS110-116B350");
        Check(!flow.CanComplete, "shipping data cannot complete before save");
        flow.SaveSucceeded();
        Check(flow.CanComplete && !flow.LeakNeedsOverride && flow.LeakChecks == 0, "shipping data completes after save without leak test");
        bool thrown = false;
        try { flow.LeakStarted(); } catch (InvalidOperationException) { thrown = true; }
        Check(thrown, "shipping data does not run the leak test");
        flow.Advance();
        Check(flow.Serial == "075634" && flow.ShippingData && !flow.CanComplete, "shipping data stays selected for the next serial");

        flow.ConfigureFor("075634", judged, null);
        Check(!flow.ShippingData && flow.TemplateModel == judged && flow.Revision > 0, "switching back to 490 kPa is a normal data acquisition");
        flow.BeginMeasurement(DateTime.Now); flow.CaptureSucceeded(); flow.TransferSucceeded(); flow.SaveSucceeded();
        Check(!flow.CanComplete, "490 kPa data acquisition still needs the leak test");
        flow.ConfigureFor("075634", shipping!, judged);
        Check(!flow.HasCapture && flow.ShippingData, "changing the supply pressure stage discards the measurement");
        flow.Configure("075634", judged);
        Check(!flow.ShippingData, "existing Configure keeps the normal stage");

        var adjustment = new ProductionWorkflow { Mode = TestMode.Production };
        adjustment.ConfigureWithoutSerialFor(shipping!, judged);
        Check(adjustment.ShippingData && adjustment.SerialMissing, "shipping data without serial cannot start");
        adjustment.Mode = TestMode.Adjustment; adjustment.ConfigureAdjustment(judged);
        Check(!adjustment.ShippingData && adjustment.JudgedModel is null, "adjustment is never shipping data");

        // 完了記録はデータ取り(490 kPa)の保存先と、その中の出荷データの保存先で別。両方のロットを同時に持てる。
        string root = Path.Combine(Path.GetTempPath(), "AS110ShippingTest-" + Guid.NewGuid().ToString("N"), "SN75633-75672");
        try
        {
            var judgedStore = new CompletionStore(root);
            var shippingStore = new CompletionStore(Path.Combine(root, ShippingDataModels.FolderName(shipping!)));
            using (judgedStore.Lock()) judgedStore.Append("COMPLETE", "75633", judged.Name, "AS110-116B_SN75633_20261112_6500");
            using (var heldShipping = shippingStore.Lock())
            using (var heldJudged = judgedStore.Lock())
            {
                Check(judgedStore.IsComplete("075633") && !judgedStore.IsComplete("75634"), "490 kPa pass is read from the data acquisition folder");
                Check(!shippingStore.IsComplete("75633"), "shipping data has its own completion record");
                shippingStore.Append("COMPLETE", "75633", shipping!.Name, "AS110-116B350_SN75633_20261112_6500");
            }
            Check(shippingStore.IsComplete("75633") && judgedStore.Entries().Length == 1, "completing shipping data does not touch the 490 kPa record");
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(root)!, true); } catch (IOException) { } }
    }
}
