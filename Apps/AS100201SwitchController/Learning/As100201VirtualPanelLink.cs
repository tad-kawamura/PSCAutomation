using System.Diagnostics;

namespace PSCAutomation.Apps.AS100201SwitchController;

// タッチパネル(M5Stack Tab5)の代わり(開発用、2026-11-10)。Tab5 が届く前に、学習モードの PC 側
// (操作ボタン → 台帳の行、操作ボタンでの LOGGER の停止と xdt の保存、途切れたときの受け付け)を実機の LOGGER で確かめるためのもの。
// COM ポートは開かない。パネルのスケッチと同じ行(確認応答・250ms の定期応答・ACT / DONE / UNDO)をアプリの中で作り、
// 本物と同じ As100201PanelProtocol に流す。見張り・受け付けの決まり・記録の処理は本物と同じ道を通る。
// 起動は `AS100201SwitchController.exe --virtual-panel` のときだけ(作業者が誤って選ばないよう、起動の窓には出さない)。
internal sealed class As100201VirtualPanelLink : IAs100201SerialLink
{
    public const string CommandLineSwitch = "--virtual-panel";
    private const int FrameIntervalMilliseconds = 250;

    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private System.Threading.Timer? _frameTimer;
    private uint _sequence;
    private volatile bool _connected, _unplugged, _framesPaused, _disposed;

    public event Action<string>? LineReceived;
    public event Action? Disconnected;
    // PC からパネルへ送った行(ACK / NAK)。本物のパネルが表示するものを、画面に出すために知らせる。
    public event Action<string>? LineSent;

    public bool IsConnected => _connected;

    // USB を抜いた状態にする(true)・差し直す(false)。抜くと切断を知らせ、差し直すまで接続確認に失敗する。
    public bool Unplugged
    {
        get => _unplugged;
        set
        {
            _unplugged = value;
            if (!value || !_connected) return;
            _connected = false;
            Disconnected?.Invoke();
        }
    }

    // 定期応答だけ止める(USB はつながったまま固まった状態)。
    public bool FramesPaused
    {
        get => _framesPaused;
        set => _framesPaused = value;
    }

    public Task VerifyAsync(string request, Func<string, bool> isReply, Action<string>? timingLog = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_unplugged)
            throw new InvalidOperationException("タッチパネル（代わり・開発用）は「抜いた状態」です。画面のチェックを外すと再接続します。");
        timingLog?.Invoke("タッチパネル内訳：代わり（開発用）。COM ポートは開かず、画面の代わりのボタンで操作します。");
        // パネルのスケッチと同じく、専用の確認文字列にだけ今の版で答える。
        if (request != As100201PanelProtocol.HelloRequest ||
            !isReply(As100201PanelProtocol.HelloPrefix + As100201PanelProtocol.PanelVersion))
            throw new InvalidOperationException("タッチパネル（代わり）の確認応答を受け付けませんでした。");
        _connected = true;
        lock (_gate) _frameTimer ??= new System.Threading.Timer(_ => SendFrame(), null, 0, FrameIntervalMilliseconds);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        _connected = false;
        return Task.CompletedTask;
    }

    public void SendLine(string line)
    {
        if (!_connected) throw new InvalidOperationException("タッチパネル（代わり）は接続されていません。");
        LineSent?.Invoke(line);
    }

    // 代わりのボタンが押された。パネルが確定した 1 行を送ったのと同じ扱い。つながっていなければ送らない(false)。
    public bool Press(string line)
    {
        if (_disposed || !_connected) return false;
        LineReceived?.Invoke(line);
        return true;
    }

    private void SendFrame()
    {
        if (_disposed || !_connected || _framesPaused) return;
        uint sequence;
        lock (_gate) sequence = ++_sequence;
        LineReceived?.Invoke($"DIAG_FRAME:{sequence}:{unchecked((uint)_clock.ElapsedMilliseconds)}");
    }

    public void Dispose()
    {
        _disposed = true;
        _connected = false;
        lock (_gate)
        {
            _frameTimer?.Dispose();
            _frameTimer = null;
        }
    }

    // 代わりのボタンに並べる行(パネルの 1 画面目 + 2 画面目で確定する全部)。表示名は台帳と同じ言葉。
    public static IReadOnlyList<As100201PanelCommand> AllCommands { get; } = BuildAllCommands();

    private static List<As100201PanelCommand> BuildAllCommands()
    {
        var lines = new List<string> { "ACT:MAG", "ACT:DEMAG:FULL", "ACT:DEMAG:TAP" };
        foreach (string side in new[] { "P", "R" })
            foreach (string direction in new[] { "IN", "OUT" })
                lines.Add($"ACT:NOZZLE:{side}:{direction}");
        // スペーサーは厚さと差の組み合わせが多いので、ここでは既定の厚さ・差「測っていない」で並べ、
        // 画面の「厚さ」「差」の選択で WithSpacer に差し替えて送る。
        foreach (string side in new[] { "P", "R" })
            foreach (string direction in new[] { "UP", "DOWN" })
                lines.Add($"ACT:SPACER:{side}:{direction}:{As100201SpacerCatalog.DefaultThickness}:{As100201PanelCommand.NotMeasured}");
        lines.Add("DONE");
        lines.Add("UNDO");
        lines.Add("HOLD");
        return lines.Select(As100201PanelCommand.Parse).ToList();
    }

    // スペーサーの行の厚さと差を差し替える。スペーサー以外はそのまま。
    public static As100201PanelCommand WithSpacer(As100201PanelCommand command, string thickness, string difference)
    {
        if (command.Operation is not (As100201Operation.SpacerP or As100201Operation.SpacerR)) return command;
        string[] parts = command.Line.Split(':');
        return As100201PanelCommand.Parse($"ACT:SPACER:{parts[2]}:{parts[3]}:{thickness}:{difference}");
    }
}
