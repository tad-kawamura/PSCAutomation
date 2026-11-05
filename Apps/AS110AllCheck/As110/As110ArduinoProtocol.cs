using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace PSCAutomation.Apps.AS110AllCheck;

// AS110専用のArduino通信内容を解釈する。
// 接続確認の要求・応答と、測定要求・測定禁止・NF弁状態を扱う。
// NF弁状態の受信時刻を管理し、MainFormへ各通知を渡す。
// COMポートの操作や文字列の送受信はArduinoConnectionへ依頼する。
// Arduino側の送受信文字列を変更するときは、このファイルを修正する。
internal sealed class As110ArduinoProtocol : IDisposable
{
    private readonly ArduinoConnection _connection = new();
    private uint? _lastUptime;
    private uint? _lastFrame;
    private uint? _lastFrameMillis;
    private long _lastFrameReceived;
    private readonly object _statusGate = new();
    private readonly System.Threading.Timer _communicationMonitor;
    private long _lastReceiveTimestamp;
    private string? _lastCommunicationState;
    private bool _disposed;
    private long _lastNfTimestamp, _recoveryStarted, _connectedTimestamp;
    private bool _recovering;
    private bool _nfEnergized;
    private DateTime _lastNfStatusUtc;
    private long _requestVersion;
    private static readonly TimeSpan FreshLimit = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AlertLimit = TimeSpan.FromSeconds(3);
    private const string HelloRequest = "AS110_PING";
    private const string HelloResponse = "AS110_READY:1";
    public event Action? MeasurementRequested;
    public event Action<int,long,string>? ButtonRequested;
    public void SendDisplay(string line) => _connection.SendLine(line);
    public event Action? MeasurementBlocked;
    public event Action? NfStatusChanged;
    public event Action? Disconnected;
    public bool IsConnected => _connection.IsConnected;
    public bool NfEnergized { get { lock (_statusGate) return _nfEnergized; } }
    public DateTime LastNfStatusUtc { get { lock (_statusGate) return _lastNfStatusUtc; } }
    // 警告猶予を延ばしても、測定許可には1秒以内の通電情報を要求する。
    public bool NfStatusFresh { get { lock (_statusGate) return IsConnected && IsFresh(); } }
    public bool NfCommunicationAlertDue
    {
        get { lock (_statusGate) return IsConnected &&
            (_lastNfTimestamp == 0 ? _connectedTimestamp != 0 &&
             Stopwatch.GetElapsedTime(_connectedTimestamp) >= AlertLimit :
             Stopwatch.GetElapsedTime(_lastNfTimestamp) >= AlertLimit); }
    }
    public bool MeasurementInputReady
    {
        get { lock (_statusGate) return IsConnected && IsFresh() &&
            (!_recovering || Stopwatch.GetElapsedTime(_recoveryStarted) >= FreshLimit); }
    }
    public long MeasurementRequestVersion { get { lock (_statusGate) return _requestVersion; } }
    private bool IsFresh() => _lastNfTimestamp != 0 &&
        Stopwatch.GetElapsedTime(_lastNfTimestamp) < FreshLimit;

    public As110ArduinoProtocol()
    {
        _connection.LineReceived += HandleLine;
        _connection.Disconnected += HandleDisconnected;
        _communicationMonitor = new System.Threading.Timer(MonitorCommunication, null, 500, 500);
    }

    public async Task VerifyAsync(Action<string>? timingLog = null)
    {
        if (!IsConnected) ResetNfStatus();
        try
        {
            await _connection.VerifyAsync(HelloRequest, IsHelloReply, timingLog);
            lock (_statusGate) if (_connectedTimestamp == 0) _connectedTimestamp = Stopwatch.GetTimestamp();
        }
        catch { ResetNfStatus(); throw; }
    }

    public async Task ConnectAsync(Action<string>? timingLog = null)
    {
        if (!IsConnected) ResetNfStatus();
        try
        {
            await _connection.ConnectAsync(HelloRequest, IsHelloReply, timingLog);
            lock (_statusGate) if (_connectedTimestamp == 0) _connectedTimestamp = Stopwatch.GetTimestamp();
        }
        catch { ResetNfStatus(); throw; }
    }

    private static bool IsHelloReply(string line) => line == HelloResponse;

    // 受信の有無とNF定期通知の意味はAS110側で判断する。
    // 通信クラス側にはNF文字列やAS110の監視時間を持たせない。
    private void MonitorCommunication(object? state)
    {
        // Disposeと監視が重ならないよう、状態の確認とログ記録を同じロック内で行う。
        lock (_statusGate)
        {
            if (_disposed || _connectedTimestamp == 0 || !IsConnected) return;
            string status = Age(_lastReceiveTimestamp) >= 1 ? "RX_STALE" :
                Age(_lastNfTimestamp) >= 1 ? "NF_STALE" :
                _recovering && Age(_recoveryStarted) < 1 ? "RX_RECOVERING" : "RECEIVING";
            string detail = $"AS110定期受信監視 / 最終RXから={Age(_lastReceiveTimestamp):F3}s / 最終NFから={Age(_lastNfTimestamp):F3}s";
            if (status != _lastCommunicationState)
            {
                ArduinoDiagnosticLog.Write(status, $"{_lastCommunicationState ?? "未監視"} -> {status} / {detail}");
                _lastCommunicationState = status;
            }
            else if (status == "RECEIVING")
            {
                ArduinoDiagnosticLog.CompleteIfActive("正常受信を確認 / " + detail);
            }
        }
    }

