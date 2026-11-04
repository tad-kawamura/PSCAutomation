using System.Collections.Concurrent;
using PSCAutomation.Wrappers.Common;
using PSCAutomation.Wrappers.WaveLogger.Automation;

// Common(元: AS110AllCheck 他 Common/・Logger/)の検査。モノレポで追加。
// 実機・Wi-Fi 切替(netsh)・メッセージボックスは使わない。SingleAdapterWifiManager は PC の Wi-Fi を変えるので呼ばない。
void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

// PcDelayObservation: 書き込み先を Start(write) で受け取る(元は ArduinoDiagnosticLog.Write 直書き)
try { PcDelayObservation.Start(null!); throw new Exception("書き込み先なしで開始できた"); }
catch (ArgumentNullException) { }
var written = new ConcurrentQueue<(string Kind, string Detail)>();
using (PcDelayObservation.Start((kind, detail) => written.Enqueue((kind, detail))))
{
    PcDelayObservation.Report("TEST_KIND", "テスト用の報告");
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (DateTime.UtcNow < deadline && !written.Any(w => w.Kind == "TEST_KIND")) Thread.Sleep(50);
}
Check(written.Any(w => w.Kind == "OBS_START"), "開始記録が書き込み先に届かない");
Check(written.Any(w => w.Kind == "TEST_KIND" && w.Detail.Contains("テスト用の報告")), "Report の内容が書き込み先に届かない");
Console.WriteLine("PASS PcDelayObservation: 開始記録と Report を、Start で渡した書き込み先へ専用スレッドから書く");

// NrXConnection: Wi-Fi 切替は IWifiConnectionManager に任せ、プロファイル名と 20 秒の待ち時間を渡す
var wifi = new FakeWifi();
var connection = new NrXConnection(new WaveLoggerSession(), wifi);
Check(!connection.WifiRestoreRequired, "初期状態で復帰が必要になっている");
await connection.ConnectWifiAsync("NRX_TEST");
Check(wifi.Calls.SequenceEqual(new[] { "Connect NRX_TEST 20" }) && connection.WifiRestoreRequired, "Wi-Fi 接続の受け渡し");
await connection.RestorePreviousWifiAsync();
Check(wifi.Calls.Last() == "Restore 20" && !connection.WifiRestoreRequired, "Wi-Fi 復帰の受け渡し");
try { connection.DetectWifi(null!); throw new Exception("プロファイルなしで接続確認できた"); }
catch (ArgumentNullException) { }
var profile = new NrWifiProfile("表示名", "PROFILE", 192, 168, 100, 100, 24682);
Check(profile.ToString() == "表示名" && profile.WindowsProfileName == "PROFILE" && profile.TcpPort == 24682, "NrWifiProfile の値");
Console.WriteLine("PASS NrXConnection: Wi-Fi の接続・復帰を切替クラスへ渡し、プロファイル一覧は持たない(アプリ側で作る)");

// WindowActivation: 無効なウィンドウでは何もしない
Check(!WindowActivation.TryActivate(IntPtr.Zero), "無効なハンドルで true");
Console.WriteLine("PASS WindowActivation: 無効なハンドルでは false");
Console.WriteLine("Common: all checks passed.");

internal sealed class FakeWifi : IWifiConnectionManager
{
    public List<string> Calls { get; } = new();
    public bool MustRestore { get; private set; }

    public Task ConnectAsync(string targetProfileName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Calls.Add($"Connect {targetProfileName} {timeout.TotalSeconds}");
        MustRestore = true;
        return Task.CompletedTask;
    }

    public Task RestoreAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Calls.Add($"Restore {timeout.TotalSeconds}");
        MustRestore = false;
        return Task.CompletedTask;
    }
}
