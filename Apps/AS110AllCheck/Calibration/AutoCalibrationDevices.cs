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

// NR-500 の今の電圧(換算前の値)を LOGGER から読む。LOGGER にファイルが開いていなければ、そのモードの雛形を開く(校正は雛形を読み込む前に行うため)。
// 未確認(SPEC「未確認」): GetCurrentRawValue が測定していないときも値を返すか、返す値が CH の電圧(V)そのものか。
internal sealed class LoggerChannelVoltageMeter(WaveLoggerSession logger, Func<string> templatePath) : IChannelVoltageMeter
{
    private const int Unit = 1;
    // この読み取りのために雛形を開いたか(校正をやめたときに閉じるため)。
    public bool OpenedTemplate { get; private set; }

    public double ReadVolts(int channel)
    {
        if (!logger.HasActiveFile())
        {
            string path = templatePath();
            long result = logger.OpenFile(path);
            if (result != 0)
                throw new InvalidOperationException($"電圧を読むために LOGGER で雛形を開けませんでした（戻り値 {result}）。{Path.GetFileName(path)}");
            OpenedTemplate = true;
        }
        // メーカー API のチャンネル番号は 0 始まり(xdt の CH 番号 − 1)。
        float? value = logger.GetCurrentRawValue(Unit, channel - 1);
        return value ?? throw new InvalidDataException(
            $"LOGGER から CH{channel} の今の値を読めません（無効値）。NR-500 の接続と LOGGER の状態を確かめてください。");
    }
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
