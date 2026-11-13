using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.IO.Ports;
using PSCAutomation.Wrappers.Common;

namespace PSCAutomation.Wrappers.Arduino;

// ArduinoなどのUSBシリアル機器との基本的な通信を担当する。
// COMポートの検索・接続・文字列の送受信・切断通知を行う。
// 接続確認用の送信文字列と応答の判定条件は、呼び出し側から受け取る。
// 受信行の意味と定期応答の監視は、プロジェクト専用クラスが解釈する。
// 特定の機種・アプリの送受信文字列や操作条件は、このファイルへ記載しない。
// 元: AS110AllCheck Arduino/ArduinoConnection.cs(AS100201SwitchController 版とは Encoding の指定以外同一)。
// 変更は namespace と public 化のみ。
public sealed class ArduinoConnection : IDisposable
{
    private volatile SerialPort? _port;
    private ReaderSession? _reader;

    // 接続ごとに専用スレッドを作る。ThreadPoolやDataReceivedで読み取らない。
    private sealed class ReaderSession
    {
        internal readonly SerialPort Port;
        internal readonly System.Threading.Thread Thread;
        internal volatile bool StopRequested;

        internal ReaderSession(ArduinoConnection owner, SerialPort port)
        {
            Port = port;
            Thread = new System.Threading.Thread(() => owner.ReadLoop(this))
            {
                IsBackground = true,
                Name = "Arduino serial reader"
            };
        }
    }
    private readonly System.Threading.SemaphoreSlim _connectionGate = new(1, 1);
    private readonly object _receiveGate = new();
    private readonly object _lifetimeGate = new();
    private readonly System.Text.StringBuilder _partialLine = new();
    private volatile string _partialSnapshot = "";
    private long _lastChunk;
    private long _lastPartialReport;
    private long _readStarted;
    private long _lastReadPendingReport;
    private long _lastMonitorTick;
    private bool _discardUntilNewline;
    private readonly object _diagnosticGate = new();
    private readonly System.Threading.Timer _diagnosticTimer;
    private long _lastRx, _lastSummary;
    private volatile bool _disposed;
    private int _monitorBusy;
    private string? _lastState;
    private volatile bool _monitorEnabled;

    public ArduinoConnection()
    {
        ArduinoDiagnosticLog.Write("START", $"アプリ起動 / OS={Environment.OSVersion} / CLR={Environment.Version} / 診断版=20261015_DedicatedReader_1min");
        _diagnosticTimer = new System.Threading.Timer(MonitorCommunication, null, 500, 500);
    }

    private void RecordReceived(string line)
    {
        using var diagnosticLockObservation = PcDelayObservation.Begin("DiagnosticGate.Wait.RecordReceived");
        lock (_diagnosticGate)
        {
            diagnosticLockObservation.Dispose();
            _lastRx = Stopwatch.GetTimestamp();

        }
        ArduinoDiagnosticLog.Write("RX", line);
    }

    private string Snapshot()
    {
        var port = _port;
        string name = port?.PortName ?? "なし";
        string ports;
        try { ports = string.Join(",", SerialPort.GetPortNames()); }
        catch (Exception ex) { ports = "列挙失敗:" + ex.Message; }
        bool open = false;
        try { open = port?.IsOpen == true; } catch (ObjectDisposedException) { }
        lock (_diagnosticGate)
            return $"対象={name} / IsOpen={open} / COM一覧=[{ports}] / 最終RXから={Age(_lastRx):F3}s / 最終CHUNKから={Age(_lastChunk):F3}s / 受信途中={Escape(_partialSnapshot)}";
    }

    private static string Escape(string value) => value.Length == 0 ? "（なし）" :
        value.Replace("\r", "\\r").Replace("\n", "\\n");

    private static double Age(long timestamp) => timestamp == 0 ? double.PositiveInfinity :
        Stopwatch.GetElapsedTime(timestamp).TotalSeconds;

    private void RecordIncident(string kind, string detail)
    {
        ArduinoDiagnosticLog.Write(kind, detail + " / " + Snapshot());
    }

