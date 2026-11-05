namespace PSCAutomation.Apps.AS110AllCheck;

/// <summary>
/// 使用可能なNR-X用Wi-Fiプロファイルの一覧。
/// 機器を増やす場合はAllへNrWifiProfileを1行追加する。
/// パスワードはWindowsのWi-Fiプロファイルが保持するため、ここには保存しない。
/// </summary>
/// <remarks>
/// 元: AS110AllCheck Logger/NrDeviceConnections.cs。SSID・IP はアプリ固有の値なので wrapper(Wrappers/Common)には置かない
/// (2026-11-05 に人の判断で、アプリの設定クラスに置いたまま移した)。
/// </remarks>
internal static class NrWifiProfiles
{
    public static IReadOnlyList<NrWifiProfile> All { get; } =
    [
        new(
            "NRX_E8FDF803634C",
            "NRX_E8FDF803634C",
            192, 168, 100, 100,
            24682)
    ];
}
