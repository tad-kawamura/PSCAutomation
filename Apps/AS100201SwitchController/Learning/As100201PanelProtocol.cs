using System.Diagnostics;

namespace PSCAutomation.Apps.AS100201SwitchController;

// タッチパネル(M5Stack Tab5)との取り決め。Arduino の As100201ArduinoProtocol と同じ作り(SPEC_調整操作の記録.md「異常時」):
// 250ms ごとの定期応答 DIAG_FRAME を見張り、1 秒止まれば操作禁止、3 秒止まる・切断で 2 秒おきに再接続、
// 復旧後 1 秒は操作を捨てる。確認文字列は Arduino とぶつからない専用のもの(COM ポートを総当たりで探すため)。
// 通信部品にはボタンの意味を持ち込まず、ここで解釈する。通信は IAs100201SerialLink 経由なので模擬に差し替えられる。
internal sealed class As100201PanelProtocol : IDisposable
{
    private readonly IAs100201SerialLink _link;
    private readonly SemaphoreSlim _maintenanceGate = new(1, 1);
    private readonly object _gate = new();
    private long _lastFrameAt, _stableSince, _version, _nextAttempt;
    private uint? _lastSequence, _lastBoardMillis;
    private bool _verified, _staleLogged;
    private volatile bool _disposed;
    private static readonly TimeSpan FreshLimit = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RecoveryTime = TimeSpan.FromSeconds(1);
    public const string HelloRequest = "AS100201_PANEL_PING";
    public const string HelloPrefix = "AS100201_PANEL_READY:";
    // 版 2(2026-11-13): USB と Wi-Fi の両方でつながり、電池残量 BATT: を送る。版 1 の行はそのまま。
    // 版 3(2026-11-13): ノズルは方向だけ、スペーサーは厚さと表示との差を付ける、パネルに触れたら HOLD を送る。
    public const int PanelVersion = 3;
    private string? _helloReply;

    public event Action<As100201PanelCommand>? CommandReceived;
    public event Action? Disconnected;
    public event Action<string>? StatusChanged;
    public event Action<As100201PanelBattery>? BatteryReported;

    public As100201PanelProtocol(IAs100201SerialLink link)
    {
        _link = link;
        _link.LineReceived += HandleLine;
        _link.Disconnected += HandleDisconnected;
    }

    public bool IsConnected { get { lock (_gate) return _verified && _link.IsConnected; } }
    public long RequestVersion { get { lock (_gate) return _version; } }
    public bool InputReady
    {
        get
        {
            lock (_gate) return !_disposed && _verified && _link.IsConnected &&
                _lastFrameAt != 0 && Stopwatch.GetElapsedTime(_lastFrameAt) < FreshLimit &&
                _stableSince != 0 && Stopwatch.GetElapsedTime(_stableSince) >= RecoveryTime;
        }
    }

    // 初期化時の確認。Arduino の確認と同時には走らせない(両方が同じ COM ポートを開こうとしないよう、呼ぶ側で順に行う)。
    public async Task VerifyAsync(Action<string>? timingLog = null)
    {
        await _maintenanceGate.WaitAsync().ConfigureAwait(false);
        try { await Task.Run(() => VerifyCoreAsync(timingLog)).ConfigureAwait(false); }
        finally { _maintenanceGate.Release(); }
    }

