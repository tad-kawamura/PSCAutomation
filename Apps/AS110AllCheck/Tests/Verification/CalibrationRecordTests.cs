using System.Text;
using PSCAutomation.Apps.AS110AllCheck;

// 校正記録: データ取りの保存先の「校正記録」フォルダーへ、適用日時の名前のテキストを 1 件 1 ファイルで残す。
internal static class CalibrationRecordTests
{
    public static void Run()
    {
        static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            Console.WriteLine("PASS " + name);
        }
        string root = Path.Combine(Path.GetTempPath(), "as110-record-" + Guid.NewGuid().ToString("N"), "SN71360-71460");
        var calibration = new As110Calibration(new(-0.002f, 0.01f, 0.993f, 30f), new(-0.001f, 0f, 5.524f, 539.8f));
        var now = new DateTimeOffset(2026, 11, 6, 9, 30, 15, TimeSpan.FromHours(9));
        var record = new CalibrationRecord(now, calibration, "データ取り", "NR-500", "AS110データ調整・データ取り用.xdt");
        try
        {
            string path = CalibrationRecordFile.Write(root, record);
            string folder = Path.Combine(root, "校正記録");
            Check(Path.GetDirectoryName(path) == folder && Path.GetFileName(path) == "校正_2026-11-06_09-30-15.txt",
                "record goes to <save root>/校正記録/校正_<applied date and time>.txt");
            string text = File.ReadAllText(path, Encoding.UTF8);
            byte[] bytes = File.ReadAllBytes(path);
            Check(bytes.Length >= 3 && !(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), "record is UTF-8 without BOM");
            foreach (string expected in new[] { "適用日時: 2026/11/06 09:30:15 (+09:00)", "モード: データ取り", "本体: NR-500",
                "雛形: AS110データ調整・データ取り用.xdt", "CH1 電流: 低点 -0.002 V = 0.01 mA、高点 0.993 V = 30 mA",
                "CH2 圧力: 低点 -0.001 V = 0 kPa、高点 5.524 V = 539.8 kPa" })
                Check(text.Contains(expected), "record text has: " + expected);
            // 同じ秒に 2 回適用しても上書きせず、_2 で残す。
            string second = CalibrationRecordFile.Write(root, record with { Calibration = calibration with { Current = calibration.Current with { Output2 = 30.02f } } });
            Check(Path.GetFileName(second) == "校正_2026-11-06_09-30-15_2.txt" && File.ReadAllText(path, Encoding.UTF8) == text &&
                File.ReadAllText(second, Encoding.UTF8).Contains("30.02 mA"), "same-second record gets _2 and never overwrites the first");
            // 不正な校正値は書かない。
            bool invalid = false;
            try { CalibrationRecordFile.Write(root, record with { Calibration = calibration with { Current = calibration.Current with { Input2 = -1 } } }); }
            catch (ArgumentException) { invalid = true; }
            Check(invalid && Directory.GetFiles(folder).Length == 2, "invalid calibration is not recorded");
            bool missingRoot = false;
            try { CalibrationRecordFile.Write("", record); } catch (ArgumentException) { missingRoot = true; }
            Check(missingRoot, "no save root reports an error instead of writing somewhere else");
            // 保存先がファイルの場合(フォルダーを作れない)も成功扱いにしない。
            string blockedRoot = Path.Combine(Path.GetDirectoryName(root)!, "file-as-root");
            File.WriteAllText(blockedRoot, "");
            bool saveFailed = false;
            try { CalibrationRecordFile.Write(blockedRoot, record); } catch (IOException) { saveFailed = true; }
            Check(saveFailed, "unwritable save root reports save failure");
        }
        finally
        {
            string parent = Path.GetDirectoryName(root)!;
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
    }
}
