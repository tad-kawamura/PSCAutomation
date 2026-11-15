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
            // ふつうの値から外れている校正値は、適用の前に聞く(2026-11-14、全体見直し・区切り 4)。止めはしない(Validate は通る)。
            var template = new As110Calibration(new(-0.002f, 0.01f, 0.993f, 30f), new(-0.001f, 0f, 5.524f, 540f));
            Check(valid.FindUnusual(template).Count == 0 && valid.FindUnusual(null).Count == 0 && template.FindUnusual(template).Count == 0,
                "usual calibration values are not questioned");
            // アンプの設定で電流の電圧は変わる(3 V = 30 mA・5 V = 30 mA)。雛形(1 V)と違っても聞かない。
            Check((valid with { Current = new(0.001f, 0f, 2.992f, 29.9f) }).FindUnusual(template).Count == 0 &&
                (valid with { Current = new(0.001f, 0f, 4.98f, 29.9f) }).FindUnusual(template).Count == 0,
                "current voltage of another amplifier setting is not questioned");
            // 供給圧力の違う型式(350〜588 kPa)でも、1 V あたりの kPa は同じなので聞かない。
            Check((valid with { Pressure = new(0.001f, 0f, 3.58f, 350f) }).FindUnusual(template).Count == 0,
                "pressure high point of another supply pressure is not questioned");
            foreach (var (unusual, word, name) in new[]
            {
                (valid with { Pressure = new(0.001f, 0.1f, 5503f, 539.5f) }, "圧力の 1 V あたり", "missing decimal point in the pressure voltage"),
                (valid with { Pressure = new(0.001f, 0.1f, 5.503f, 53.95f) }, "圧力の 1 V あたり", "pressure kPa one digit short"),
                (valid with { Pressure = new(0.1f, 0.001f, 539.5f, 5.503f) }, "圧力の 1 V あたり", "pressure V and kPa columns swapped"),
                (valid with { Pressure = new(0.001f, 100f, 5.503f, 639.5f) }, "圧力の低点", "pressure low point far from 0 kPa"),
                (valid with { Current = new(-0.002f, 0.01f, 0.998f, 300.2f) }, "電流の高点の表示値", "current mA ten times too large"),
                (valid with { Current = new(0.01f, -0.002f, 30.02f, 0.998f) }, "電流の高点の表示値", "current V and mA columns swapped"),
                (valid with { Current = new(-0.002f, 0.01f, 998f, 30.02f) }, "電流の高点の計測値", "missing decimal point in the current voltage"),
                (valid with { Current = new(-0.002f, 10f, 0.998f, 30.02f) }, "電流の低点", "current low point far from 0 mA"),
            })
            {
                unusual.Validate();
                Check(unusual.FindUnusual(template).Any(line => line.Contains(word)), "questioned before applying: " + name);
            }
            Check((valid with { Pressure = new(0.001f, 0.1f, 5503f, 539.5f) }).FindUnusual(null).Count == 0,
                "without a template the pressure slope is not compared");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
