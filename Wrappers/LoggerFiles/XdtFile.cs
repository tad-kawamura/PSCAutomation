using System.Buffers.Binary;
using System.Text;

namespace PSCAutomation.Wrappers.LoggerFiles;

/// <summary>One in-memory XDT snapshot. No Excel, GUI, COM, or hardware dependency.</summary>
public sealed class XdtFile
{
    private readonly byte[] data;
    public XdtInfo Info { get; }
    public int SampleCount => Info.SampleCount;
    public TimeSpan? SampleInterval => Info.Timing.SampleInterval;
    public IReadOnlyList<XdtChannel> Channels { get; }

    private XdtFile(byte[] bytes)
    {
        data = bytes;
        Info = ParseHeader(bytes);
        Channels = Array.AsReadOnly(Info.Channels.Select(c => new XdtChannel(this, c)).ToArray());
    }

    public static XdtFile Load(string path) => new(File.ReadAllBytes(path));

    public XdtChannel GetChannel(int number) => Channels.FirstOrDefault(c => c.Info.Number == number)
        ?? throw new InvalidDataException($"CH{number}がありません。");

    public TimeSpan? GetElapsedTime(int sampleIndex)
    {
        ValidateIndex(sampleIndex);
        return SampleInterval is { } dt ? TimeSpan.FromTicks(dt.Ticks * sampleIndex) : null;
    }

    public DateTime? GetSampleTime(int sampleIndex)
    {
        var elapsed = GetElapsedTime(sampleIndex);
        return Info.Timing.FirstSample is { } first && elapsed is { } dt
            && first.Ticks <= DateTime.MaxValue.Ticks - dt.Ticks ? first.Add(dt) : null;
    }

    private void ValidateIndex(int index)
    {
        if (index < 0 || index >= SampleCount) throw new ArgumentOutOfRangeException(nameof(index));
    }

