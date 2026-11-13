namespace PSCAutomation.Apps.AS100201SwitchController;

// タッチパネルへの道(USB / Wi-Fi)。
internal enum As100201PanelPath { None, Usb, Wifi }

// タッチパネルへの 2 本の道(USB と Wi-Fi)を 1 本に見せる(SPEC_調整操作の記録.md「Wi-Fi・バッテリーへの変更」、2026-11-13 承認)。
// - 道を選ぶのは確認のとき(接続確認・「確認」ボタン・見張りの再接続)だけ。毎回 USB を先に確かめ、見つからなければ Wi-Fi(両方なら USB)
// - 確認が通った道を「今の道」にし、もう一方は閉じる。Wi-Fi で使っている最中に USB を挿しても、次に選び直すまで移らない
//   (測定の途中で COM ポートを探し直さないため)
// - 今の道が切れたら切断を知らせる。見張り(As100201PanelProtocol)が再接続するときに、また USB → Wi-Fi の順で選ぶので、もう一方へ移る
// - 今の道でない側の行と切断は捨てる(Tab5 は PING が届いた道にだけ送るので、ふつうは来ない)
// 見張りの秒数・受け付けの決まりは As100201PanelProtocol のまま(人の決定「今と同じ」)。
internal sealed class As100201PanelPathLink : IAs100201SerialLink
{
    private readonly IAs100201SerialLink _usb;
    private readonly IAs100201SerialLink _wifi;
    private readonly object _gate = new();
    private As100201PanelPath _active, _lastConnected;
    private volatile bool _disposed;

    public As100201PanelPathLink(IAs100201SerialLink usb, IAs100201SerialLink wifi)
    {
        _usb = usb;
        _wifi = wifi;
        _usb.LineReceived += line => Forward(As100201PanelPath.Usb, line);
        _wifi.LineReceived += line => Forward(As100201PanelPath.Wifi, line);
        _usb.Disconnected += () => LinkDisconnected(As100201PanelPath.Usb);
        _wifi.Disconnected += () => LinkDisconnected(As100201PanelPath.Wifi);
    }

    public event Action<string>? LineReceived;
    public event Action? Disconnected;
    // 確認が通った道が、前回つながっていた道と違うとき(最初につながったときも)に知らせる。
    public event Action<As100201PanelPath>? PathChanged;

    public As100201PanelPath ActivePath { get { lock (_gate) return _active; } }
    public bool IsConnected => Link(ActivePath)?.IsConnected == true;

    public static string PathName(As100201PanelPath path) => path switch
    {
        As100201PanelPath.Usb => "USB",
        As100201PanelPath.Wifi => "Wi-Fi",
        _ => "なし"
    };

    public async Task VerifyAsync(string request, Func<string, bool> isReply, Action<string>? timingLog = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var failures = new List<string>();
        foreach (var path in new[] { As100201PanelPath.Usb, As100201PanelPath.Wifi })
        {
            var link = Link(path)!;
            // 確認中に届く行(定期応答)も今の道として流す。失敗したら戻す。
            lock (_gate) _active = path;
            timingLog?.Invoke($"タッチパネル内訳：{PathName(path)} で確認します");
            try
            {
                await link.VerifyAsync(request, isReply, timingLog).ConfigureAwait(false);
            }
            catch (Exception ex) when (!_disposed)
            {
                lock (_gate) if (_active == path) _active = As100201PanelPath.None;
                failures.Add($"{PathName(path)}：{ex.Message}");
                continue;
            }
            var other = Link(Other(path))!;
            if (other.IsConnected) await other.DisconnectAsync().ConfigureAwait(false);
            bool changed;
            lock (_gate)
            {
                changed = _lastConnected != path;
                _lastConnected = path;
            }
            if (changed) PathChanged?.Invoke(path);
            return;
        }
        ObjectDisposedException.ThrowIf(_disposed, this);
        throw new InvalidOperationException("USB でも Wi-Fi でも見つかりません。\n" + string.Join("\n", failures));
    }

    public async Task DisconnectAsync()
    {
        lock (_gate) _active = As100201PanelPath.None;
        await _usb.DisconnectAsync().ConfigureAwait(false);
        await _wifi.DisconnectAsync().ConfigureAwait(false);
    }

    public void SendLine(string line)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var link = Link(ActivePath) ?? throw new InvalidOperationException("タッチパネルにつながっていません。");
        link.SendLine(line);
    }

    private IAs100201SerialLink? Link(As100201PanelPath path) => path switch
    {
        As100201PanelPath.Usb => _usb,
        As100201PanelPath.Wifi => _wifi,
        _ => null
    };

    private static As100201PanelPath Other(As100201PanelPath path) =>
        path == As100201PanelPath.Usb ? As100201PanelPath.Wifi : As100201PanelPath.Usb;

    private void Forward(As100201PanelPath path, string line)
    {
        if (_disposed || ActivePath != path) return;
        LineReceived?.Invoke(line);
    }

    private void LinkDisconnected(As100201PanelPath path)
    {
        lock (_gate)
        {
            if (_disposed || _active != path) return;
            _active = As100201PanelPath.None;
        }
        Disconnected?.Invoke();
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_gate) _active = As100201PanelPath.None;
        _usb.Dispose();
        _wifi.Dispose();
    }
}
