using System;
using System.Collections.Generic;
using PSCAutomation.Wrappers.WaveLogger.Automation;

namespace PSCAutomation.Wrappers.Common;

// NR-500／NR-Xの接続方式と、機種ごとの接続処理をまとめる。
// NR-500はUSB、NR-XはUSBまたはWi-Fiの接続を扱う。
// NR-X用のWi-Fiプロファイル名・IPアドレス・ポート番号を保持する。
// 実際の本体確認はWaveLoggerSession、Wi-Fi切替は専用クラスへ依頼する。
// 元: AS100201SwitchController Logger/NrDeviceConnections.cs。Wi-Fiプロファイルの一覧(SSID・IP)はwrapperに持たない。
// 一覧はアプリ側の設定クラスに置く(例: AS100201SwitchController の As100201ConnectionSettings.cs の NrWifiProfiles)。
public enum NrConnectionMode
{
    Usb,
    Wifi
}

/// <summary>
/// 画面に表示する名前と、Windowsへ渡すWi-Fiプロファイル名を保持する。
/// 通常はSSIDとWindowsプロファイル名は同じ名前になる。
/// </summary>
public sealed class NrWifiProfile
{
    public NrWifiProfile(
        string displayName,
        string windowsProfileName,
        int address1,
        int address2,
        int address3,
        int address4,
        int tcpPort)
    {
        DisplayName = displayName;
        WindowsProfileName = windowsProfileName;
        Address1 = address1;
        Address2 = address2;
        Address3 = address3;
        Address4 = address4;
        TcpPort = tcpPort;
    }

    public string DisplayName { get; }

    public string WindowsProfileName { get; }

    public int Address1 { get; }
    public int Address2 { get; }
    public int Address3 { get; }
    public int Address4 { get; }
    public int TcpPort { get; }

    public override string ToString() => DisplayName;
}

/// <summary>
/// Wi-Fiアダプター構成に依存する処理の境界。
/// 将来は2台用の実装へ差し替えられる。
/// </summary>
public interface IWifiConnectionManager
{
    bool MustRestore { get; }

    Task ConnectAsync(
        string targetProfileName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task RestoreAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// NR-500固有の接続処理。NR-500はUSB接続だけを扱う。
/// </summary>
public sealed class Nr500Connection
{
    private readonly WaveLoggerSession _logger;
    public Nr500Connection(WaveLoggerSession logger) => _logger = logger;
    public WaveLoggerSession.HardwareCheckResult DetectUsb(int deviceId = 0)
    {
        return _logger.DetectNr500Usb(deviceId);
    }
}

/// <summary>
/// NR-X固有の接続処理。USBとWi-Fi/LANを扱う。
/// </summary>
public sealed class NrXConnection
{
    private static readonly TimeSpan WifiTimeout = TimeSpan.FromSeconds(20);
    private readonly WaveLoggerSession _logger;
    private readonly IWifiConnectionManager _wifiManager;

    public NrXConnection(WaveLoggerSession logger, IWifiConnectionManager wifiManager)
    {
        _logger = logger;
        _wifiManager = wifiManager;
    }

    public bool WifiRestoreRequired => _wifiManager.MustRestore;

    public WaveLoggerSession.HardwareCheckResult DetectUsb(int deviceId = 0)
    {
        return _logger.DetectNrX(deviceId, NrXConnectionMethod.Usb);
    }

    public Task ConnectWifiAsync(
        string wifiProfileName,
        CancellationToken cancellationToken = default)
    {
        return _wifiManager.ConnectAsync(
            wifiProfileName,
            WifiTimeout,
            cancellationToken);
    }

    public WaveLoggerSession.HardwareCheckResult DetectWifi(
        NrWifiProfile profile,
        int deviceId = 0)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return _logger.ConnectAndDetectNrXWifi(
            deviceId,
            profile.Address1,
            profile.Address2,
            profile.Address3,
            profile.Address4,
            profile.TcpPort);
    }

    public Task RestorePreviousWifiAsync(
        CancellationToken cancellationToken = default)
    {
        return _wifiManager.RestoreAsync(WifiTimeout, cancellationToken);
    }
}
