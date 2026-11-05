namespace PSCAutomation.Apps.AS110AllCheck;

// アプリ全体の起動時設定。
internal static class As110Application
{
    // M5Stack 通信の異常ログ。保存先 Logs に書けないときは LocalApplicationData\AS110AllCheck\Logs へ保存する
    // (移植前は LocalApplicationData\AS110BoltCheck\Logs。2026-11-05 に人の判断でアプリ名へそろえた)。
    // AS110 固有の異常種別は、移植前の AllCheck 版の一覧と共通の一覧の差。
    public static void ConfigureDiagnostics() => ArduinoDiagnosticLog.Configure(
        "AS110AllCheck", "NF_STALE", "FRAME_GAP", "NF_RECOVERY_START");
}