    private void MonitorCommunication(object? state)
    {
        if (System.Threading.Interlocked.Exchange(ref _monitorBusy, 1) != 0) return;
        try
        {
            SerialPort? port = _port;
            if (!_monitorEnabled || port is null) return;
            long tickNow = Stopwatch.GetTimestamp();
            long previousTick = _lastMonitorTick;
            _lastMonitorTick = tickNow;
            if (previousTick != 0 && Stopwatch.GetElapsedTime(previousTick, tickNow).TotalMilliseconds >= 1000)
                ArduinoDiagnosticLog.Write("MONITOR_DELAY",
                    $"500ms監視の実行間隔={Stopwatch.GetElapsedTime(previousTick, tickNow).TotalMilliseconds:F1}ms");
            string[] names = SerialPort.GetPortNames();
            bool present = Array.Exists(names, n => string.Equals(n, port.PortName, StringComparison.OrdinalIgnoreCase));
            string status;
            lock (_diagnosticGate)
                status = !present ? "COM_DISAPPEARED" : !port.IsOpen ? "PORT_CLOSED" : "PORT_OPEN";
            if (status != _lastState)
            {
                RecordIncident(status, $"{_lastState ?? "未監視"} -> {status}");
                _lastState = status;
            }
            // 受信が必要な頻度はプロトコルごとに異なるため、ここでは判定しない。
            if (_partialSnapshot.Length > 0 && Age(_lastChunk) >= 1 && Age(_lastPartialReport) >= 3)
            {
                _lastPartialReport = Stopwatch.GetTimestamp();
                RecordIncident("RX_PARTIAL_STALE", "改行未受信の文字列を保持中");
            }
            if (Age(_lastSummary) >= 5)
            {
                _lastSummary = Stopwatch.GetTimestamp();
                ArduinoDiagnosticLog.Write("STATUS", Snapshot());
            }
            long readStarted = System.Threading.Interlocked.Read(ref _readStarted);
            if (readStarted != 0 && Age(readStarted) >= 0.5 && Age(_lastReadPendingReport) >= 1)
            {
                _lastReadPendingReport = Stopwatch.GetTimestamp();
                ArduinoDiagnosticLog.Write("READ_PENDING",
                    $"到着済みバイトのRead呼出中={Stopwatch.GetElapsedTime(readStarted).TotalMilliseconds:F1}ms / " + Snapshot());
            }
        }
        catch (Exception ex) { ArduinoDiagnosticLog.Write("MONITOR_ERROR", ex.ToString()); }
        finally { System.Threading.Volatile.Write(ref _monitorBusy, 0); }
    }
    // 判定条件と待機タスクを一つの参照として公開し、別の接続の応答と混同しない。
    private sealed class PendingReply
    {
        public readonly SerialPort Port;
        public readonly Func<string, bool> IsReply;
        public readonly TaskCompletionSource<bool> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingReply(SerialPort port, Func<string, bool> isReply)
        {
            Port = port;
            IsReply = isReply;
        }
    }

    private PendingReply? _pendingReply;

    private void CompleteReply(SerialPort port, string line)
    {
        var pending = System.Threading.Volatile.Read(ref _pendingReply);
        if (pending is null || !ReferenceEquals(port, pending.Port)) return;
        if (pending.IsReply(line) && pending.Completion.TrySetResult(true))
            ArduinoDiagnosticLog.Write("HELLO_MATCH", $"確認応答を待機タスクへ通知 / {line}");
    }
    public event Action<string>? LineReceived;
    public event Action? Disconnected;
    public bool IsConnected
    {
        get { try { return !_disposed && _port?.IsOpen == true; } catch (ObjectDisposedException) { return false; } }
    }

    // いまつないでいる COM ポートの名前(つないでいなければ null)。ほかの機器(GC16 など)を探すときに、このポートを除くため(2026-11-13 追加)。
    public string? PortName
    {
        get { try { var port = _port; return !_disposed && port?.IsOpen == true ? port.PortName : null; } catch (ObjectDisposedException) { return null; } }
    }

    // 再接続方針は呼び出し側に任せる。終了とは別にポートだけ閉じる。
    public async Task DisconnectAsync()
    {
        await _connectionGate.WaitAsync().ConfigureAwait(false);
        try { DisposePort(); }
        finally { _connectionGate.Release(); }
    }

