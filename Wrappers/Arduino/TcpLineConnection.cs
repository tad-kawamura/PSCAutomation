using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using PSCAutomation.Wrappers.Common;

namespace PSCAutomation.Wrappers.Arduino;

// マイコンなどとの行単位テキストの通信を、ネットワーク(TCP)で行う。ArduinoConnection(USB シリアル)の TCP 版。
// 決まった相手(IP またはホスト名とポート番号)へ PC から接続しに行く。PC は待ち受けないので、Windows のファイアウォールに例外は要らない。
// 接続確認用の送信文字列と応答の判定条件は、呼び出し側から受け取る(ArduinoConnection と同じ)。
// 受信行の意味と定期応答の見張り・再接続の方針は、呼び出し側が決める。
// 行は UTF-8・改行は LF(受信では CR を捨て、前後の空白を除く。空行は捨てる)。1 行 1024 バイトを超えたら改行まで捨てる。
// 特定の機種・アプリの送受信文字列や接続先は、このファイルへ記載しない。
// 追加: 2026-11-13(AS100201SwitchController のタッチパネル Tab5 を Wi-Fi でつなぐため)。
public sealed class TcpLineConnection : IDisposable
{
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan DefaultReplyTimeout = TimeSpan.FromSeconds(2);
    private const int MaxLineBytes = 1024;
    private const int WriteTimeoutMilliseconds = 1000;

    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly object _lifetimeGate = new();
    private readonly object _sendGate = new();
    private Session? _session;
    private PendingReply? _pendingReply;
    private volatile bool _disposed;

    public TcpLineConnection(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        Host = host;
        Port = port;
    }

    public string Host { get; }
    public int Port { get; }
    public string Endpoint => $"{Host}:{Port}";
    // 接続そのもの(TCP のつながり)を待つ時間。
    public TimeSpan ConnectTimeout { get; init; } = DefaultConnectTimeout;
    // 接続確認の文字列を送ってから応答を待つ時間(ArduinoConnection と同じ 2 秒)。
    public TimeSpan ReplyTimeout { get; init; } = DefaultReplyTimeout;

    public event Action<string>? LineReceived;
    // 相手が閉じた・読み書きに失敗したときに知らせる。DisconnectAsync・Dispose・接続確認の失敗では知らせない。
    public event Action? Disconnected;

    public bool IsConnected
    {
        get { lock (_lifetimeGate) return !_disposed && _session is { Closed: false }; }
    }

    private sealed class Session
    {
        internal readonly TcpClient Client;
        internal readonly NetworkStream Stream;
        internal readonly Thread Reader;
        internal volatile bool Closed;

        internal Session(TcpLineConnection owner, TcpClient client)
        {
            Client = client;
            Stream = client.GetStream();
            Stream.WriteTimeout = WriteTimeoutMilliseconds;
            Reader = new Thread(() => owner.ReadLoop(this)) { IsBackground = true, Name = "TCP line reader" };
        }
    }

    private sealed class PendingReply(Session session, Func<string, bool> isReply)
    {
        public readonly Session Session = session;
        public readonly Func<string, bool> IsReply = isReply;
        public readonly TaskCompletionSource<bool> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // つながっていなければ接続して確認する。つながっていれば何もしない。
    public async Task ConnectAsync(string request, Func<string, bool> isReply, Action<string>? timingLog = null)
    {
        await _connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateRequest(request, isReply);
            if (IsConnected) return;
            await ConnectCoreAsync(request, isReply, timingLog).ConfigureAwait(false);
        }
        finally { _connectionGate.Release(); }
    }

