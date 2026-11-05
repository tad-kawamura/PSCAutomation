using System.Buffers.Binary;
using PSCAutomation.Apps.AS110NoiseTest.Measurement;
using PSCAutomation.Wrappers.LoggerFiles;

namespace PSCAutomation.Apps.AS110NoiseTest.Simulation;

// 制御圧力の波形の種類(模擬)。
internal enum WaveProfile
{
    Normal,  // 白色ノイズ p-p 約 0.8 kPa → 無色
    Yellow,  // 白色ノイズ p-p 約 1.8 kPa → 黄
    Red,     // 矩形波 ±0.5 kPa(周期 4 秒)+ 白色ノイズ → 赤
}

// WAVE LOGGER の代わり(機器なしの確認用。Tools/MeasurementCheck --sim と、画面の AS110NoiseTest.exe --sim で使う)。OpenFile した雛形 xdt を元に、SaveXdt でデータ部だけ差し替えた xdt を書く。
// 状態は 1(停止)↔ 4(測定中)だけ。記録時間は duration(実機は雛形の 40 秒、模擬では短くする)。
// CH1(電流)は模擬プラントの実電流、CH3(制御圧力)は電流ごとの WaveProfile で作る。CH2・CH4 は雛形のまま。
internal sealed class SimulatedLogger(
    TimeSpan duration,
    Func<double> currentMilliamps,
    Func<int, WaveProfile> profileOf,
    Action<string>? log = null) : ILoggerDevice
{
    private const int TimestampOffset = 0x58;   // docs/xdt形式解析メモ.md
    private readonly Random _random = new(7);
    private string? _openFile;
    private DateTime? _startedAt;
    private int _saved;

    public string Description => $"模擬 LOGGER(記録 {duration.TotalSeconds:0.#} 秒)";
    // 故障の模擬: Start の戻り値、保存の失敗。
    public long StartReturnValue { get; set; }
    public bool FailSave { get; set; }
    // 試し取り用の雛形(MeasurementSettings.PreviewTemplateXdtPath と同じもの)。これを開いて記録したときは PreviewDuration だけ記録する
    // (実機の試し取りは 0.5 秒。状態を読む前に終わる場合の流れを確かめられる)。
    public string? PreviewTemplatePath { get; set; }
    public TimeSpan PreviewDuration { get; set; } = TimeSpan.FromSeconds(0.5);
    // 制御圧力の平均 [kPa] を電流ごとに変える(試し取りで表示レンジが動くことを確かめる用)。null なら雛形の平均のまま。
    public Func<int, double>? ControlKpaOf { get; set; }
    // 書いたファイルとその波形(確認用。試し取りは入れない)。
    public List<(string Path, int Milliamps, WaveProfile Profile)> Saved { get; } = [];

    private bool IsPreview => PreviewTemplatePath is not null && _openFile is not null &&
        string.Equals(Path.GetFullPath(_openFile), Path.GetFullPath(PreviewTemplatePath), StringComparison.OrdinalIgnoreCase);

    public long OpenFile(string path)
    {
        if (!File.Exists(path)) return -1;
        _openFile = path;
        _startedAt = null;
        log?.Invoke($"  [模擬LOGGER] OpenFile {Path.GetFileName(path)}");
        return 0;
    }

    public long CloseActiveFile()
    {
        if (_openFile is null) return -1;
        _openFile = null;
        return 0;
    }

    public long StartSingleAcquisition()
    {
        if (_openFile is null) return -1;
        if (StartReturnValue != 0) return StartReturnValue;
        _startedAt = DateTime.UtcNow;
        _recordFor = IsPreview ? PreviewDuration : duration;
        return 0;
    }

    private TimeSpan _recordFor;

    public long GetState()
    {
        if (_startedAt is DateTime t && DateTime.UtcNow - t < _recordFor) return 4;
        return LoggerState.Stopped;
    }

    public void Stop() => _startedAt = null;

    public long SaveXdt(string path)
    {
        if (_openFile is null || FailSave) return -1;
        int milliamps = (int)Math.Round(currentMilliamps());
        WaveProfile profile = profileOf(milliamps);
        bool preview = IsPreview;
        WriteXdt(_openFile, path, milliamps, profile);
        if (!preview) Saved.Add((path, milliamps, profile));
        _saved++;
        log?.Invoke($"  [模擬LOGGER] SaveXdt {Path.GetFileName(path)}({(preview ? "試し取り" : profile.ToString())})");
        return 0;
    }

    private void WriteXdt(string template, string path, int milliamps, WaveProfile profile)
    {
        byte[] bytes = File.ReadAllBytes(template);
        XdtInfo info = XdtReader.Inspect(template);
        int channelCount = info.ChannelCount;
        var baseline = XdtReader.Read(template, [3]).Values[3];   // 雛形の制御圧力 [kPa]
        double baseKpa = ControlKpaOf?.Invoke(milliamps) ?? baseline.Average();
        double dt = 0.02;

        ChannelInfo? chI = info.Channels.FirstOrDefault(c => c.Number == 1);
        ChannelInfo? chP = info.Channels.FirstOrDefault(c => c.Number == 3);
        if (chI is null || chP is null) throw new InvalidDataException("雛形 xdt に CH1 / CH3 がありません。");
        int current = chI.DataColumnIndex, control = chP.DataColumnIndex;

        for (int i = 0; i < info.Samples; i++)
        {
            double t = i * dt;
            double kpa = profile switch
            {
                WaveProfile.Yellow => baseKpa + Uniform(0.9),
                WaveProfile.Red => baseKpa + (Math.Floor(t / 2.0) % 2 == 0 ? 0.5 : -0.5) + Uniform(0.2),
                _ => baseKpa + Uniform(0.4),
            };
            kpa = Math.Round(kpa, 1);                          // LOGGER の表示と同じ 0.1 kPa 刻み
            double ma = milliamps + Uniform(0.01);
            Put(bytes, info.DataOffset + 4 * (i * channelCount + control), ToRaw(kpa, chP));
            Put(bytes, info.DataOffset + 4 * (i * channelCount + current), ToRaw(ma, chI));
        }
        // 測定日時(XdtTimestamp が読む FILETIME。ローカル時刻を UTC の目盛で入れる)
        if (bytes.AsSpan(0, 10).SequenceEqual("NR500P.DAT"u8))
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(TimestampOffset),
                DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Utc).ToFileTimeUtc());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    // XdtReader は raw × Gain + Offset で物理量にする。その逆。
    private static float ToRaw(double physical, ChannelInfo ch) => (float)((physical - ch.Offset) / ch.Gain);

    private static void Put(byte[] bytes, int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), value);

    private double Uniform(double amplitude) => (_random.NextDouble() * 2 - 1) * amplitude;
}