    private async Task VerifyCoreAsync(Action<string>? timingLog = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_link.IsConnected) Reset();
        Volatile.Write(ref _helloReply, null);
        await _link.VerifyAsync(HelloRequest, line =>
        {
            if (!line.StartsWith(HelloPrefix, StringComparison.Ordinal)) return false;
            Volatile.Write(ref _helloReply, line);
            return true;
        }, timingLog).ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (CheckHello(Volatile.Read(ref _helloReply)) is string versionError)
        {
            await _link.DisconnectAsync().ConfigureAwait(false);
            ArduinoDiagnosticLog.Write("PANEL_VERSION", versionError);
            throw new As100201PanelVersionException(versionError);
        }
        lock (_gate) _verified = true;
    }

    // 確認応答の版を見る。今の版なら null、それ以外は人に見せる理由。
    public static string? CheckHello(string? reply)
    {
        string version = reply is not null && reply.StartsWith(HelloPrefix, StringComparison.Ordinal)
            ? reply[HelloPrefix.Length..] : "不明";
        return version == PanelVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) ? null :
            $"タッチパネルのスケッチの版が合いません（版 {version}、このアプリは版 {PanelVersion}）。" +
            "firmware のタッチパネル用スケッチを書き込み直してください。";
    }

    // 受信が一時停止しただけなら同じポートでの受信再開を待つ。3 秒以上応答がない・切断ではポートを開き直して確認する。
    // LOGGER・WF1973・Arduino には触れない。
    public async Task MaintainAsync()
    {
        if (_disposed || !await _maintenanceGate.WaitAsync(0).ConfigureAwait(false)) return;
        bool attempted = false;
        try
        {
            if (InputReady)
            {
                ArduinoDiagnosticLog.CompleteIfActive("タッチパネルの定期応答が安定しました。");
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
                ArduinoDiagnosticLog.Write("PANEL_HEARTBEAT_STALE", "タッチパネルの定期応答が1秒以上停止。操作ボタンと測定開始を禁止します。");
                StatusChanged?.Invoke("タッチパネルの定期応答が停止。受信再開を待っています。");
            }
            if (connected && age < 3) return;
            if (Environment.TickCount64 < _nextAttempt) return;
            attempted = true;
            _nextAttempt = Environment.TickCount64 + 2000;
            await Task.Run(async () =>
            {
                Reset();
                await _link.DisconnectAsync().ConfigureAwait(false);
                if (_disposed) return;
                await VerifyCoreAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
            if (!_disposed) StatusChanged?.Invoke("タッチパネルを再接続しました。定期応答の安定を確認しています。");
        }
        catch (Exception ex)
        {
            Reset();
            if (!_disposed)
            {
                ArduinoDiagnosticLog.Write("PANEL_RECONNECT_WAIT", ex.Message);
                StatusChanged?.Invoke(ex is As100201PanelVersionException
                    ? ex.Message
                    : "タッチパネルの接続を待っています。USB か Wi-Fi でつながれば自動で再確認します。");
            }
        }
        finally
        {
            if (attempted) _nextAttempt = Environment.TickCount64 + 2000;
            _maintenanceGate.Release();
        }
    }

    // 記録できた(ACK:<受け取った行>)・できなかった(NAK:<理由>)をパネルへ返す。パネルは短く表示する。
    // 送れなくても呼び出し側の記録は済んでいるので、例外は異常ログに残して次の再接続に任せる。
    public bool TrySend(string line)
    {
        if (_disposed || !_link.IsConnected) return false;
        try
        {
            _link.SendLine(line);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or UnauthorizedAccessException)
        {
            ArduinoDiagnosticLog.Write("PANEL_SEND_ERROR", line + " / " + ex.Message);
            return false;
        }
    }

    public static string Ack(As100201PanelCommand command) => "ACK:" + command.Line;
    public static string Nak(string reason) => "NAK:" + reason;

    private void HandleLine(string line)
    {
        if (_disposed) return;
        if (line == "DIAG_BOOT:1")
        {
            // 接続の確認応答とは別。パネル再起動後の古い操作を無効にする。
            Reset();
            ArduinoDiagnosticLog.Write("PANEL_BOOT", "タッチパネルの起動通知");
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
                        ArduinoDiagnosticLog.Write("PANEL_FRAME_GAP", $"seq={previous}->{sequence} / パネル経過={boardDelta}ms / PC受信間隔={pcDelta:F1}ms");
                        gap = true;
                    }
                }
                if (gap || _stableSince == 0)
                {
                    _stableSince = now;
                    _version++;
                    ArduinoDiagnosticLog.Write(_lastFrameAt == 0 ? "PANEL_WARMUP" : "PANEL_RECOVERY", "タッチパネルの定期応答の継続を1秒間確認。古い操作は破棄します。");
                }
                _lastFrameAt = now;
                _lastSequence = sequence;
                _lastBoardMillis = boardMillis;
                _staleLogged = false;
            }
        }
        else if (line.StartsWith(As100201PanelBattery.Prefix, StringComparison.Ordinal))
        {
            // 電池残量は受け付けの決まり(定期応答の安定)と関係なく知らせる。形が違えば異常ログにだけ残す。
            if (As100201PanelBattery.TryParse(line, out var battery)) BatteryReported?.Invoke(battery);
            else ArduinoDiagnosticLog.Write("PANEL_UNKNOWN_LINE", line);
        }
        else if (As100201PanelCommand.TryParse(line, out var command))
        {
            if (InputReady) CommandReceived?.Invoke(command);
            else
            {
                ArduinoDiagnosticLog.Write("PANEL_COMMAND_DROPPED", "復旧確認中の操作を破棄 / " + line);
                // HOLD は人が待つ返事ではないので、断りを返さない(パネルの表示を乱さない)。
                if (command.Kind != As100201PanelCommandKind.Hold)
                    TrySend(Nak("接続の確認中です。少し待ってから押し直してください"));
            }
        }
        else if (!line.StartsWith(HelloPrefix, StringComparison.Ordinal))
        {
            // 接続確認の応答は通信部品が受け取る。それ以外の知らない行は異常ログにだけ残す。
            ArduinoDiagnosticLog.Write("PANEL_UNKNOWN_LINE", line);
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
        _link.LineReceived -= HandleLine;
        _link.Disconnected -= HandleDisconnected;
        _link.Dispose();
    }
}

// タッチパネルのスケッチの版がアプリと合わない(書き込み直しが要る)。
internal sealed class As100201PanelVersionException(string message) : InvalidOperationException(message);
