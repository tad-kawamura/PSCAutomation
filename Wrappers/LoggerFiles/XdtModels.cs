namespace PSCAutomation.Wrappers.LoggerFiles;

/// <summary>Offsets are zero-based byte positions; numbers in XDT are little-endian.</summary>
public sealed record XdtBinaryField(string Name, int Offset, int Length, string DataType, string Meaning);

public sealed record XdtScalingPoints(double Input1, double Output1, double Input2, double Output2);

public sealed record ChannelInfo(int Number, double Gain, double Offset, bool MovingAverage, int Window)
{
    public string Name { get; init; } = "";
    public string Unit { get; init; } = "";
    public bool ScalingEnabled { get; init; }
    public XdtScalingPoints ScalingPoints { get; init; } = new(0, 0, 1, 1);
    public int SettingsOffset { get; init; }
    public int DataColumnIndex { get; init; }
}

/// <summary>Only unit codes verified against WAVE LOGGER X CSV are decoded.</summary>
public sealed record XdtSamplingInterval(ushort Value, ushort UnitCode)
{
    public TimeSpan? Interval => Value == 0 ? null : UnitCode switch
    {
        2 => TimeSpan.FromTicks((long)Value * 10),      // microseconds
        3 => TimeSpan.FromTicks((long)Value * 10_000), // milliseconds
        _ => null
    };
    public string? Unit => UnitCode switch { 2 => "µs", 3 => "ms", _ => null };
}

/// <summary>
/// Calendar values match the manufacturer's CSV. Time zone is not encoded/verified;
/// dates deliberately use DateTimeKind.Unspecified, with the original 64-bit values retained.
/// </summary>
public sealed record XdtTiming(ulong AcquisitionStartRaw, ulong FirstSampleRaw,
    DateTime? AcquisitionStart, DateTime? FirstSample, XdtSamplingInterval Sampling, int SampleCount)
{
    public TimeSpan? SampleInterval => Sampling.Interval;
    public TimeSpan? SampleSpan => SampleInterval is { } dt
        ? TimeSpan.FromTicks(dt.Ticks * (SampleCount - 1L)) : null;
    public TimeSpan? RecordLength => SampleInterval is { } dt
        ? TimeSpan.FromTicks(dt.Ticks * SampleCount) : null;
    public DateTime? LastSample => FirstSample is { } first && SampleSpan is { } span
        && first.Ticks <= DateTime.MaxValue.Ticks - span.Ticks ? first.Add(span) : null;
}

public sealed record XdtInfo(string Format, int Samples, int DataOffset, IReadOnlyList<ChannelInfo> Channels)
{
    public int SampleCount => Samples;
    public int ChannelCount => Channels.Count;
    public int WaveformHeaderOffset { get; init; }
    public int TrailerOffset { get; init; }
    public XdtTiming Timing { get; init; } = null!;
    public IReadOnlyList<XdtBinaryField> BinaryFields { get; init; } = Array.Empty<XdtBinaryField>();
}

public sealed record XdtResult(XdtInfo Info, Dictionary<int, double[]> Values);