    internal double[] ReadRaw(ChannelInfo channel)
    {
        var output = new double[SampleCount];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = F32(data, Info.DataOffset + 4 * (i * Info.ChannelCount + channel.DataColumnIndex));
            if (!double.IsFinite(output[i]))
                throw new InvalidDataException($"CH{channel.Number}の{i + 1}点目が非数値です。");
        }
        return output;
    }

    private static uint U32(byte[] d, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(offset, 4));
    private static ushort U16(byte[] d, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(offset, 2));
    private static ulong U64(byte[] d, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(offset, 8));
    private static float F32(byte[] d, int offset) => BitConverter.Int32BitsToSingle(
        BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(offset, 4)));

    private static DateTime? Calendar(ulong raw)
    {
        // This reproduces CSV wall-clock values without applying the host's time zone.
        const long epochTicks = 504911232000000000; // 1601-01-01
        return raw == 0 || raw > (ulong)(DateTime.MaxValue.Ticks - epochTicks)
            ? null : new DateTime(epochTicks + (long)raw, DateTimeKind.Unspecified);
    }

    private static string Text(byte[] d, int offset, int length, bool unicode)
    {
        if (unicode)
        {
            int end = 0;
            while (end < length && (d[offset + end] != 0 || d[offset + end + 1] != 0)) end += 2;
            return Encoding.Unicode.GetString(d, offset, end);
        }
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        int count = Array.IndexOf(d, (byte)0, offset, length);
        return Encoding.GetEncoding(932).GetString(d, offset, count < 0 ? length : count - offset);
    }

    private static XdtInfo ParseHeader(byte[] d)
    {
        if (d.Length < 124) throw new InvalidDataException("XDTファイルが短すぎます。");
        if (!d.AsSpan(0, 10).SequenceEqual("NR500P.DAT"u8))
            throw new InvalidDataException("対応していないファイル識別子です。YDTは未対応です。");
        bool x = U32(d, 16) switch
        {
            0x00030003 => false,
            0x00030101 => true,
            _ => throw new InvalidDataException("対応していないXDT形式です。")
        };
        int countOffset = x ? 120 : 104;
        uint rawCount = U32(d, countOffset);
        if (rawCount == 0 || rawCount > int.MaxValue) throw new InvalidDataException("測定データがありません。");
        int n = (int)rawCount;
        var hits = new List<(int H, int Start, int End, int[] Ids)>();
        for (int h = 4; h <= d.Length - 12; h++)
        {
            if (U32(d, h) != rawCount) continue;
            uint c = U32(d, h + 4);
            if (c is 0 or > 256 || U32(d, h - 4) != 4 * c || U32(d, h + 8) != 0) continue;
            long start = h + 12L + 4L * c, end = start + 4L * n * c;
            if (end + 36 != d.Length || U32(d, (int)end) != 2) continue;
            var ids = new int[(int)c];
            for (int j = 0; j < ids.Length; j++) ids[j] = unchecked((int)U32(d, h + 12 + 4 * j));
            if (ids.Any(id => id < 0 || id > 255) || ids.Distinct().Count() != ids.Length) continue;
            hits.Add((h, (int)start, (int)end, ids));
        }
        if (hits.Count != 1) throw new InvalidDataException("波形ヘッダを一意に特定できません。未対応の構造か破損したファイルです。");
        var hit = hits[0];
        int intervalOffset = x ? 820 : 516, timeOffset = x ? 104 : 88;
        if (intervalOffset + 4 > hit.H - 4) throw new InvalidDataException("収集設定領域が不正です。");
        var fields = new List<XdtBinaryField>();
        void Field(string name, int offset, int length, string type, string meaning)
            => fields.Add(new(name, offset, length, type, meaning));
        Field("Signature", 0, 16, "ASCII", "NR500P.DAT + padding");
        Field("FormatVersion", 16, 4, "bytes", "Verified layout discriminator");
        Field("AcquisitionStart", timeOffset, 8, "uint64 LE", "1601 epoch, 100 ns ticks; timezone unverified");
        Field("FirstSample", timeOffset + 8, 8, "uint64 LE", "First saved sample calendar value");
        Field("SampleCount", countOffset, 4, "uint32 LE", "Samples per stored channel");
        Field("Sampling.Value", intervalOffset, 2, "uint16 LE", "Sampling interval magnitude");
        Field("Sampling.UnitCode", intervalOffset + 2, 2, "uint16 LE", "Verified: 2=µs, 3=ms; other codes unknown");
        Field("Settings.SampleCount", x ? 828 : 524, 4, "uint32 LE", "Duplicate count observed in fixtures; not used as data count");
        Field("ChannelIdByteLength", hit.H - 4, 4, "uint32 LE", "4 * channel count");
        Field("Waveform.SampleCount", hit.H, 4, "uint32 LE", "Must equal header count");
        Field("ChannelCount", hit.H + 4, 4, "uint32 LE", "Count of stored channels");
        Field("Waveform.Reserved", hit.H + 8, 4, "uint32 LE", "Observed zero; required by supported layout");
        Field("ChannelIds", hit.H + 12, 4 * hit.Ids.Length, "int32 LE[]", "0-based IDs in waveform column order");
        Field("Waveform", hit.Start, hit.End - hit.Start, "float32 LE[]", "Sample-major values before scaling and smoothing");
        Field("Trailer", hit.End, 36, "bytes", "First uint32=2; remaining meaning unverified");
        var channels = new List<ChannelInfo>();
        for (int j = 0; j < hit.Ids.Length; j++)
        {
            int id = hit.Ids[j];
            int b = (x ? 1100 : 796) + (x ? 92 : 68) * id;
            int r = x ? 40 : 24, avg = x ? 72 : 48, enabled = x ? 36 : 20;
            if (b + (x ? 92 : 68) > hit.H - 4)
                throw new InvalidDataException($"CH{id + 1}の設定領域を読み出せません。");
            uint scaleFlag = U32(d, b + enabled), avgFlag = U32(d, b + avg), window = U32(d, b + avg + 4);
            if (scaleFlag > 1 || avgFlag > 1 || (avgFlag == 1 && (window == 0 || window > int.MaxValue)))
                throw new InvalidDataException($"CH{id + 1}のスケーリング／移動平均設定が無効です。");
            double x1 = F32(d, b + r), y1 = F32(d, b + r + 4), x2 = F32(d, b + r + 8), y2 = F32(d, b + r + 12);
            double gain = scaleFlag == 1 ? (y2 - y1) / (x2 - x1) : 1;
            double offset = scaleFlag == 1 ? y1 - gain * x1 : 0;
            if (!double.IsFinite(gain) || !double.IsFinite(offset) || (scaleFlag == 1 &&
                (!double.IsFinite(x1) || !double.IsFinite(y1) || !double.IsFinite(x2) || !double.IsFinite(y2))))
                throw new InvalidDataException($"CH{id + 1}のスケーリング設定が無効です。");
            channels.Add(new(id + 1, gain, offset, avgFlag == 1, window <= int.MaxValue ? (int)window : 0)
            {
                Name = Text(d, b, x ? 32 : 16, x), Unit = Text(d, b + r + 16, x ? 16 : 8, x),
                ScalingEnabled = scaleFlag == 1, ScalingPoints = new(x1, y1, x2, y2),
                SettingsOffset = b, DataColumnIndex = j
            });
            string prefix = $"CH{id + 1}.";
            string encoding = x ? "UTF-16LE" : "CP932";
            Field(prefix + "Name", b, x ? 32 : 16, encoding, "Waveform name");
            Field(prefix + "ScalingEnabled", b + enabled, 4, "uint32 LE", "0=off, 1=on");
            Field(prefix + "Input1", b + r, 4, "float32 LE", "Scaling input 1");
            Field(prefix + "Output1", b + r + 4, 4, "float32 LE", "Scaling output 1");
            Field(prefix + "Input2", b + r + 8, 4, "float32 LE", "Scaling input 2");
            Field(prefix + "Output2", b + r + 12, 4, "float32 LE", "Scaling output 2");
            Field(prefix + "Unit", b + r + 16, x ? 16 : 8, encoding, "Configured display unit");
            Field(prefix + "MovingAverage", b + avg, 4, "uint32 LE", "0=off, 1=on");
            Field(prefix + "Window", b + avg + 4, 4, "uint32 LE", "Smoothing window, samples");
        }
        return new(x ? "WAVE LOGGER X" : "WAVE LOGGER（旧）", n, hit.Start, channels.AsReadOnly())
        {
            WaveformHeaderOffset = hit.H, TrailerOffset = hit.End,
            Timing = new(U64(d, timeOffset), U64(d, timeOffset + 8), Calendar(U64(d, timeOffset)),
                Calendar(U64(d, timeOffset + 8)), new(U16(d, intervalOffset), U16(d, intervalOffset + 2)), n),
            BinaryFields = fields.AsReadOnly()
        };
    }
}

