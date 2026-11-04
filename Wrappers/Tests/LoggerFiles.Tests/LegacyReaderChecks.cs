using PSCAutomation.Wrappers.LoggerFiles;
using static TestSupport;

// 元: XdtConversion Verification/Program.cs の「5 種類の全点照合」(--xdt-only の範囲)と、XdtReader の異常系。
// AS110AllCheck / AS110NoiseTest が使っていた XdtReader.Inspect / Read の互換入口を確認する。
internal static class LegacyReaderChecks
{
    public static void Run(string fixture)
    {
        foreach (string name in new[] { "b", "loggerX", "c", "PC", "CR" })
        {
            using var gold = new BinaryReader(File.OpenRead(Path.Combine(fixture, name + ".bin")));
            int n = gold.ReadInt32(), count = gold.ReadInt32();
            var expected = new Dictionary<int, double[]>();
            for (int j = 0; j < count; j++)
            {
                int ch = gold.ReadInt32();
                var values = new double[n];
                for (int i = 0; i < n; i++) values[i] = gold.ReadDouble();
                expected.Add(ch, values);
            }
            var result = XdtReader.Read(Path.Combine(fixture, name + ".xdt"), expected.Keys);
            Ensure(result.Info.Samples == n, name + " 点数相違");
            Ensure(XdtReader.Inspect(Path.Combine(fixture, name + ".xdt")).ChannelCount == count, name + " CH 数相違");
            foreach (var pair in expected)
            {
                double max = result.Values[pair.Key].Zip(pair.Value, (a, b) => Math.Abs(a - b)).Max();
                Ensure(max <= 0.0002, $"{name} CH{pair.Key} 数値相違 {max}");
            }
        }
        Console.WriteLine("PASS XdtReader.Read: b / loggerX / c / PC / CR 全点照合");

        string temp = CreateTempFolder("LoggerFilesReader");
        try
        {
            string pc = Path.Combine(fixture, "PC.xdt");
            Throws<InvalidDataException>(() => XdtReader.Read(pc, new[] { 3 }), "存在しない CH");
            string truncated = Path.Combine(temp, "truncated.xdt");
            File.WriteAllBytes(truncated, File.ReadAllBytes(pc)[..^1]);
            Throws<InvalidDataException>(() => XdtReader.Inspect(truncated), "破損ファイル");
            Console.WriteLine("PASS XdtReader: 存在しない CH・破損ファイルを例外にする");
        }
        finally { Directory.Delete(temp, true); }
    }
}
