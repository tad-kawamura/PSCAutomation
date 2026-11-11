using System.Reflection;
using PSCAutomation.Wrappers.WaveLogger.Automation;

// 測定中の値の取り出し(ReadRawData / GetCurrentRawValue、2026-11-11 追加)。COM の代わりに下の DataLogger を差し込む。
internal static class DataReadTests
{
    public static void Run(Type type)
    {
        var appField = type.GetField("_application", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        WaveLoggerSession Create(DataLogger fake)
        {
            var session = new WaveLoggerSession();
            appField.SetValue(session, fake);
            return session;
        }

        // 初期化前は既存の関数と同じく InvalidOperationException。
        try { new WaveLoggerSession().ReadRawData(1, [0], 0); throw new Exception("uninitialized read accepted"); }
        catch (InvalidOperationException) { }

        var fake = new DataLogger { Count = 5 };
        var session = Create(fake);
        var block = session.ReadRawData(1, [0, 2], 0);
        Check(block.TotalCount == 5 && block.Start == 0 && block.Count == 5 && block.Values.Count == 2, "first block shape");
        Check(block.Values[0].SequenceEqual(new float[] { 0, 1, 2, 3, 4 }) && block.Values[1].SequenceEqual(new float[] { 200, 201, 202, 203, 204 }),
            "values or channel order");
        Check(fake.Calls.SequenceEqual(new[] { "Count", "Array:1:0:0:5", "Array:1:2:0:5" }), "calls: " + string.Join(",", fake.Calls));

        // 続きだけ取る。増えていなければ空(GetArrayData を呼ばない)。
        fake.Calls.Clear();
        fake.Count = 8;
        block = session.ReadRawData(1, [0], 5);
        Check(block.Count == 3 && block.Values[0].SequenceEqual(new float[] { 5, 6, 7 }), "continuation");
        block = session.ReadRawData(1, [0], 8);
        Check(block.Count == 0 && fake.Calls.Count(c => c.StartsWith("Array")) == 1, "empty read called GetArrayData");
        // 点数が減ったら空で返し、判断は呼び出し元。
        fake.Count = 2;
        block = session.ReadRawData(1, [0], 8);
        Check(block.TotalCount == 2 && block.Count == 0, "decreased count");
        Console.WriteLine("PASS ReadRawData: channel order, continuation, empty and decreased counts");

        // 取り出せた点数が合わなければ例外。アクティブなファイルが無くても例外。
        fake.Count = 10;
        fake.ShortRead = true;
        try { session.ReadRawData(1, [0], 0); throw new Exception("short read accepted"); }
        catch (InvalidOperationException ex) { Check(ex.Message.Contains("GetArrayData"), "short read message"); }
        fake.ShortRead = false;
        fake.NoFile = true;
        try { session.ReadRawData(1, [0], 0); throw new Exception("missing file accepted"); }
        catch (InvalidOperationException) { }
        fake.NoFile = false;
        try { session.ReadRawData(1, [], 0); throw new Exception("empty channel list accepted"); }
        catch (ArgumentException) { }
        try { session.ReadRawData(1, [0], -1); throw new Exception("negative start accepted"); }
        catch (ArgumentOutOfRangeException) { }

        // 最新値: 無効値(3.4e38)は null。
        Check(session.GetCurrentRawValue(1, 3) == 3.5f, "current value");
        fake.CurrentInvalid = true;
        Check(session.GetCurrentRawValue(1, 3) is null, "invalid current value not null");
        Console.WriteLine("PASS ReadRawData failures (short read, no file, arguments) and GetCurrentRawValue invalid value");
    }
}

public class DataLogger
{
    public List<string> Calls = new();
    public long Count;
    public bool ShortRead, NoFile, CurrentInvalid;
    public object? GetActiveFile() => NoFile ? null : this;
    public long GetDataCount() { Calls.Add("Count"); return Count; }
    public long GetArrayData(int unit, int channel, int start, int count, ref float[] buffer, int option)
    {
        Calls.Add($"Array:{unit}:{channel}:{start}:{count}");
        for (int i = 0; i < count; i++) buffer[i] = channel * 100 + start + i;
        return ShortRead ? count - 1 : count;
    }
    public float GetCurrentData(int unit, int channel) => CurrentInvalid ? 3.4e38f : channel + 0.5f;
}
