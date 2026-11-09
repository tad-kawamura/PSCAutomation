using System.Diagnostics;

namespace PSCAutomation.Apps.AS100201SwitchController;

// AS100-201専用の通信規約。ピン番号はArduinoスケッチで管理する。
// 通信部品にはボタン名・READY LED・定期応答の意味を持ち込まない。
internal sealed class As100201ArduinoProtocol : IDisposable
{
    private readonly ArduinoConnection _connection = new();
    private readonly SemaphoreSlim _maintenanceGate = new(1, 1);
    private readonly object _gate = new();
    private long _lastFrameAt, _stableSince, _version, _nextAttempt;
    private uint? _lastSequence, _lastBoardMillis;
    private bool _verified, _staleLogged;
    private volatile bool _disposed;
    private static readonly TimeSpan FreshLimit = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RecoveryTime = TimeSpan.FromSeconds(1);
    private const string HelloRequest = "AS100201_PING";
    // 版 2(2026-11-09): PC_go / CR_go の 2 回押しをスケッチで判定する。版 1 のスケッチは接続エラーにする。
    public const string HelloPrefix = "AS100201_READY:";
    public const int SketchVersion = 2;
    private const string DoubleWaitCommand = "DOUBLE_WAIT_MS:";
    private const string DoubleWaitReply = "DOUBLE_WAIT_MS_OK:";
    private string? _helloReply;
    private int _doubleWaitMilliseconds = As100201WaveformSettings.DoubleWaitMilliseconds;

    public event Action<As100201Switch>? SwitchPressed;
    // Arduino が受け付けた 2 回押しの待ち時間(ミリ秒)。
    public event Action<int>? DoubleWaitApplied;
    public event Action? Disconnected;
    public event Action<string>? StatusChanged;

    public As100201ArduinoProtocol()
    {
        _connection.LineReceived += HandleLine;
        _connection.Disconnected += HandleDisconnected;
    }

    public bool IsConnected { get { lock (_gate) return _verified && _connection.IsConnected; } }
    public long RequestVersion { get { lock (_gate) return _version; } }
    public bool InputReady
    {
        get
        {
            lock (_gate) return !_disposed && _verified && _connection.IsConnected &&
                _lastFrameAt != 0 && Stopwatch.GetElapsedTime(_lastFrameAt) < FreshLimit &&
                _stableSince != 0 && Stopwatch.GetElapsedTime(_stableSince) >= RecoveryTime;
        }
    }

    // 初期化時の確認。ポート操作と読取スレッドの終了待ちはUIを占有しない。
    public async Task VerifyAsync(Action<string>? timingLog = null)
    {
        await _maintenanceGate.WaitAsync().ConfigureAwait(false);
        try { await Task.Run(() => VerifyCoreAsync(timingLog)).ConfigureAwait(false); }
        finally { _maintenanceGate.Release(); }
    }

