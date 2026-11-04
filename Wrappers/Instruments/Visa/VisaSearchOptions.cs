namespace PSCAutomation.Wrappers.Instruments;

// VisaInstrumentSession.Connect の探し方。既定値は AS100201SwitchController と同じ
// (?*INSTR を総当たり、USB に限らない、タイムアウト 2000ms、失敗時の詳細なし)。
public sealed record VisaSearchOptions
{
    // FindRsrc に渡すパターン。AS110NoiseTest は USB の VID/PID を指定していた(例: USB?*::0x0D4A::0x000D::?*::INSTR)。
    public string ResourcePattern { get; init; } = "?*INSTR";
    // 指定すると探さずにこのリソースだけを開く(AS110NoiseTest の Connect(resource))。
    public string? Resource { get; init; }
    // true なら "USB" で始まるリソースだけを試す(AS110AllCheck の発振器。LAN・シリアル機器に *IDN? を送らない)。
    public bool UsbOnly { get; init; }
    // 開いた直後に設定するタイムアウト(ms)。
    public int TimeoutMilliseconds { get; init; } = 2000;
    // true なら、見つからなかったときのメッセージに、通信できなかったリソースと理由を 1 行ずつ付ける。
    public bool ReportCommunicationErrors { get; init; }
    // 指定すると、*IDN? が合わなかったリソースも「<リソース>: <名前> ではありません(<応答>)」として付ける。
    public string? RejectedIdentityName { get; init; }
}
