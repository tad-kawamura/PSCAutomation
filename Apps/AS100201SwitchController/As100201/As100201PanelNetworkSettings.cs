namespace PSCAutomation.Apps.AS100201SwitchController;

// タッチパネル(M5Stack Tab5)を Wi-Fi でつなぐときの接続先(SPEC_調整操作の記録.md「Wi-Fi・バッテリーへの変更」、2026-11-13 承認)。
// Tab5 が Wi-Fi の親機になり、PC は Tab5 専用の USB Wi-Fi 子機でつなぎっぱなしにする(アプリは Wi-Fi を切り替えない)。
// Tab5 を使うのはこのアプリだけなので、共通の設定ファイルではなくここに置く(人の決定)。
// 値はタッチパネルのスケッチ(firmware/Tab5LearningPanel)の PANEL_IP・PANEL_PORT と揃える。
// Wi-Fi の名前(SSID)とパスワードはスケッチにだけ書く(PC は Windows に保存した子機の登録でつなぐ)。
internal static class As100201PanelNetworkSettings
{
    // Tab5 の親機の IP。社内 LAN と同じ範囲(192.168.4.x)なら、スケッチと両方を変える。
    public const string Host = "192.168.4.1";
    public const int Port = 50201;
}