/// <summary>Returns independent arrays. Reading does not modify the loaded file or cached bytes.</summary>
public sealed class XdtChannel
{
    private readonly XdtFile file;
    public ChannelInfo Info { get; }
    internal XdtChannel(XdtFile file, ChannelInfo info) { this.file = file; Info = info; }
    public double[] ReadRawValues() => file.ReadRaw(Info);
    public double[] ReadScaledValues()
    {
        var values = ReadRawValues();
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (float)(Info.Gain * values[i] + Info.Offset);
            if (!double.IsFinite(values[i])) throw new InvalidDataException($"CH{Info.Number}の{i + 1}点目が非数値です。");
        }
        return values;
    }
    public double[] ReadProcessedValues()
    {
        var source = ReadScaledValues();
        if (!Info.MovingAverage || Info.Window <= 1) return source;
        int window = Info.Window;
        var output = new double[source.Length];
        int left = 0, right = 0;
        double sum = 0;
        for (int i = 0; i < source.Length; i++)
        {
            int wantLeft = (int)Math.Max(0L, (long)i - window / 2);
            int wantRight = (int)Math.Min(source.Length, (long)i - window / 2 + window);
            while (right < wantRight) sum += source[right++];
            while (left < wantLeft) sum -= source[left++];
            output[i] = (float)(sum / (right - left));
        }
        return output;
    }
}

/// <summary>Compatibility entry points for the existing application and verification program.</summary>
public static class XdtReader
{
    public static XdtInfo Inspect(string path) => XdtFile.Load(path).Info;
    public static XdtResult Read(string path, IEnumerable<int> requested)
    {
        var file = XdtFile.Load(path);
        var values = requested.Distinct().ToDictionary(n => n, n => file.GetChannel(n).ReadProcessedValues());
        return new(file.Info, values);
    }
}
