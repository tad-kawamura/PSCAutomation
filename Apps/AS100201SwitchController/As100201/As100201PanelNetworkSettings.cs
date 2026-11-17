namespace PSCAutomation.Apps.AS100201SwitchController;

// タッチパネル(M5Stack Tab5)を Wi-Fi でつなぐときの接続先(SPEC_調整操作の記録.md「Wi-Fi・バッテリーへの変更」、2026-11-13 承認)。
// Tab5 が Wi-Fi の親機になり、PC は Tab5 専用の USB Wi-Fi 子機でつなぎっぱなしにする(アプリは接続先を切り替えない)。
// Tab5 を使うのはこのアプリだけなので、共通の設定ファイルではなくここに置く(人の決定)。
// 値はタッチパネルのスケッチ(firmware/Tab5LearningPanel)の WIFI_SSID・PANEL_IP・PANEL_PORT と揃える。
// パスワードはスケッチにだけ書く(PC は Windows に保存した子機の登録でつなぐ)。
internal static class As100201PanelNetworkSettings
{
    // Tab5 の親機の IP。社内 LAN と同じ範囲(192.168.4.x)なら、スケッチと両方を変える。
    public const string Host = "192.168.4.1";
    public const int Port = 50201;

    // TCP の接続を諦めるまで(2026-11-17 人の決定 B、共通部品の既定 3 秒から短くした)。Tab5 は同じ部屋の親機で、
    // 実機の ping は 2ms。子機が古いつながりのときは何秒待ってもつながらないので、早く諦めてつなぎ直しへ進む。
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(1);

    // 子機に保存した Wi-Fi の登録名(Windows が付ける名前で、ふつうはスケッチの WIFI_SSID と同じ)。
    // Wi-Fi で 2 回続けてつながらないとき、この登録を持つ子機だけを切ってつなぎ直す(2026-11-17 人の決定)。
    // 子機はアダプターの名前(「Wi-Fi 2」など、PC ごとに変わる)ではなく、この登録を持っていることで見つける。
    public const string WifiProfileName = "AS100201_PANEL";
}