    public void SendLine(string line)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var port = _port ?? throw new InvalidOperationException("シリアル接続がありません。");
        ArduinoDiagnosticLog.Write("TX", line);
        try
        {
            using var observation = PcDelayObservation.Begin("Serial.WriteLine");
            port.WriteLine(line);
        }
        catch (Exception ex)
        {
            RecordIncident("WRITE_ERROR", ex.ToString());
            if (!_disposed) Disconnected?.Invoke();
            throw;
        }
    }

    public async Task VerifyAsync(string request, Func<string, bool> isReply, Action<string>? timingLog = null)
    {
        using (PcDelayObservation.Begin("ConnectionGate.WaitAsync"))
            await _connectionGate.WaitAsync().ConfigureAwait(false);
        try { await VerifyCoreAsync(request, isReply, timingLog).ConfigureAwait(false); }
        finally { _connectionGate.Release(); }
    }

    private async Task VerifyCoreAsync(string request, Func<string, bool> isReply, Action<string>? timingLog)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        ArgumentNullException.ThrowIfNull(isReply);
        if (!IsConnected)
        {
            await ConnectCoreAsync(request, isReply, timingLog).ConfigureAwait(false);
            return;
        }
        var pending = new PendingReply(_port!, isReply);
        System.Threading.Volatile.Write(ref _pendingReply, pending);
        try
        {
            SendLine(request);
            await pending.Completion.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordIncident("VERIFY_ERROR", ex.ToString());
            DisposePort();
            throw;
        }
        finally { System.Threading.Volatile.Write(ref _pendingReply, null); }
    }

    public async Task ConnectAsync(string request, Func<string, bool> isReply, Action<string>? timingLog = null)
    {
        using (PcDelayObservation.Begin("ConnectionGate.WaitAsync"))
            await _connectionGate.WaitAsync().ConfigureAwait(false);
        try { await ConnectCoreAsync(request, isReply, timingLog).ConfigureAwait(false); }
        finally { _connectionGate.Release(); }
    }

    private async Task ConnectCoreAsync(string request, Func<string, bool> isReply, Action<string>? timingLog)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        ArgumentNullException.ThrowIfNull(isReply);
        if (IsConnected) return;
        DisposePort();
        if (_reader?.Thread.IsAlive == true)
            throw new InvalidOperationException("前のシリアル受信処理がまだ終了していません。少し待ってから接続を確認してください。");

        var failures = new List<string>();
        var stepTimer = Stopwatch.StartNew();
        string[] names = SerialPort.GetPortNames();
        ArduinoDiagnosticLog.Write("CONNECT_SCAN", string.Join(",", names));
        timingLog?.Invoke($"Arduino内訳：ポート列挙 = {stepTimer.Elapsed.TotalSeconds:F3} 秒 / {string.Join(", ", names)}");
        if (names.Length == 0)
        {
            RecordIncident("CONNECT_FAILED", "COM ポートが見つかりません。");
            throw new InvalidOperationException("COM ポートが見つかりません。USB 接続を確認してください。");
        }

        foreach (string name in names)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SerialPort? candidate = null;
            try
            {
                candidate = new SerialPort(name, 115200)
                {
                    NewLine = "\n",
                    Encoding = new System.Text.UTF8Encoding(false),
                    ReadTimeout = 100,
                    WriteTimeout = 1000,
                    // USBシリアルに必要なDTRを有効にする。
                    DtrEnable = true
                };
                lock (_receiveGate)
                {
                    _partialLine.Clear();
                    _partialSnapshot = "";
                    _discardUntilNewline = false;
                }
                _lastChunk = _lastPartialReport = 0;
                _lastMonitorTick = 0;
                candidate.ErrorReceived += ErrorReceived;
                stepTimer.Restart();
                ArduinoDiagnosticLog.Write("OPEN", name + " / 115200bps / DTR=true");
                candidate.Open();
                timingLog?.Invoke($"Arduino内訳：{name} ポートを開く = {stepTimer.Elapsed.TotalSeconds:F3} 秒");
                lock (_lifetimeGate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    _port = candidate;
                }
                lock (_diagnosticGate)
                {
                    _lastRx = 0;
                }
                _lastState = null;
                StartReader(candidate);
                // ポートを開くとリセットされるボードがあるため、起動を待つ。
                stepTimer.Restart();
                await Task.Delay(1700).ConfigureAwait(false);
                ObjectDisposedException.ThrowIf(_disposed, this);
                timingLog?.Invoke($"Arduino内訳：{name} 起動待ち（設定1.7秒） = {stepTimer.Elapsed.TotalSeconds:F3} 秒");
                stepTimer.Restart();
                var pending = new PendingReply(candidate, isReply);
                System.Threading.Volatile.Write(ref _pendingReply, pending);
                SendLine(request);
                await pending.Completion.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                timingLog?.Invoke($"Arduino内訳：{name} 応答待ち = {stepTimer.Elapsed.TotalSeconds:F3} 秒");
                System.Threading.Volatile.Write(ref _pendingReply, null);
                _monitorEnabled = true;
                RecordIncident("CONNECTED", "確認応答を受信");
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or
                UnauthorizedAccessException or TimeoutException or System.Security.SecurityException)
            {
                timingLog?.Invoke($"Arduino内訳：{name} 確認失敗 / {ex.Message}");
                RecordIncident("CONNECT_ERROR", ex.ToString());
                failures.Add($"{name}: {ex.GetType().Name} — {ex.Message}");
                if (ReferenceEquals(candidate, _port))
                {
                    DisposePort();
                }
                else if (candidate is not null)
                {
                    candidate.ErrorReceived -= ErrorReceived;
                    candidate.Dispose();
                }
                System.Threading.Volatile.Write(ref _pendingReply, null);
            }
        }
        throw new InvalidOperationException(
            "Arduino を確認できません。\n" + string.Join("\n", failures));
    }

    private void StartReader(SerialPort port)
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var reader = new ReaderSession(this, port);
            _reader = reader;
            reader.Thread.Start();
        }
    }

    private void ReadLoop(ReaderSession reader)
    {
        byte[] buffer = new byte[256];
        ArduinoDiagnosticLog.Write("READER_START",
            $"専用スレッド開始 / port={reader.Port.PortName} / thread={Environment.CurrentManagedThreadId} / idlePoll=5ms");
        try
        {
            while (!reader.StopRequested && ReferenceEquals(reader.Port, _port))
            {
                ReadAvailableData(reader.Port, buffer);
                // 待機は専用スレッドだけで行い、ThreadPoolを使用しない。
                // 1回につき最大256バイトを処理し、終了要求を毎回確認する。
                System.Threading.Thread.Sleep(5);
            }
        }
        catch (Exception ex)
        {
            if (!reader.StopRequested && ReferenceEquals(reader.Port, _port))
            {
                RecordIncident("READER_ERROR", ex.ToString());
                if (!_disposed) Disconnected?.Invoke();
            }
        }
        finally
        {
            ArduinoDiagnosticLog.Write("READER_END",
                $"専用スレッド終了 / port={reader.Port.PortName} / thread={Environment.CurrentManagedThreadId}");
        }
    }

    private void ReadAvailableData(SerialPort port, byte[] buffer)
    {
        using var callbackObservation = PcDelayObservation.Begin("RX.ReadBatch");
        try
        {
            // ASCIIプロトコル。到着済みのバイトだけ読み、改行や次のデータを待たない。
            long lockStarted = Stopwatch.GetTimestamp();
            using var receiveLockObservation = PcDelayObservation.Begin("ReceiveGate.Wait");
            lock (_receiveGate)
            {
                receiveLockObservation.Dispose();
                double lockMs = Stopwatch.GetElapsedTime(lockStarted).TotalMilliseconds;
                if (lockMs >= 100)
                    ArduinoDiagnosticLog.Write("RX_LOCK_SLOW", $"受信ロック待ち={lockMs:F1}ms");
                if (!ReferenceEquals(port, _port) || !port.IsOpen) return;
                if (port.IsOpen)
                {
                    int availableBefore;
                    using (PcDelayObservation.Begin("Serial.BytesToRead"))
                        availableBefore = port.BytesToRead;
                    if (availableBefore <= 0) return;
                    long readStarted = Stopwatch.GetTimestamp();
                    System.Threading.Interlocked.Exchange(ref _readStarted, readStarted);
                    string chunk;
                    try
                    {
                        int count;
                        using (PcDelayObservation.Begin("Serial.Read"))
                            count = port.Read(buffer, 0, Math.Min(availableBefore, buffer.Length));
                        using (PcDelayObservation.Begin("RX.ASCII.Decode"))
                            chunk = System.Text.Encoding.ASCII.GetString(buffer, 0, count);
                    }
                    catch (TimeoutException)
                    {
                        // 読めなかった場合は専用スレッドの次の確認で再試行する。
                        RecordIncident("READ_TIMEOUT", $"到着済みバイトの読取が100ms以内に完了せず / availableBefore={availableBefore}");
                        return;
                    }
                    finally { System.Threading.Interlocked.Exchange(ref _readStarted, 0); }
                    double readMs = Stopwatch.GetElapsedTime(readStarted).TotalMilliseconds;
                    if (chunk.Length == 0) return;
                    _lastChunk = Stopwatch.GetTimestamp();
                    ArduinoDiagnosticLog.Write("RX_CHUNK",
                        $"chars={chunk.Length} / availableBefore={availableBefore} / readMs={readMs:F1} / " +
                        Escape(chunk.Length <= 512 ? chunk : chunk[..512] + "（以降省略）"));
                    if (readMs >= 100)
                        ArduinoDiagnosticLog.Write("READ_SLOW", $"到着済みバイトのRead所要={readMs:F1}ms / availableBefore={availableBefore} / chars={chunk.Length}");
                    using var assemblyObservation = PcDelayObservation.Begin("RX.AssembleAndDispatch");
                    foreach (char ch in chunk)
                    {
                        if (_discardUntilNewline)
                        {
                            if (ch == '\n') _discardUntilNewline = false;
                            continue;
                        }
                        if (ch != '\n')
                        {
                            _partialLine.Append(ch);
                            _partialSnapshot = _partialLine.ToString();
                            if (_partialLine.Length > 1024)
                            {
                                RecordIncident("RX_LINE_TOO_LONG", "1024文字を超えた行を改行まで破棄");
                                _partialLine.Clear();
                                _partialSnapshot = "";
                                _discardUntilNewline = true;
                            }
                            continue;
                        }
                        string line;
                        using (PcDelayObservation.Begin("RX.FinishLine"))
                            line = _partialLine.ToString().Trim();
                        _partialLine.Clear();
                        _partialSnapshot = "";
                        if (line.Length == 0) continue;
                        long lineStarted = Stopwatch.GetTimestamp();
                        // ログ記録・画面通知より先に応答待ちを完了する。
                        using (PcDelayObservation.Begin("RX.CompleteReply"))
                            CompleteReply(port, line);
                        using (PcDelayObservation.Begin("RX.RecordReceived"))
                            RecordReceived(line);
                        long handlerStarted = Stopwatch.GetTimestamp();
                        try
                        {
                            using var observation = PcDelayObservation.Begin("RX.LineReceived.Invoke");
                            LineReceived?.Invoke(line);
                        }
                        catch (Exception ex)
                        {
                            RecordIncident("APP_RECEIVE_HANDLER_ERROR", ex.ToString());
                            throw;
                        }
                        double handlerMs = Stopwatch.GetElapsedTime(handlerStarted).TotalMilliseconds;
                        if (handlerMs >= 100)
                            RecordIncident("RECEIVE_HANDLER_SLOW", $"受信後のアプリ処理={handlerMs:F1}ms / line={line}");
                        double lineMs = Stopwatch.GetElapsedTime(lineStarted).TotalMilliseconds;
                        if (lineMs >= 100 && handlerMs < 100)
                            RecordIncident("RECEIVE_HANDLER_SLOW", $"ログ記録を含む1行処理={lineMs:F1}ms / handler={handlerMs:F1}ms / line={line}");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // 旧ポートの終了と競合した通知は現在の接続を切断扱いにしない。
            if (!ReferenceEquals(port, _port)) return;
            RecordIncident("READ_ERROR", ex.ToString());
            // 同じ異常を5msごとに通知し続けない。終了はReadLoopで処理する。
            throw;
        }
    }

    private void ErrorReceived(object? sender, SerialErrorReceivedEventArgs e)
    {
        if (sender is not SerialPort port || !ReferenceEquals(port, _port)) return;
        RecordIncident("SERIAL_ERROR", $"ErrorReceived={e.EventType} / 従来の切断通知を発行");
        Disconnected?.Invoke();
    }

    private void DisposePort()
    {
        _monitorEnabled = false;
        ArduinoDiagnosticLog.Write("CLOSE", Snapshot());
        SerialPort? port;
        ReaderSession? reader;
        lock (_lifetimeGate)
        {
            port = _port;
            _port = null;
            reader = _reader;
            if (reader is not null) reader.StopRequested = true;
        }
        var pending = System.Threading.Interlocked.Exchange(ref _pendingReply, null);
        pending?.Completion.TrySetException(new ObjectDisposedException(nameof(ArduinoConnection)));
        if (port is not null)
        {
            port.ErrorReceived -= ErrorReceived;
            port.Dispose();
        }
        if (reader is not null && reader.Thread != System.Threading.Thread.CurrentThread &&
            reader.Thread.IsAlive &&
            !reader.Thread.Join(2000))
        {
            ArduinoDiagnosticLog.Write("READER_STOP_SLOW", "専用受信スレッドが2秒以内に終了しませんでした。");
            // 終了するまで参照を保持し、新しい受信スレッドとの重複を防ぐ。
            return;
        }
        _reader = null;
    }

    public void Dispose()
    {
        lock (_lifetimeGate) _disposed = true;
        _diagnosticTimer.Dispose();
        DisposePort();
        ArduinoDiagnosticLog.Write("END", "Arduino通信を終了");
        ArduinoDiagnosticLog.FlushOnExit();
    }
}
