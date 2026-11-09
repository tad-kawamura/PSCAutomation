namespace PSCAutomation.Apps.AS100201SwitchController;

// タッチパネルとの行単位の通信を、機器なしの模擬に差し替えるための薄い interface(AGENTS.md の方針)。
// 実機では共通部品 ArduinoConnection(USB シリアル、COM ポートの総当たり)をそのまま使う。
internal interface IAs100201SerialLink : IDisposable
{
    event Action<string>? LineReceived;
    event Action? Disconnected;
    bool IsConnected { get; }
    Task VerifyAsync(string request, Func<string, bool> isReply, Action<string>? timingLog = null);
    Task DisconnectAsync();
    void SendLine(string line);
}

internal sealed class As100201SerialLink : IAs100201SerialLink
{
    private readonly ArduinoConnection _connection = new();

    public As100201SerialLink()
    {
        _connection.LineReceived += line => LineReceived?.Invoke(line);
        _connection.Disconnected += () => Disconnected?.Invoke();
    }

    public event Action<string>? LineReceived;
    public event Action? Disconnected;
    public bool IsConnected => _connection.IsConnected;
    public Task VerifyAsync(string request, Func<string, bool> isReply, Action<string>? timingLog = null) =>
        _connection.VerifyAsync(request, isReply, timingLog);
    public Task DisconnectAsync() => _connection.DisconnectAsync();
    public void SendLine(string line) => _connection.SendLine(line);
    public void Dispose() => _connection.Dispose();
}
