using System.Buffers.Binary;

namespace PSCAutomation.Wrappers.LoggerFiles;

// 旧 LOGGER(NR-500)の xdt に入っている測定日時を読む(docs/xdt形式解析メモ.md: 0x58 に FILETIME)。
// 元: AS110NoiseTest Conversion/XdtTimestamp.cs。形式(旧 LOGGER / LOGGER X)を判別せず常に 0x58 を読むので、旧 LOGGER の xdt 専用。
// LOGGER X の xdt も扱うなら XdtFile.Load(path).Info.Timing.AcquisitionStart を使う(同じ値を形式ごとの位置から読む)。
// 値はローカル時刻をそのまま FILETIME の目盛で入れたもの(学習データで、ファイルの更新時刻と一致を確認)。
public static class XdtTimestamp
{
    private const int TimestampOffset = 0x58;

    public static DateTime? TryRead(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> head = stackalloc byte[TimestampOffset + 8];
        if (fs.Read(head) < head.Length) return null;
        if (!head[..10].SequenceEqual("NR500P.DAT"u8)) return null;
        long fileTime = BinaryPrimitives.ReadInt64LittleEndian(head[TimestampOffset..]);
        if (fileTime <= 0) return null;
        try
        {
            // UTC として換算した値がそのままローカル時刻。Kind は付けない。
            return DateTime.SpecifyKind(DateTime.FromFileTimeUtc(fileTime), DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
