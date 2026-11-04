using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using PSCAutomation.Wrappers.LoggerFiles;

// 元: XdtConversion CoreVerification/Program.cs(9809ec3a5352)。名前空間だけ変えて全体を残し、末尾にモノレポで足した検査を呼ぶ。

static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
static void Fails<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
static DateTime Date(string text) => DateTime.ParseExact(text, "yyyy/MM/dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
static double CsvTolerance(string text)
{
    int dot = text.IndexOf('.');
    int decimals = dot < 0 ? 0 : text.Length - dot - 1;
    return 0.5 * Math.Pow(10, -decimals) + 0.0002;
}
string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures");
using var reference = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture, "timing-reference.json")));
foreach (var item in reference.RootElement.EnumerateArray())
{
    string name = item.GetProperty("name").GetString()!;
    string path = Path.Combine(fixture, name + ".xdt");
    byte[] bytes = File.ReadAllBytes(path);
    Check(Convert.ToHexStringLower(SHA256.HashData(bytes)) == item.GetProperty("xdtSha256").GetString(), name + " fixture hash");
    var file = XdtFile.Load(path);
    var info = file.Info;
    var timing = info.Timing;
    Check(file.SampleCount == item.GetProperty("sampleCount").GetInt32(), name + " count");
    long intervalTicks = name switch { "b" => 100000, "c" => 20000, "loggerX" => 2000, _ => 10000 };
    Check(file.SampleInterval == TimeSpan.FromTicks(intervalTicks), name + " sampling interval");
    Check(timing.AcquisitionStart == Date(item.GetProperty("acquisitionStart").GetString()!), name + " acquisition date");
    Check(timing.FirstSample == Date(item.GetProperty("firstSample").GetString()!), name + " leading date");
    Check(timing.FirstSample!.Value.Kind == DateTimeKind.Unspecified, name + " timezone must remain unspecified");
    Check(timing.LastSample == Date(item.GetProperty("lastSample").GetString()!), name + " last date");
    Check(file.GetSampleTime(file.SampleCount - 1) == timing.LastSample, name + " indexed date");
    Check(file.GetElapsedTime(0) == TimeSpan.Zero, name + " first elapsed time");
    Check(timing.SampleSpan!.Value.Ticks == intervalTicks * (file.SampleCount - 1L), name + " N-1 span");
    Check(timing.RecordLength!.Value.Ticks == intervalTicks * file.SampleCount, name + " N record length");
    Check(info.TrailerOffset + 36 == bytes.Length, name + " trailer");
    foreach (var field in info.BinaryFields)
        Check(field.Offset >= 0 && field.Length > 0 && (long)field.Offset + field.Length <= bytes.Length, name + " field bounds " + field.Name);
    for (int j = 0; j < info.ChannelCount; j++)
    {
        var ch = file.Channels[j];
        var c = ch.Info;
        Check(c.Name == item.GetProperty("channelNames")[j].GetString(), name + " channel name");
        // When scaling is off, CSV derives a default unit from input settings;
        // the stored scaling-unit string can be empty. Do not invent that default.
        if (c.ScalingEnabled) Check(c.Unit == item.GetProperty("units")[j].GetString(), name + " channel unit");
        Check(c.ScalingEnabled == item.GetProperty("scalingEnabled")[j].GetBoolean(), name + " scaling flag");
        Check(c.MovingAverage == item.GetProperty("movingAverage")[j].GetBoolean(), name + " smoothing flag");
        if (c.MovingAverage) Check(c.Window == item.GetProperty("windows")[j].GetInt32(), name + " window");
        var raw = ch.ReadRawValues();
        var scaled = ch.ReadScaledValues();
        var processed = ch.ReadProcessedValues();
        Check(Math.Abs(processed[0] - item.GetProperty("firstProcessed")[j].GetDouble())
            <= CsvTolerance(item.GetProperty("firstProcessedText")[j].GetString()!), name + " CSV first value");
        Check(Math.Abs(processed[^1] - item.GetProperty("lastProcessed")[j].GetDouble())
            <= CsvTolerance(item.GetProperty("lastProcessedText")[j].GetString()!), name + " CSV last value");
        for (int i = 0; i < raw.Length; i++)
        {
            float expected = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(info.DataOffset + 4 * (i * info.ChannelCount + j), 4)));
            Check(raw[i] == expected, name + " raw value");
            if (!c.ScalingEnabled) Check(scaled[i] == raw[i], name + " disabled scaling");
            if (!c.MovingAverage) Check(processed[i] == scaled[i], name + " disabled smoothing");
        }
        raw[0] = double.NaN;
        Check(double.IsFinite(ch.ReadRawValues()[0]), name + " independent arrays");
        Check(ReferenceEquals(file.GetChannel(c.Number), ch), name + " channel lookup");
    }
    using var gold = new BinaryReader(File.OpenRead(Path.Combine(fixture, name + ".bin")));
    int n = gold.ReadInt32(), count = gold.ReadInt32();
    Check(n == file.SampleCount && count == info.ChannelCount, name + " gold dimensions");
    for (int j = 0; j < count; j++)
    {
        int number = gold.ReadInt32();
        var actual = file.GetChannel(number).ReadProcessedValues();
        for (int i = 0; i < n; i++) Check(Math.Abs(actual[i] - gold.ReadDouble()) <= 0.0002, $"{name} CH{number} sample {i}");
    }
    Fails<ArgumentOutOfRangeException>(() => file.GetSampleTime(-1));
    Fails<ArgumentOutOfRangeException>(() => file.GetElapsedTime(file.SampleCount));
    Fails<InvalidDataException>(() => file.GetChannel(256));
    Console.WriteLine($"{name}: metadata, CSV times, raw/scaled/processed values, all golden points OK");
}

