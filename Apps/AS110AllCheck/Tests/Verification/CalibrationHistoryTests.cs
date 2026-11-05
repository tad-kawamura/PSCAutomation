using System.Text;
using System.Text.Json;
using PSCAutomation.Apps.AS110AllCheck;

internal static class CalibrationHistoryTests
{
    public static void Run()
    {
        static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            Console.WriteLine("PASS " + name);
        }
        string folder = Path.Combine(Path.GetTempPath(), "as110-history-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "校正記録.txt");
        var store = new CalibrationHistory(path);
        var calibration = new As110Calibration(new(-0.002f, 0.01f, 0.998f, 30.02f), new(-0.001f, 0.1f, 5.503f, 539.5f));
        DateTimeOffset now = new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.FromHours(9));
        CalibrationRecord Read(string line) => JsonSerializer.Deserialize<CalibrationRecord>(line)!;
        string[] Lines() => File.ReadAllLines(path, Encoding.UTF8).Where(l => l.Length > 0).ToArray();
        try
        {
            store.Append(new(now, calibration));
            byte[] firstBytes = File.ReadAllBytes(path);
            var first = Read(Lines()[0]);
            Check(Lines().Length == 1 && first.AppliedAt == now && first.Calibration == calibration && first.Version == 2,
                "record keeps applied time and all eight float values");
            string text = File.ReadAllText(path);
            Check(!text.Contains("Stand") && !text.Contains("Hardware") && !text.Contains("EquipmentId"),
                "record has no stand, hardware or equipment fields");
            // 旧形式（機器識別名つき）の行が残っていても、読み直さずに追記する。
            File.AppendAllText(path, "{\"Id\":\"old\",\"EquipmentId\":\"管理番号001\"}", new UTF8Encoding(false));
            byte[] withOld = File.ReadAllBytes(path);
            var changed = calibration with { Pressure = calibration.Pressure with { Input2 = 5.498f, Output2 = 540.5f } };
            store.Append(new(now.AddDays(1), changed));
            var lines = Lines();
            Check(lines.Length == 3 && File.ReadAllBytes(path).AsSpan(0, withOld.Length).SequenceEqual(withOld) &&
                Read(lines[2]).Calibration == changed && withOld.AsSpan(0, firstBytes.Length).SequenceEqual(firstBytes),
                "append keeps prior bytes, including old-format lines, and separates a final line without newline");
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                bool locked = false;
                try { store.Append(new(now, calibration)); } catch (IOException) { locked = true; }
                Check(locked, "concurrent calibration writers are excluded");
            }
            byte[] beforeInvalid = File.ReadAllBytes(path);
            bool invalid = false;
            var reversed = calibration with { Current = calibration.Current with { Input2 = -1 } };
            try { store.Append(new(now, reversed)); } catch (ArgumentException) { invalid = true; }
            Check(invalid && beforeInvalid.SequenceEqual(File.ReadAllBytes(path)), "invalid calibration cannot alter calibration text");
            // 保存先がファイルの場合も成功扱いにしない。
            var blocked = new CalibrationHistory(Path.Combine(path, "校正記録.txt"));
            bool saveFailed = false;
            try { blocked.Append(new(now, calibration)); } catch (IOException) { saveFailed = true; }
            Check(saveFailed, "unwritable history location reports save failure");
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
