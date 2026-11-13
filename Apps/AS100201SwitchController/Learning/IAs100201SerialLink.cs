namespace PSCAutomation.Apps.AS100201SwitchController;

// タッチパネルとの行単位の通信を、機器なしの模擬に差し替えるための薄い interface(AGENTS.md の方針)。
// 実機では USB(共通部品 ArduinoConnection、COM ポートの総当たり)と Wi-Fi(共通部品 TcpLineConnection)の 2 本を
// As100201PanelPathLink でまとめて使う(2026-11-13 承認。USB 優先、切れたらもう一方へ)。
internal interface IAs100201SerialLink : IDisposable
{
    event Action<string>? LineReceived;
    event Action? Disconnected;
    bool IsConnected { get; }
    Task VerifyAsync(string request, Func<string, bool> isReply, Action<string>? timingLog = null);
    Task DisconnectAsync();
    void SendLine(string line);
}

// USB シリアル(COM ポートの総当たり)。
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

// Wi-Fi(Tab5 の親機へ TCP で接続。接続先は As100201PanelNetworkSettings)。
internal sealed class As100201NetworkLink : IAs100201SerialLink
{
    private readonly TcpLineConnection _connection;

    public As100201NetworkLink(string host, int port)
    {
        _connection = new TcpLineConnection(host, port);
        _connection.LineReceived += line => LineReceived?.Invoke(line);
        _connection.Disconnected += () => Disconnected?.Invoke();
    }

    public event Action<string>? LineReceived;
    public event Action? Disconnected;
    public string Endpoint => _connection.Endpoint;
    public bool IsConnected => _connection.IsConnected;
    public Task VerifyAsync(string request, Func<string, bool> isReply, Action<string>? timingLog = null) =>
        _connection.VerifyAsync(request, isReply, timingLog);
    public Task DisconnectAsync() => _connection.DisconnectAsync();
    public void SendLine(string line) => _connection.SendLine(line);
    public void Dispose() => _connection.Dispose();
}
