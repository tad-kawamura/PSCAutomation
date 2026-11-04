using PSCAutomation.Wrappers.LoggerFiles;
using static TestSupport;

// XdtDisplayRange / XdtTimestamp(元: AS110NoiseTest Conversion/)の検査。モノレポで追加。
internal static class DisplayRangeAndTimestampChecks
{
    public static void Run(string fixture)
    {
        string temp = CreateTempFolder("LoggerFilesDisplay");
        try
        {
            string noise = Path.Combine(fixture, "as110-noise-legacy.xdt");
            var range = XdtDisplayRange.Read(noise, 3);
            Ensure(float.IsFinite(range.Span) && range.Span > 0, "雛形 CH3 の表示レンジ");
            string copy = Path.Combine(temp, "noise.xdt");
            File.Copy(noise, copy);
            Ensure(XdtDisplayRange.Center(copy, 3, 80.26), "中心を変えたのに false");
            var centered = XdtDisplayRange.Read(copy, 3);
            Ensure(centered.Center == 80.5f && centered.Span == range.Span,
                $"0.5 刻みの中心・幅の維持(中心 {centered.Center}、幅 {centered.Span})");
            Ensure(!XdtDisplayRange.Center(copy, 3, 80.4), "同じ中心で書き換えた");
            Ensure(CountDifferentBytes(File.ReadAllBytes(noise), File.ReadAllBytes(copy)) <= 8, "表示レンジ以外のバイトが変わった");
            var original = XdtFile.Load(noise);
            var edited = XdtFile.Load(copy);
            foreach (var channel in original.Channels)
                Ensure(channel.ReadRawValues().SequenceEqual(edited.GetChannel(channel.Info.Number).ReadRawValues()), "測定データが変わった");
            Throws<InvalidDataException>(() => XdtDisplayRange.Read(Path.Combine(fixture, "loggerX.xdt"), 1), "LOGGER X 形式");
            Throws<ArgumentOutOfRangeException>(() => XdtDisplayRange.Read(noise, 0), "CH0");
            Throws<ArgumentException>(() => XdtDisplayRange.Center(copy, 3, double.NaN), "中心 NaN");
            Console.WriteLine("PASS XdtDisplayRange: 旧 LOGGER の表示レンジを 0.5 刻みで中心合わせ、測定データは不変、LOGGER X は例外");

            foreach (string name in new[] { "as110-noise-legacy.xdt", "as110-motion-legacy.xdt", "b.xdt", "c.xdt", "PC.xdt", "CR.xdt" })
            {
                string path = Path.Combine(fixture, name);
                var expected = XdtFile.Load(path).Info.Timing.AcquisitionStart;
                Ensure(XdtTimestamp.TryRead(path) == expected, $"{name}: XdtTimestamp と XdtFile の収集開始日時が違う");
            }
            Ensure(XdtTimestamp.TryRead(Path.Combine(fixture, "as110-motion-nrx100.ydt")) is null, "ydt は null");
            Console.WriteLine("PASS XdtTimestamp: 旧 LOGGER 形式 6 本で XdtFile の収集開始日時と一致、ydt は null");
        }
        finally { Directory.Delete(temp, true); }
    }
}
