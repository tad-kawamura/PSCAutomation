namespace PSCAutomation.Apps.AS100201SwitchController;

internal static class As100201Application
{
    public const string WindowTitle = "AS100-201 スイッチ制御";
    public const string Heading = "AS100-201 スイッチ制御・測定";
    public const string Instructions = "起動時に接続を確認します。Arduinoは自動再接続します。\r\n" +
        "全て接続した後、WAVE LOGGER の雛形ファイルをドロップしてください。";

    public static void ConfigureDiagnostics() => ArduinoDiagnosticLog.Configure(
        "AS100201SwitchController", "HEARTBEAT_STALE", "FRAME_GAP", "PROTOCOL_RECOVERY");
}