// Malformed/unknown metadata tests use temporary copies, never change fixtures.
string temp = Path.Combine(Path.GetTempPath(), "XdtCoreVerify-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    byte[] original = File.ReadAllBytes(Path.Combine(fixture, "PC.xdt"));
    string test = Path.Combine(temp, "test.xdt");
    void Save(Action<byte[]> change) { var copy = (byte[])original.Clone(); change(copy); File.WriteAllBytes(test, copy); }
    Save(d => BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(518, 2), 999));
    var unknown = XdtFile.Load(test);
    Check(unknown.Info.Timing.Sampling.UnitCode == 999 && unknown.SampleInterval is null
        && unknown.Info.Timing.SampleSpan is null && unknown.Info.Timing.RecordLength is null
        && unknown.GetSampleTime(1) is null, "Unknown time unit must not be guessed");
    Check(unknown.GetChannel(5).ReadProcessedValues().Length == 50000, "Unknown timing preserves waveform access");
    Save(d => BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(516, 2), 0));
    Check(XdtFile.Load(test).SampleInterval is null, "Zero interval is unknown");
    Save(d => d.AsSpan(88, 16).Fill(255));
    var badDates = XdtFile.Load(test);
    Check(badDates.Info.Timing.AcquisitionStart is null && badDates.Info.Timing.FirstSample is null, "Invalid dates");
    Save(d => d.AsSpan(88, 16).Clear());
    Check(XdtFile.Load(test).Info.Timing.FirstSample is null, "Zero date");
    Save(d => d[0] = 0);
    Fails<InvalidDataException>(() => XdtFile.Load(test));
    Save(d => d[16] = 255);
    Fails<InvalidDataException>(() => XdtFile.Load(test));
    File.WriteAllBytes(test, original[..^1]);
    Fails<InvalidDataException>(() => XdtFile.Load(test));
    File.WriteAllBytes(test, new byte[20]);
    Fails<InvalidDataException>(() => XdtFile.Load(test));
    int h = XdtFile.Load(Path.Combine(fixture, "PC.xdt")).Info.WaveformHeaderOffset;
    Save(d => BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(h + 16, 4), 0));
    Fails<InvalidDataException>(() => XdtFile.Load(test));
    Save(d => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(796 + 48 + 4, 4), 0));
    Fails<InvalidDataException>(() => XdtFile.Load(test));
    Save(d => d.AsSpan(796 + 24, 4).CopyTo(d.AsSpan(796 + 24 + 8, 4)));
    Fails<InvalidDataException>(() => XdtFile.Load(test));
    // An even smoothing window has one sample to the left and none to the right.
    Save(d => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(796 + 48 + 4, 4), 2));
    var even = XdtFile.Load(test).GetChannel(1);
    var evenSource = even.ReadScaledValues();
    var evenActual = even.ReadProcessedValues();
    Check(evenActual[0] == evenSource[0], "Even window leading edge");
    for (int i = 1; i < evenActual.Length; i++)
        Check(Math.Abs(evenActual[i] - (float)((evenSource[i - 1] + evenSource[i]) / 2)) < 0.000001,
            "Even window alignment");
    Save(d => BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(6044, 4), 0x7fc00000));
    Fails<InvalidDataException>(() => XdtFile.Load(test).GetChannel(1).ReadRawValues());
    Console.WriteLine("Unknown timing, invalid dates, malformed headers/scaling/windows/values OK");
}
finally { Directory.Delete(temp, true); }
Console.WriteLine("All core checks passed.");

LegacyReaderChecks.Run(fixture);
ScalingEditorChecks.Run(fixture);
DisplayRangeAndTimestampChecks.Run(fixture);
LoggerCsvReaderChecks.Run();
Console.WriteLine("LoggerFiles: all checks passed.");