    // つながっていなければ接続して確認する。つながっていれば同じ接続で確認し直す(応答がなければ閉じて例外)。
    public async Task VerifyAsync(string request, Func<string, bool> isReply, Action<string>? timingLog = null)
    {
        await _connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateRequest(request, isReply);
            Session? session;
            lock (_lifetimeGate) session = _session is { Closed: false } current ? current : null;
            if (session is null)
            {
                await ConnectCoreAsync(request, isReply, timingLog).ConfigureAwait(false);
                return;
            }
            try { await ExchangeAsync(session, request, isReply).ConfigureAwait(false); }
            catch (Exception ex)
            {
                ArduinoDiagnosticLog.Write("VERIFY_ERROR", $"TCP {Endpoint} / {ex.Message}");
                CloseSession(session, notify: false);
                throw;
            }
        }
        finally { _connectionGate.Release(); }
    }

    // 再接続の方針は呼び出し側に任せる。終了とは別に接続だけ閉じる。
    public async Task DisconnectAsync()
    {
        await _connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Session? session;
            lock (_lifetimeGate) session = _session;
            if (session is not null) CloseSession(session, notify: false);
        }
        finally { _connectionGate.Release(); }
    }

    public void SendLine(string line)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Session? session;
        lock (_lifetimeGate) session = _session is { Closed: false } current ? current : null;
        if (session is null) throw new InvalidOperationException("TCP の接続がありません。");
        Send(session, line);
    }

    private static void ValidateRequest(string request, Func<string, bool> isReply)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        ArgumentNullException.ThrowIfNull(isReply);
    }

    private async Task ConnectCoreAsync(string request, Func<string, bool> isReply, Action<string>? timingLog)
    {
        Session? previous;
        lock (_lifetimeGate) previous = _session;
        if (previous is not null) CloseSession(previous, notify: false);

        var timer = Stopwatch.StartNew();
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = new CancellationTokenSource(ConnectTimeout);
            await client.ConnectAsync(Host, Port, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
        {
            client.Dispose();
            string reason = ex is OperationCanceledException
                ? $"{ConnectTimeout.TotalSeconds:0.#} 秒以内につながりません"
                : ex.Message;
            ArduinoDiagnosticLog.Write("TCP_CONNECT_FAILED", $"{Endpoint} / {reason}");
            timingLog?.Invoke($"TCP内訳：{Endpoint} 接続失敗 = {timer.Elapsed.TotalSeconds:F3} 秒 / {reason}");
            throw new InvalidOperationException($"{Endpoint} に接続できません（{reason}）。", ex);
        }
        timingLog?.Invoke($"TCP内訳：{Endpoint} 接続 = {timer.Elapsed.TotalSeconds:F3} 秒");

        Session session;
        lock (_lifetimeGate)
        {
            if (_disposed)
            {
                client.Dispose();
                throw new ObjectDisposedException(nameof(TcpLineConnection));
            }
            session = new Session(this, client);
            _session = session;
            session.Reader.Start();
        }
        timer.Restart();
        try { await ExchangeAsync(session, request, isReply).ConfigureAwait(false); }
        catch (Exception ex)
        {
            CloseSession(session, notify: false);
            ArduinoDiagnosticLog.Write("TCP_CONNECT_FAILED", $"{Endpoint} / 確認応答なし / {ex.Message}");
            timingLog?.Invoke($"TCP内訳：{Endpoint} 応答なし = {timer.Elapsed.TotalSeconds:F3} 秒");
            if (ex is ObjectDisposedException && _disposed) throw;
            throw new InvalidOperationException($"{Endpoint} から確認応答がありません（{ex.Message}）。", ex);
        }
        timingLog?.Invoke($"TCP内訳：{Endpoint} 応答待ち = {timer.Elapsed.TotalSeconds:F3} 秒");
        ArduinoDiagnosticLog.Write("TCP_CONNECTED", $"{Endpoint} / 確認応答を受信");
    }

    private async Task ExchangeAsync(Session session, string request, Func<string, bool> isReply)
    {
        var pending = new PendingReply(session, isReply);
        Volatile.Write(ref _pendingReply, pending);
        try
        {
            Send(session, request);
            await pending.Completion.Task.WaitAsync(ReplyTimeout).ConfigureAwait(false);
        }
        finally { Interlocked.CompareExchange(ref _pendingReply, null, pending); }
    }

    private void Send(Session session, string line)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        ArduinoDiagnosticLog.Write("TX", $"TCP {line}");
        try
        {
            lock (_sendGate)
            {
                using var observation = PcDelayObservation.Begin("Tcp.WriteLine");
                session.Stream.Write(bytes, 0, bytes.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            if (!session.Closed) ArduinoDiagnosticLog.Write("WRITE_ERROR", $"TCP {Endpoint} / {ex.Message}");
            CloseSession(session, notify: true);
            if (ex is ObjectDisposedException) throw new IOException("TCP の接続が閉じられています。", ex);
            throw;
        }
    }

    private void ReadLoop(Session session)
    {
        byte[] buffer = new byte[512];
        var line = new List<byte>(128);
        bool discardUntilNewline = false;
        try
        {
            while (!session.Closed)
            {
                int count = session.Stream.Read(buffer, 0, buffer.Length);
                if (count <= 0)
                {
                    if (!session.Closed) ArduinoDiagnosticLog.Write("TCP_CLOSED_BY_PEER", Endpoint);
                    break;
                }
                for (int i = 0; i < count; i++)
                {
                    byte b = buffer[i];
                    if (b != (byte)'\n')
                    {
                        if (discardUntilNewline) continue;
                        if (b == (byte)'\r') continue;
                        line.Add(b);
                        if (line.Count > MaxLineBytes)
                        {
                            ArduinoDiagnosticLog.Write("RX_LINE_TOO_LONG", $"TCP {Endpoint} / {MaxLineBytes} バイトを超えた行を改行まで破棄");
                            line.Clear();
                            discardUntilNewline = true;
                        }
                        continue;
                    }
                    if (discardUntilNewline)
                    {
                        discardUntilNewline = false;
                        continue;
                    }
                    string text = Encoding.UTF8.GetString(line.ToArray()).Trim();
                    line.Clear();
                    if (text.Length == 0) continue;
                    Dispatch(session, text);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            if (!session.Closed) ArduinoDiagnosticLog.Write("READ_ERROR", $"TCP {Endpoint} / {ex.Message}");
        }
        finally
        {
            CloseSession(session, notify: true);
        }
    }

    private void Dispatch(Session session, string line)
    {
        // ログ記録・画面通知より先に応答待ちを完了する(ArduinoConnection と同じ順)。
        var pending = Volatile.Read(ref _pendingReply);
        if (pending is not null && ReferenceEquals(pending.Session, session) && pending.IsReply(line))
            pending.Completion.TrySetResult(true);
        ArduinoDiagnosticLog.Write("RX", $"TCP {line}");
        if (session.Closed) return;
        try { LineReceived?.Invoke(line); }
        catch (Exception ex) { ArduinoDiagnosticLog.Write("APP_RECEIVE_HANDLER_ERROR", $"TCP {line} / {ex}"); }
    }

    // 閉じる。notify なら(まだ閉じていなかったときだけ)切断を知らせる。何度呼んでもよい。
    private void CloseSession(Session session, bool notify)
    {
        lock (_lifetimeGate)
        {
            if (ReferenceEquals(_session, session)) _session = null;
            if (session.Closed) return;
            session.Closed = true;
        }
        var pending = Volatile.Read(ref _pendingReply);
        if (pending is not null && ReferenceEquals(pending.Session, session))
            pending.Completion.TrySetException(new IOException("TCP の接続が閉じられました。"));
        try { session.Client.Dispose(); }
        catch (Exception ex) when (ex is IOException or SocketException) { }
        ArduinoDiagnosticLog.Write("CLOSE", $"TCP {Endpoint}");
        if (notify && !_disposed) Disconnected?.Invoke();
    }

    public void Dispose()
    {
        Session? session;
        lock (_lifetimeGate)
        {
            if (_disposed) return;
            _disposed = true;
            session = _session;
        }
        if (session is not null) CloseSession(session, notify: false);
    }
}
