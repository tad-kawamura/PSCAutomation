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
        // Tab5 の COM ポートだけを試す(2026-11-18 人の決定 ①)。ほかの USB シリアル機器(FTDI など)を 1 台約 3.7 秒ずつ試していた。
        _connection.SelectPorts = names => SelectPanelPorts(names, As100201UsbDevices.FindPortNames(PanelUsbIdPrefix));
    }

    // 試すポート: Tab5 の COM ポートのうち今ある分を、それぞれ 2 回(開いた瞬間に Tab5 が再起動して 1 回目は返事が来ないため。
    // 2026-11-18 の実機では、再起動から返事まで約 6.2 秒)。Tab5 の COM ポートが分からないとき(null)は今までどおり全部。
    public static IReadOnlyList<string> SelectPanelPorts(string[] names, IReadOnlyList<string>? panelPorts)
    {
        if (panelPorts is null) return names;
        var targets = new List<string>();
        foreach (string port in panelPorts.Distinct(StringComparer.OrdinalIgnoreCase))
            if (names.Contains(port, StringComparer.OrdinalIgnoreCase)) targets.AddRange(new[] { port, port });
        return targets;
    }

    // Tab5 の COM ポートができているか(2026-11-18 人の決定 ②: 挿してからの 2 秒待ちをやめ、できたらすぐ USB へ戻す)。
    public static As100201UsbState PanelUsbState()
    {
        if (As100201UsbDevices.IsPresent(PanelUsbIdPrefix) is not bool present) return As100201UsbState.Unknown;
        if (!present) return As100201UsbState.Absent;
        var ports = As100201UsbDevices.FindPortNames(PanelUsbIdPrefix);
        if (ports is null) return As100201UsbState.Present;
        string[] names = System.IO.Ports.SerialPort.GetPortNames();
        return SelectPanelPorts(names, ports).Count > 0 ? As100201UsbState.PortReady : As100201UsbState.Present;
    }

    public event Action<string>? LineReceived;
    public event Action? Disconnected;
    public bool IsConnected => _connection.IsConnected;

    public async Task VerifyAsync(string request, Func<string, bool> isReply, Action<string>? timingLog = null)
    {
        // Tab5(Espressif の USB 機器)が挿さっていなければ、COM ポートを総当たりしない(2026-11-17)。
        // 共通部品は全部の COM ポートを 1 つずつ開いて待つので、ほかの USB シリアル機器があると 1 台約 3.7 秒かかり、
        // USB を抜いたあと Wi-Fi に移るのが遅れた(実機で約 40 秒)。分からないとき(null)は今までどおり総当たりする。
        if (As100201UsbDevices.IsPresent(PanelUsbIdPrefix) == false)
            throw new InvalidOperationException(PanelNotPlugged);
        try
        {
            await _connection.VerifyAsync(request, isReply, timingLog).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not ObjectDisposedException && ex.Message.StartsWith(ArduinoNotFound, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(ToPanelMessage(ex.Message), ex);
        }
    }

    // 共通部品(ArduinoConnection)の見つからないときの文は Arduino 向け。ここはタッチパネルなので言い換える(2026-11-17、人の指摘)。
    public const string ArduinoNotFound = "Arduino を確認できません。";
    public const string PanelNotFound = "タッチパネルを COM ポートで確認できません。";
    public const string PanelNotPlugged = "タッチパネルの USB が挿さっていません（Espressif の USB 機器が見つかりません）。";
    // Tab5(ESP32-P4)の USB の機器 ID の先頭(Espressif の VID 303A。実機は USB\VID_303A&PID_1001&MI_00)。
    public const string PanelUsbIdPrefix = @"USB\VID_303A&";
    public static string ToPanelMessage(string message) =>
        message.StartsWith(ArduinoNotFound, StringComparison.Ordinal) ? PanelNotFound + message[ArduinoNotFound.Length..] : message;

    public Task DisconnectAsync() => _connection.DisconnectAsync();
    public void SendLine(string line) => _connection.SendLine(line);
    public void Dispose() => _connection.Dispose();
}

// Tab5 の USB の様子。Unknown: Windows に聞けない、Absent: 挿さっていない、
// Present: 挿さっているが COM ポートがまだ無い(または分からない)、PortReady: COM ポートがある。
internal enum As100201UsbState { Unknown, Absent, Present, PortReady }

// いま挿さっている USB 機器の ID を Windows(cfgmgr32)に聞く。機器は開かない。
internal static class As100201UsbDevices
{
    private const int FilterEnumerator = 0x1;   // CM_GETIDLIST_FILTER_ENUMERATOR
    private const int FilterPresent = 0x100;    // CM_GETIDLIST_FILTER_PRESENT(挿さっているものだけ)

    // idPrefix で始まる機器が挿さっていれば true、無ければ false、聞けなければ null。
    public static bool? IsPresent(string idPrefix)
    {
        try
        {
            const int flags = FilterEnumerator | FilterPresent;
            if (CM_Get_Device_ID_List_Size(out int length, "USB", flags) != 0 || length <= 0) return null;
            var buffer = new char[length];
            if (CM_Get_Device_ID_List("USB", buffer, length, flags) != 0) return null;
            foreach (string id in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                if (id.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    // idPrefix で始まる、いま挿さっている機器の COM ポート名(機器のレジストリの Device Parameters\PortName)。
    // 挿さっていなければ空、聞けなければ null。Tab5 は USB\VID_303A&PID_1001&MI_00\... が COM ポートを持つ。
    public static IReadOnlyList<string>? FindPortNames(string idPrefix)
    {
        try
        {
            const int flags = FilterEnumerator | FilterPresent;
            if (CM_Get_Device_ID_List_Size(out int length, "USB", flags) != 0 || length <= 0) return null;
            var buffer = new char[length];
            if (CM_Get_Device_ID_List("USB", buffer, length, flags) != 0) return null;
            var ports = new List<string>();
            foreach (string id in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!id.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{id}\Device Parameters");
                if (key?.GetValue("PortName") is string port && port.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) ports.Add(port);
            }
            return ports;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_Size(out int length, string? filter, int flags);
    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List(string? filter, char[] buffer, int length, int flags);
}

// Wi-Fi(Tab5 の親機へ TCP で接続。接続先は As100201PanelNetworkSettings)。
internal sealed class As100201NetworkLink : IAs100201SerialLink
{
    private readonly TcpLineConnection _connection;

    // connectTimeout: TCP の接続を諦めるまで(null なら共通部品の既定 3 秒)。
    public As100201NetworkLink(string host, int port, TimeSpan? connectTimeout = null)
    {
        _connection = connectTimeout is TimeSpan timeout
            ? new TcpLineConnection(host, port) { ConnectTimeout = timeout }
            : new TcpLineConnection(host, port);
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
