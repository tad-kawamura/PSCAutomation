using System.Buffers.Binary;
using PSCAutomation.Apps.AS110AllCheck;

internal static class CalibrationTests
{
    public static void Run()
    {
        static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            Console.WriteLine("PASS " + name);
        }
        string folder = Path.Combine(Path.GetTempPath(), "as110-calibration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var current = new ScalingPoints(-0.002f, 0.01f, 0.998f, 30.02f);
            foreach (string fixture in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "*.xdt"))
            {
                byte[] original = File.ReadAllBytes(fixture);
                uint signature = BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(16, 4));
                int first = signature == 0x00030003 ? 820 : 1140;
                int stride = signature == 0x00030003 ? 68 : 92;
                var initialEditor = new XdtScalingEditor(fixture);
                // 校正画面の初期値は雛形の値そのまま(スタンド公称値は 2026-11-06 に廃止)。
                var initial = As110Calibration.FromTemplate(initialEditor);
                Check(initial.Current == initialEditor.GetScaling(1) && initial.Pressure == initialEditor.GetScaling(2),
                    "calibration defaults are the template's own CH1/CH2 scaling");
                Console.WriteLine($"  template {Path.GetFileName(fixture)}: CH1 {initial.Current} / CH2 {initial.Pressure}");
                foreach (var pressure in new[] { new ScalingPoints(-0.001f, 0.2f, 5.503f, 539.5f),
                    new ScalingPoints(0.002f, -0.1f, 5.498f, 540.5f) })
                {
                    var calibration = new As110Calibration(current, pressure);
                    var editor = new XdtScalingEditor(fixture);
                    calibration.Apply(editor);
                    string output = Path.Combine(folder, Path.GetFileName(fixture));
                    editor.Save(output);
                    var saved = new XdtScalingEditor(output);
                    Check(saved.GetScaling(1) == current && saved.GetScaling(2) == pressure,
                        "both measured low/high calibration points survive XDT round trip");
                    byte[] modified = File.ReadAllBytes(output);
                    Check(original.Length == modified.Length && Enumerable.Range(0, original.Length)
                        .All(i => (i >= first && i < first + 16) || (i >= first + stride && i < first + stride + 16) || original[i] == modified[i]),
                        "XDT changes only CH1/CH2 scaling; waveform, unit, averaging and other settings preserved");
                    var info = XdtReader.Inspect(output);
                    foreach (var (channel, points) in new[] { (1, current), (2, pressure) })
                    {
                        var ch = info.Channels.Single(c => c.Number == channel);
                        Check(Math.Abs(ch.Gain * points.Input1 + ch.Offset - points.Output1) < 1e-8 &&
                            Math.Abs(ch.Gain * points.Input2 + ch.Offset - points.Output2) < 1e-8,
                            "XDT converter maps both measured endpoints on CH" + channel);
                    }
                    Check(File.ReadAllBytes(fixture).SequenceEqual(original), "source template unchanged");
                }
            }
            // 新LOGGERのXDTヘッダ位置も、波形を持たない最小ファイルで確認する。
            byte[] loggerX = new byte[1400];
            BinaryPrimitives.WriteUInt32LittleEndian(loggerX.AsSpan(16, 4), 0x00030101);
            foreach (int offset in new[] { 1140, 1232 })
                BinaryPrimitives.WriteInt32LittleEndian(loggerX.AsSpan(offset - 4, 4), 1);
            string synthetic = Path.Combine(folder, "logger-x.xdt");
            File.WriteAllBytes(synthetic, loggerX);
            var xEditor = new XdtScalingEditor(synthetic);
            var valid = new As110Calibration(current, new(0.001f, 0.1f, 5.503f, 539.5f));
            valid.Apply(xEditor);
            xEditor.Save(synthetic);
            Check(new XdtScalingEditor(synthetic).GetScaling(2) == valid.Pressure, "LOGGER X XDT channel stride");
            foreach (var bad in new[] { current with { Input2 = current.Input1 }, current with { Input2 = -1 },
                current with { Output2 = current.Output1 }, current with { Output2 = -1 },
                current with { Input1 = float.NaN }, current with { Output2 = float.PositiveInfinity } })
            {
                foreach (bool pressureInvalid in new[] { false, true })
                {
                    var invalid = pressureInvalid ? valid with { Pressure = bad } : valid with { Current = bad };
                    var editor = new XdtScalingEditor(synthetic);
                    bool rejected = false;
                    try { invalid.Apply(editor); } catch (ArgumentException) { rejected = true; }
                    Check(rejected && editor.GetScaling(1) == current && editor.GetScaling(2) == valid.Pressure,
                        "invalid endpoints rejected before changing either channel");
                }
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
