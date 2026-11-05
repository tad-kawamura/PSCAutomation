using System.Text;
using System.Text.Json;

namespace PSCAutomation.Apps.AS110AllCheck;

// 記録はスタンドごとのPCに保存されるため、スタンド名・機器名は持たない。
internal sealed record CalibrationRecord(DateTimeOffset AppliedAt, As110Calibration Calibration)
{
    public int Version { get; init; } = 2;

    public void Validate()
    {
        if (AppliedAt == default || Calibration?.Current is null || Calibration.Pressure is null)
            throw new InvalidDataException("校正記録の適用日時または電流・圧力の値がありません。");
        Calibration.Validate();
    }
}

// UTF-8の1行1JSONテキスト。過去の行は読み直さず、成功した適用を末尾へ追記する（旧形式の行もそのまま残す）。
internal sealed class CalibrationHistory(string filePath)
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AS110AllCheck", "校正記録.txt");
    private static readonly UTF8Encoding Encoding = new(false, true);
    public string FilePath { get; } = filePath;

    public void Append(CalibrationRecord record)
    {
        record.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        // 他プロセスの書込みとは排他する。
        using var stream = new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (stream.Length > 0)
        {
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() != '\n') stream.WriteByte((byte)'\n');
        }
        stream.Seek(0, SeekOrigin.End);
        byte[] bytes = Encoding.GetBytes(JsonSerializer.Serialize(record) + "\n");
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
