using PSCAutomation.Wrappers.WaveLogger.Automation;

namespace PSCAutomation.Apps.AS110NoiseTest.Measurement;

// WAVE LOGGER(KEYENCE NR-500、USB)を ILoggerDevice として使う。
// Wrappers/WaveLogger.Automation の WaveLoggerSession を包むだけで、判断はしない。
// 使い方(Wrappers/WaveLogger.Automation/README.md のとおり):
//   - 1 つの STA スレッドで Connect から ShutdownAsync まで使い切る。別スレッドから触らない
//   - 終わりは必ず await ShutdownAsync(log)(Stop → Quit → COM 解放 → プロセス終了確認)。
//     Dispose は ShutdownAsync が済んでいないときの参照解放だけで、LOGGER を終了させる保証はない
internal sealed class WaveLoggerDevice : ILoggerDevice, IDisposable
{
    private readonly WaveLoggerSession _session;
    private bool _shutDown;

    private WaveLoggerDevice(WaveLoggerSession session, string description)
    {
        _session = session;
        Description = description;
    }

    public string Description { get; }
    public WaveLoggerSession Session => _session;

    // LOGGER を起動して NR-500(USB 機器 ID usbId)を確認する。
    // edition: Auto は WAVE LOGGER X を優先し、無ければ WAVE LOGGER(旧)。
    public static WaveLoggerDevice Connect(WaveLoggerEdition edition = WaveLoggerEdition.Auto, int usbId = 0, Action<string>? log = null)
    {
        var installed = WaveLoggerSession.GetInstalledEditions();
        if (installed.Count == 0)
            throw new InvalidOperationException("WAVE LOGGER がインストールされていません(COM 登録 WaveLoggerX.Application / WaveLogger2.Application が無い)。");
        var session = new WaveLoggerSession();
        try
        {
            session.Initialize(edition, log);
            WaveLoggerSession.HardwareCheckResult result = session.DetectNr500Usb(usbId);
            log?.Invoke($"LOGGER 本体確認: {result.Description}");
            if (!result.IsDetected)
                throw new InvalidOperationException(
                    $"NR-500 を確認できません(USB 機器 ID {usbId})。本体の電源・USB・LOGGER 画面を確認してください。{result.Description}");
            string description = $"{WaveLoggerSession.GetEditionDisplayName(session.ActiveEdition)} / {result.HardwareName} / USB ID {usbId}";
            return new WaveLoggerDevice(session, description);
        }
        catch
        {
            // 起動した LOGGER を残さない(README: 初期化・接続失敗時は Release)。
            try { session.Release(); } catch { }
            throw;
        }
    }

    public long OpenFile(string path) => _session.OpenFile(path);
    public long CloseActiveFile() => _session.CloseActiveFile();
    public long StartSingleAcquisition() => _session.StartSingleAcquisition();
    public long GetState() => _session.GetState();
    public void Stop() => _session.Stop();
    public long SaveXdt(string path) => _session.SaveXdt(path);

    // 通常の終了。呼び出し元の STA スレッド(同期コンテキストあり)で await する。
    public async Task ShutdownAsync(Action<string> log)
    {
        if (_shutDown) return;
        await _session.ShutdownAsync(log);
        _shutDown = true;
    }

    public void Dispose()
    {
        if (_shutDown) return;
        try { _session.Release(); } catch { }
        _shutDown = true;
    }
}
