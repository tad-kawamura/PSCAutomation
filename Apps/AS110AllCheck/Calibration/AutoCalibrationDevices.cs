using System.Text;

namespace PSCAutomation.Apps.AS110AllCheck;

// 校正の自動読み取りで使う機器を、AutoCalibration.cs の interface に合わせて包む(実機用)。機器なしの確かめは Tests/Verification の模擬で行う。

// GC16 を探す(SPEC 決まったこと 26、AI の案 2)。M5Stack のポートには何も送らない。前回見つけた COM 番号を先に試す。
// 見つけたら RM,1(通信で操作する状態)にする。シリアル通信は待ちがあるので、画面を止めないよう別のスレッドで行う。
internal sealed class Gc16GaugeLocator(Func<string?> excludedPort) : IPressureGaugeLocator
{
    public DateTimeOffset? FirstConfirmedAt { get; private set; }

    public Task<IPressureGauge> FindAsync(CancellationToken cancellationToken)
    {
        string? excluded = excludedPort();
        string? preferred = Gc16PortMemory.Read();
        return Task.Run<IPressureGauge>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = Gc16Session.Find(new Gc16SearchOptions
            {
                PreferredPort = preferred,
                ExcludedPorts = excluded is null ? Array.Empty<string>() : new[] { excluded },
            });
            try
            {
                session.EnterRemote();
            }
            catch
            {
                session.Dispose();
                throw;
            }
            FirstConfirmedAt ??= DateTimeOffset.Now;
            Gc16PortMemory.Write(session.PortName);
            return new Gc16PressureGauge(session);
        }, cancellationToken);
    }
}

internal sealed class Gc16PressureGauge(Gc16Session session) : IPressureGauge
{
    public string PortName => session.PortName;

    public Task<double> ReadKilopascalsAsync(CancellationToken cancellationToken) =>
        Task.Run(session.ReadPressureKilopascals, cancellationToken);

    public Task ZeroAdjustAsync(CancellationToken cancellationToken) =>
        Task.Run(session.ZeroAdjust, cancellationToken);

    // RM,0 を送ってから閉じる(Gc16Session.Dispose)。
    public void Dispose() => session.Dispose();
}

// WF1973(または SDG1032X)の DC 出力で電流を指令する。接続確認でつないだ発振器をそのまま使う(1 台を 2 つのクラスで開かない)。
internal sealed class GeneratorCurrentOutput(IFunctionGeneratorSession generator) : ICurrentCommandOutput
{
    public void SetVolts(double volts) => generator.SetDcOutput(volts);
    public void Stop() => generator.StopOutput();
}

internal sealed class WaveLoggerCalibrationSource(WaveLoggerSession logger) : ICalibrationLoggerSource
{
    private const int Unit = 1;
    public bool HasActiveFile() => logger.HasActiveFile();
    public long OpenFile(string path) => logger.OpenFile(path);
    public long CloseActiveFile() => logger.CloseActiveFile();
    public long GetState() => logger.GetState();
    public long Start() => logger.Start();
    public void Stop() => logger.Stop();
    public long GetDataCount() => logger.GetDataCount();

    public float[] ReadRaw(int channelIndex, long start, int count)
    {
        var block = logger.ReadRawData(Unit, [channelIndex], start);
        float[] values = block.Values[0];
        return values.Length <= count ? values : values[..count];
    }
}

// マルチメータ ADCMT 7351E(USB)。全アプリ共通の部品をそのまま使う(AS110NoiseTest と同じ。2026-11-18)。
// 設定は部品の既定(DC 電流・200 mA レンジ・バス トリガー・オートゼロ)。通信は待ちがあるので別のスレッドで行う。
internal sealed class Adcmt7351ELocator : ICurrentMeterLocator
{
    public Task<ICurrentMeter> FindAsync(CancellationToken cancellationToken) => Task.Run<ICurrentMeter>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = Adcmt7351ESession.Connect();
        try
        {
            session.ApplySettings(new Adcmt7351ESettings());
            return new Adcmt7351ECurrentMeter(session);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }, cancellationToken);
}

internal sealed class Adcmt7351ECurrentMeter(Adcmt7351ESession session) : ICurrentMeter
{
    public string Name => "7351E";
    public Task<double> ReadMilliampsAsync(CancellationToken cancellationToken) => Task.Run(session.ReadMilliamps, cancellationToken);
    // トリガーを IMMEDIATE に戻して閉じる(前面で読める状態。Adcmt7351ESession.Dispose)。
    public void Dispose() => session.Dispose();
}

// 前回 GC16 を見つけた COM 番号(%LOCALAPPDATA%\AS110AllCheck\GC16のCOM番号.txt)。読めなければ null、書けなくても止めない。
internal static class Gc16PortMemory
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AS110AllCheck", "GC16のCOM番号.txt");

    public static string? Read()
    {
        try { return File.Exists(FilePath) ? File.ReadAllText(FilePath, Encoding.UTF8).Trim() is { Length: > 0 } port ? port : null : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static void Write(string portName)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, portName, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