    private async Task VerifyCoreAsync(Action<string>? timingLog = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_connection.IsConnected) Reset();
        Volatile.Write(ref _helloReply, null);
        await _connection.VerifyAsync(HelloRequest, line =>
        {
            if (!line.StartsWith(HelloPrefix, StringComparison.Ordinal)) return false;
            Volatile.Write(ref _helloReply, line);
            return true;
        }, timingLog).ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (CheckHello(Volatile.Read(ref _helloReply)) is string versionError)
        {
            // 古いスケッチは 2 回押しを判定せず、1 回押しの送り方も違うので使わない。
            await _connection.DisconnectAsync().ConfigureAwait(false);
            ArduinoDiagnosticLog.Write("SKETCH_VERSION", versionError);
            throw new As100201SketchVersionException(versionError);
        }
        lock (_gate) _verified = true;
        // READY_OFFは新しい接続を含め、準備確認が終わるまで点灯させない。
        _connection.SendLine("READY_OFF");
        _connection.SendLine(DoubleWaitCommand + Volatile.Read(ref _doubleWaitMilliseconds));
    }

    // 確認応答の版を見る。版 2 なら null、それ以外は人に見せる理由。
    public static string? CheckHello(string? reply)
    {
        string version = reply is not null && reply.StartsWith(HelloPrefix, StringComparison.Ordinal)
            ? reply[HelloPrefix.Length..] : "不明";
        return version == SketchVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) ? null :
            $"Arduino のスケッチが古い（版 {version}、このアプリは版 {SketchVersion}）。" +
            "firmware/ArduinoSwitchAndReadyLed を書き込み直してください。";
    }

    // 2 回押しの待ち時間を Arduino へ送る。未接続なら、次の接続確認の後に送る。
    public void SetDoubleWait(int milliseconds)
    {
        if (milliseconds is < As100201SwitchSettings.MinDoubleWaitMilliseconds or > As100201SwitchSettings.MaxDoubleWaitMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        Volatile.Write(ref _doubleWaitMilliseconds, milliseconds);
        if (IsConnected) _connection.SendLine(DoubleWaitCommand + milliseconds);
    }

    // 受信が一時停止しただけなら同じポートでの受信再開を待つ。
    // 3秒以上応答がない場合や実際の切断では、ポートを開き直して接続確認する。
    // LOGGERとWF1973には触れず、以前のボタン要求も再実行しない。
    public async Task MaintainAsync()
    {
        if (_disposed || !await _maintenanceGate.WaitAsync(0).ConfigureAwait(false)) return;
        bool attempted = false;
        try
        {
            if (InputReady)
            {
                ArduinoDiagnosticLog.CompleteIfActive("AS100-201の定期応答が安定しました。");
                return;
            }
            bool connected = IsConnected;
            double age;
            bool report = false;
            lock (_gate)
            {
                age = _lastFrameAt == 0 ? double.PositiveInfinity : Stopwatch.GetElapsedTime(_lastFrameAt).TotalSeconds;
                if (connected && age >= 1 && !_staleLogged)
                {
                    _staleLogged = true;
                    _stableSince = 0;
                    _version++;
                    report = true;
                }
            }
            if (report)
            {
                ArduinoDiagnosticLog.Write("HEARTBEAT_STALE", "定期応答が1秒以上停止。操作を禁止します。");
                StatusChanged?.Invoke("Arduinoの定期応答が停止。受信再開を待っています。");
            }
            if (connected && age < 3) return;
            if (Environment.TickCount64 < _nextAttempt) return;
            attempted = true;
            _nextAttempt = Environment.TickCount64 + 2000;
            await Task.Run(async () =>
            {
                Reset();
                await _connection.DisconnectAsync().ConfigureAwait(false);
                if (_disposed) return;
                await VerifyCoreAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
            if (!_disposed) StatusChanged?.Invoke("Arduinoを再接続しました。定期応答の安定を確認しています。");
        }
        catch (Exception ex)
        {
            Reset();
            if (!_disposed)
            {
                ArduinoDiagnosticLog.Write("RECONNECT_WAIT", ex.Message);
                StatusChanged?.Invoke(ex is As100201SketchVersionException
                    ? ex.Message
                    : "Arduinoの接続を待っています。USB接続後に自動で再確認します。");
            }
        }
        finally
        {
            if (attempted) _nextAttempt = Environment.TickCount64 + 2000;
            _maintenanceGate.Release();
        }
    }

    public void SetReadyLed(bool on)
    {
        if (on && !InputReady) return;
        if (_connection.IsConnected) _connection.SendLine(on ? "READY_ON" : "READY_OFF");
    }

    private void HandleLine(string line)
    {
        if (_disposed) return;
        if (line == "DIAG_BOOT:1")
        {
            // 接続の確認応答とは別。ボード再起動後の古い要求を無効にする。
            Reset();
            ArduinoDiagnosticLog.Write("ARDUINO_BOOT", "AS100-201の起動通知");
        }
        else if (line.StartsWith("DIAG_FRAME:"))
        {
            string[] parts = line.Split(':');
            if (parts.Length != 3 || !uint.TryParse(parts[1], out uint sequence) ||
                !uint.TryParse(parts[2], out uint boardMillis)) return;
            long now = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                bool gap = _lastFrameAt == 0 || Stopwatch.GetElapsedTime(_lastFrameAt, now) >= FreshLimit;
                if (_lastSequence is uint previous && _lastBoardMillis is uint previousMillis)
                {
                    uint step = unchecked(sequence - previous);
                    uint boardDelta = unchecked(boardMillis - previousMillis);
                    double pcDelta = Stopwatch.GetElapsedTime(_lastFrameAt, now).TotalMilliseconds;
                    if (step != 1 || boardDelta >= 1000 || pcDelta >= 1000)
                    {
                        ArduinoDiagnosticLog.Write("FRAME_GAP", $"seq={previous}->{sequence} / Arduino経過={boardDelta}ms / PC受信間隔={pcDelta:F1}ms");
                        gap = true;
                    }
                }
                if (gap || _stableSince == 0)
                {
                    _stableSince = now;
                    _version++;
                    ArduinoDiagnosticLog.Write(_lastFrameAt == 0 ? "PROTOCOL_WARMUP" : "PROTOCOL_RECOVERY", "定期応答の継続を1秒間確認。古い操作要求は破棄します。");
                }
                _lastFrameAt = now;
                _lastSequence = sequence;
                _lastBoardMillis = boardMillis;
                _staleLogged = false;
            }
        }
        else if (line.StartsWith(DoubleWaitReply, StringComparison.Ordinal))
        {
            if (int.TryParse(line.AsSpan(DoubleWaitReply.Length), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int applied))
                DoubleWaitApplied?.Invoke(applied);
        }
        else if (As100201Switch.TryParse(line, out var pressed))
        {
            if (InputReady) SwitchPressed?.Invoke(pressed);
            else ArduinoDiagnosticLog.Write("SWITCH_DROPPED", "復旧確認中の操作を破棄 / " + line);
        }
    }

    private void Reset()
    {
        lock (_gate)
        {
            _verified = false;
            _lastFrameAt = _stableSince = 0;
            _lastSequence = _lastBoardMillis = null;
            _staleLogged = false;
            _version++;
        }
    }

    private void HandleDisconnected()
    {
        Reset();
        if (!_disposed) Disconnected?.Invoke();
    }

    public void Dispose()
    {
        _disposed = true;
        Reset();
        _connection.LineReceived -= HandleLine;
        _connection.Disconnected -= HandleDisconnected;
        _connection.Dispose();
    }
}

// Arduino のスケッチの版がアプリと合わない(書き込み直しが要る)。
internal sealed class As100201SketchVersionException(string message) : InvalidOperationException(message);
