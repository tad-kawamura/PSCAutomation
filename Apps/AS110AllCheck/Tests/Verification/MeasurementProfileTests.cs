using PSCAutomation.Apps.AS110AllCheck;

// 型式ごとの測り方(SPEC「AS110-006 の雛形と測り方」)と、末尾の点数をやめたファイル名。画面・機器・Excel なし。
internal static class MeasurementProfileTests
{
    private static void Check(bool b, string name) { if (!b) throw new Exception(name); Console.WriteLine("PASS " + name); }

    public static void Run()
    {
        var m006 = ProductModel.All.First(m => m.Name == "AS110-006");
        var m035 = ProductModel.All.First(m => m.Name == "AS110-035");
        Check(MeasurementProfile.LengthFactor(m006) == 2 && MeasurementProfile.HasOwnLoggerTemplate(m006), "AS110-006 measures twice as long with its own LOGGER template");
        Check(ProductModel.All.Where(m => m.Name != "AS110-006").All(m => MeasurementProfile.LengthFactor(m) == 1 && !MeasurementProfile.HasOwnLoggerTemplate(m)),
            "other models keep the common measurement");
        Check(MeasurementProfile.LoggerTemplateName("AS110データ調整・データ取り用", m006) == "AS110データ調整・データ取り用_AS110-006"
            && MeasurementProfile.LoggerTemplateName("AS110データ調整・データ取り_NRX100", m006) == "AS110データ調整・データ取り_NRX100_AS110-006"
            && MeasurementProfile.LoggerTemplateName("AS110データ調整・データ取り用", m035) == "AS110データ調整・データ取り用",
            "LOGGER template names: AS110-006 has its own, others use the common one");

        // 発振器: データ調整・データ取りは周波数が半分(WF1973 0.02 Hz、SDG1032X 0.04 Hz)。振幅などは同じ。動き検査は変えない。
        var wf = As110MotionTest.GetSettings(isSdg1032X: false, isMotion: false, lengthFactor: 2);
        var sdg = As110MotionTest.GetSettings(isSdg1032X: true, isMotion: false, lengthFactor: 2);
        Check(Math.Abs(wf.FrequencyHz - 0.02) < 1e-12 && Math.Abs(sdg.FrequencyHz - 0.04) < 1e-12, "AS110-006 data frequency is 0.02 Hz (WF1973) and 0.04 Hz (SDG1032X)");
        Check(wf with { FrequencyHz = 0.04 } == As110MotionTest.DataSettings && sdg with { FrequencyHz = 0.08 } == As110MotionTest.SdgDataSettings,
            "AS110-006 keeps amplitude, offset, cycles and start phase");
        Check(As110MotionTest.GetSettings(false, true, 2) == As110MotionTest.Settings && As110MotionTest.GetSettings(true, true, 2) == As110MotionTest.SdgSettings,
            "motion test frequency does not depend on the model");
        Check(As110MotionTest.GetSettings(false, false, 1) == As110MotionTest.DataSettings && As110MotionTest.GetSettings(true, false, 1) == As110MotionTest.SdgDataSettings,
            "other models keep 0.04 / 0.08 Hz");

        // 転送: 13000 点・10 点平均 → 1300 点。元の設定(画面で直す値)は変えない。
        var settings = new TransferSettings();
        var t006 = settings.ForModel(m006);
        Check(t006.ExpectedSamples == 13000 && t006.Compression == 10 && settings.ExpectedSamples == 6500 && settings.Compression == 5,
            "AS110-006 transfers 13000 points averaged by 10 without changing the saved settings");
        Check(TransferSettings.Compress(new double[t006.ExpectedSamples], t006.Compression).Length == 1300, "AS110-006 still writes 1300 points to Excel");
        var t035 = settings.ForModel(m035);
        Check(t035.ExpectedSamples == 6500 && t035.Compression == 5 && !ReferenceEquals(t035, settings), "other models keep 6500 points averaged by 5");
        settings.ExpectedSamples = 7000; settings.Compression = 7;
        Check(settings.ForModel(m006).ExpectedSamples == 14000 && settings.ForModel(m006).Compression == 14, "AS110-006 follows the transfer settings times two");

        // 漏れ試験の最大値の下限: AS110-006 は 550 kPa。前の版の設定ファイル(006 が無い)にも足す。人が入れた値は変えない。
        Check(new TransferSettings().LeakMaximumLowerLimitKpa["AS110-006"] == 550.0, "AS110-006 leak maximum lower limit defaults to 550 kPa");
        var old = TransferSettings.FromJson("{\"LeakMaximumLowerLimitKpa\":{\"AS110-035\":531}}");
        Check(old.LeakMaximumLowerLimitKpa["AS110-006"] == 550.0 && old.LeakMaximumLowerLimitKpa["AS110-035"] == 531.0, "older settings files get the AS110-006 default");

        // ファイル名: 末尾の _6500 をやめた。前の版の名前も読む。
        var flow = new ProductionWorkflow { Mode = TestMode.Production };
        flow.ConfigureFor("068431", m006, null);
        flow.BeginMeasurement(new DateTime(2026, 11, 13));
        Check(flow.BaseName == "AS110-006_SN068431_20261113" && flow.LegacyBaseName == "AS110-006_SN068431_20261113_6500", "file name has no point count; legacy name keeps _6500");
        string root = Path.Combine(Path.GetTempPath(), "AS110AllCheck.Verification", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Excel")); Directory.CreateDirectory(Path.Combine(root, "PDF"));
            File.WriteAllText(Path.Combine(root, "Excel", "AS110-006_SN68431_20261113.xlsx"), "");
            File.WriteAllText(Path.Combine(root, "PDF", "AS110-035_SN74233_20261019_6500.pdf"), "");
            File.WriteAllText(Path.Combine(root, "PDF", "AS110-035_SN74234_2026101.pdf"), "");
            var (files, unreadable) = SaveFolder.Scan(root);
            Check(files.Count == 2 && files[0] == ("68431", "AS110-006", "AS110-006_SN68431_20261113") && files[1] == ("74233", "AS110-035", "AS110-035_SN74233_20261019_6500") && unreadable == 1,
                "save folder reads new and legacy file names");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