    private static double Age(long timestamp) => timestamp == 0 ? double.PositiveInfinity :
        Stopwatch.GetElapsedTime(timestamp).TotalSeconds;

    private void HandleLine(string line)
    {
        lock (_statusGate)
        {
            if (_disposed) return;
            _lastReceiveTimestamp = Stopwatch.GetTimestamp();
        }
        if (line.StartsWith("DIAG_FRAME:"))
        {
            string[] parts = line.Split(':');
            if (parts.Length == 3 && uint.TryParse(parts[1], out uint sequence) &&
                uint.TryParse(parts[2], out uint boardMillis))
            {
                long now = Stopwatch.GetTimestamp();
                if (_lastFrame is uint previous && _lastFrameMillis is uint previousMillis)
                {
                    uint step = unchecked(sequence - previous);
                    uint boardDelta = unchecked(boardMillis - previousMillis);
                    double pcDelta = Stopwatch.GetElapsedTime(_lastFrameReceived, now).TotalMilliseconds;
                    if (step != 1 || pcDelta >= 1000 || boardDelta >= 1000)
                        ArduinoDiagnosticLog.Write("FRAME_GAP",
                            $"seq={previous}->{sequence} / step={step} / Arduino経過={boardDelta}ms / PC受信間隔={pcDelta:F1}ms");
                    else
                        ArduinoDiagnosticLog.Write("FRAME_TIMING", $"seq={sequence} / Arduino経過={boardDelta}ms / PC受信間隔={pcDelta:F1}ms");
                }
                _lastFrame = sequence;
                _lastFrameMillis = boardMillis;
                _lastFrameReceived = now;
            }
        }
        else if (line.StartsWith("DIAG_CMD_"))
            ArduinoDiagnosticLog.Write("ARDUINO_COMMAND", line);
        else if (line == "DIAG_BOOT:1")
        {
            ArduinoDiagnosticLog.Write("ARDUINO_BOOT", "起動通知を受信（接続確認でポートを開いた直後は通常起動の可能性もあります）");
            _lastUptime = null;
            _lastFrame = _lastFrameMillis = null;
        }
        else if (line.StartsWith("DIAG_UPTIME:") && uint.TryParse(line.AsSpan(12), out uint uptime))
        {
            if (_lastUptime is uint previous && uptime < previous)
            {
                bool wrap = previous > 0xFFFF0000 && uptime < 60000;
                ArduinoDiagnosticLog.Write(wrap ? "UPTIME_WRAP" : "ARDUINO_RESTART",
                    $"稼働時間が {previous}ms -> {uptime}ms / 電源・リセット原因はこの情報だけでは特定できません");
            }
            _lastUptime = uptime;
        }
        else if (line is "NF:1" or "NF:0")
        {
            lock (_statusGate)
            {
                if (_lastNfTimestamp != 0 && !IsFresh())
                {
                    _recovering = true;
                    _recoveryStarted = Stopwatch.GetTimestamp();
                    _requestVersion++;
                    ArduinoDiagnosticLog.Write("NF_RECOVERY_START",
                        $"NF受信間隔={Stopwatch.GetElapsedTime(_lastNfTimestamp).TotalSeconds:F3}s / 開始要求を破棄し1秒間の継続受信を確認");
                }
                else if (_recovering && Stopwatch.GetElapsedTime(_recoveryStarted) >= FreshLimit)
                {
                    _recovering = false;
                    ArduinoDiagnosticLog.Write("NF_RECOVERY_READY", "継続受信を確認。新しい測定操作を受け付けます");
                }
                _nfEnergized = line == "NF:1";
                _lastNfStatusUtc = DateTime.UtcNow;
                _lastNfTimestamp = Stopwatch.GetTimestamp();
            }
            NfStatusChanged?.Invoke();
        }
        else if (line.StartsWith("BUTTON:"))
        {
            string[] parts=line.Split(':');
            if(parts.Length==4 && int.TryParse(parts[1],out int button) && button is >=1 and <=3 && long.TryParse(parts[2],out long epoch))
                ButtonRequested?.Invoke(button,epoch,parts[3]);
        }
        else if (line == "MEASURE_BLOCKED") MeasurementBlocked?.Invoke();
        else if (line == "MEASURE")
        {
            if (!MeasurementInputReady)
            {
                ArduinoDiagnosticLog.Write("MEASURE_DROPPED", "受信停止中または復旧確認中の測定要求を破棄。再操作が必要です");
                return;
            }
            MeasurementRequested?.Invoke();
        }
    }

    private void ResetNfStatus()
    {
        lock (_statusGate)
        {
            _nfEnergized = false;
            _lastNfStatusUtc = default;
            _lastNfTimestamp = _recoveryStarted = _connectedTimestamp = 0;
            _lastReceiveTimestamp = 0;
            _lastCommunicationState = null;
            _recovering = false;
            _requestVersion++;
        }
        _lastUptime = _lastFrame = _lastFrameMillis = null;
    }

    private void HandleDisconnected()
    {
        ResetNfStatus();
        Disconnected?.Invoke();
    }

    public void Dispose()
    {
        lock (_statusGate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _communicationMonitor.Dispose();
        _connection.LineReceived -= HandleLine;
        _connection.Disconnected -= HandleDisconnected;
        _connection.Dispose();
        ResetNfStatus();
    }
}
