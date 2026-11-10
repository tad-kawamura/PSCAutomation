namespace PSCAutomation.Apps.AS100201SwitchController;

internal static class As100201Application
{
    public const string WindowTitle = "AS100-201 スイッチ制御";
    public const string Heading = "AS100-201 スイッチ制御・測定";
    public const string Instructions = "起動時に接続を確認します。Arduinoは自動再接続します。\r\n" +
        "全て接続した後、WAVE LOGGER の雛形ファイルをドロップしてください。";

    // 学習モード(SPEC_調整操作の記録.md)。タイトルと画面上部に出す。
    public const string LearningModeLabel = "学習モード";
    public const string LearningWindowTitle = WindowTitle + "【" + LearningModeLabel + "】";
    public const string LearningInstructions = "雛形は接続確認のあとに自動で読み込みます（ドロップ不要。別の雛形を使うときだけ「雛形を削除」してドロップ）。\r\n" +
        "タッチパネルの操作ボタンは、手を動かす前に押してください。完了は盲栓確認が OK のとき、取消は直前の操作 1 件だけです。\r\n" +
        "測定が止まるたび（sw4・操作ボタン）に xdt を保存し、台帳（CSV）に 1 行ずつ追記します。保存できないときは READY LED を消して止まり、「再試行」で再開します。";

    // 開発用(--virtual-panel)。タッチパネルの代わりを画面に出しているときのタイトルの後ろ。
    public const string VirtualPanelTitleSuffix = "（開発用：タッチパネルの代わり）";

    public static string GetWindowTitle(As100201StartupMode mode) =>
        mode == As100201StartupMode.Learning ? LearningWindowTitle : WindowTitle;

    // タッチパネルの異常種別(PANEL_*)は学習モードだけで起きる。通常モードの異常ログは今までと同じ。
    public static void ConfigureDiagnostics() => ArduinoDiagnosticLog.Configure(
        "AS100201SwitchController", "HEARTBEAT_STALE", "FRAME_GAP", "PROTOCOL_RECOVERY",
        "PANEL_HEARTBEAT_STALE", "PANEL_FRAME_GAP", "PANEL_RECOVERY", "PANEL_VERSION", "PANEL_SEND_ERROR");
}
