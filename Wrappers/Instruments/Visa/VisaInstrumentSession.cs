using System.Runtime.InteropServices;
using Ivi.Visa.Interop;

namespace PSCAutomation.Wrappers.Instruments;

// VISA 機器を探して識別し、接続(COM オブジェクト)の所有権を持つ。
// 元: AS100201SwitchController Generator/VisaInstrumentSession.cs(AS110AllCheck の FunctionGeneratorConnection が土台)。
// モノレポで AS110NoiseTest Devices/VisaUtil.cs の探し方(パターン指定・リソース直接指定・該当なしを例外にしない)と、
// AS110AllCheck の USB 限定・失敗理由の一覧を VisaSearchOptions で選べるようにした。
// どの機種かの判定と接続直後の初期化コマンドは呼び出し側(機器クラス)が渡す。
public sealed class VisaInstrumentSession : IInstrumentSession
{
    private IVisaChannel? _channel;
    private readonly Action _release;

    private VisaInstrumentSession(IVisaChannel channel, string address, string identity, Action release)
    {
        _channel = channel;
        Address = address;
        Identity = identity;
        _release = release;
    }

    public string Address { get; }
    public string Identity { get; }
    public bool IsConnected => _channel is not null;

    public int TimeoutMilliseconds
    {
        get => Channel.TimeoutMilliseconds;
        set => Channel.TimeoutMilliseconds = value;
    }

    // 探して *IDN? を送り、acceptsIdentity が true を返した最初の機器へ initialize を実行して返す。
    // 見つからなければ InvalidOperationException(failureMessage)。
    // 通信できないリソース(COMException・InvalidCastException)は飛ばして次を試す。initialize でのそれも同じ。
    // それ以外の例外は、開いた接続を閉じてから呼び出し側へ投げる。
    public static VisaInstrumentSession Connect(Func<string, bool> acceptsIdentity,
        Action<IInstrumentSession> initialize, string failureMessage, VisaSearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(acceptsIdentity);
        ArgumentNullException.ThrowIfNull(initialize);
        options ??= new VisaSearchOptions();
        var manager = new ResourceManager();
        try
        {
            string[] addresses = string.IsNullOrWhiteSpace(options.Resource)
                ? Find(manager, options.ResourcePattern)
                : [options.Resource];
            int timeout = options.TimeoutMilliseconds;
            return Search(addresses, address => ComVisaChannel.Open(manager, address, timeout),
                acceptsIdentity, initialize, failureMessage, options, () => Release(manager));
        }
        catch { Release(manager); throw; }
    }

    // 探索の本体。通信の開き方を差し替えられるよう分けてある(テストで模擬の通信を渡す)。
    // 成功したら release(ResourceManager の解放)の責任は返したセッションへ移る。失敗時は呼び出し側が解放する。
    internal static VisaInstrumentSession Search(IEnumerable<string?> addresses, Func<string, IVisaChannel> open,
        Func<string, bool> acceptsIdentity, Action<IInstrumentSession> initialize, string failureMessage,
        VisaSearchOptions options, Action release)
    {
        var errors = new List<string>();
        foreach (string? address in addresses)
        {
            if (string.IsNullOrWhiteSpace(address)) continue;
            if (options.UsbOnly && !address.StartsWith("USB", StringComparison.OrdinalIgnoreCase)) continue;
            IVisaChannel? channel = null;
            try
            {
                channel = open(address);
                channel.Write("*IDN?");
                string identity = channel.Read().Trim();
                if (!acceptsIdentity(identity))
                {
                    if (options.RejectedIdentityName is { } name)
                        errors.Add(address + ": " + name + " ではありません(" + identity + ")");
                    continue;
                }
                var session = new VisaInstrumentSession(channel, address, identity, release);
                initialize(session);
                channel = null; // session へ所有権を移す。
                return session;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                if (options.ReportCommunicationErrors) errors.Add(address + ": " + ex.Message);
            }
            finally { channel?.Close(); }
        }
        string detail = errors.Count == 0 ? "" : "\n" + string.Join("\n", errors);
        throw new InvalidOperationException(failureMessage + detail);
    }

    public void Write(string command) => Channel.Write(command);

    public string Read() => Channel.Read().Trim();

    public string Query(string command)
    {
        var channel = Channel;
        channel.Write(command);
        return channel.Read().Trim();
    }

    public void Dispose()
    {
        if (_channel is null) return;
        _channel.Close();
        _channel = null;
        _release();
    }

    private IVisaChannel Channel => _channel ?? throw new ObjectDisposedException(nameof(VisaInstrumentSession));

    // パターンに合う VISA リソースを返す。見つからなければ空配列(該当なしのときも VISA は例外を返すため)。
    private static string[] Find(ResourceManager manager, string pattern)
    {
        try
        {
            return ((Array)manager.FindRsrc(pattern))
                .Cast<object>()
                .Select(item => item?.ToString() ?? "")
                .Where(address => address.Length > 0)
                .ToArray();
        }
        catch (COMException) { return []; }
    }

    internal static void Release(object value)
    {
        if (Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
}

// 1 本の VISA 接続の生の送受信。テストでは模擬に差し替える。
internal interface IVisaChannel
{
    int TimeoutMilliseconds { get; set; }
    void Write(string command);
    string Read();
    // 閉じて COM オブジェクトを解放する。閉じるときの COMException は無視する(元のまま)。
    void Close();
}

// FormattedIO488 による実際の接続。
internal sealed class ComVisaChannel : IVisaChannel
{
    private readonly FormattedIO488 _io;

    private ComVisaChannel(FormattedIO488 io) => _io = io;

    public static ComVisaChannel Open(ResourceManager manager, string address, int timeoutMilliseconds)
    {
        var io = new FormattedIO488 { IO = (IMessage)manager.Open(address) };
        var channel = new ComVisaChannel(io);
        try { io.IO.Timeout = timeoutMilliseconds; }
        catch { channel.Close(); throw; }
        return channel;
    }

    public int TimeoutMilliseconds
    {
        get => _io.IO.Timeout;
        set => _io.IO.Timeout = value;
    }

    public void Write(string command) => _io.WriteString(command, true);

    public string Read() => _io.ReadString();

    public void Close()
    {
        try { _io.IO?.Close(); } catch (COMException) { }
        VisaInstrumentSession.Release(_io);
    }
}
