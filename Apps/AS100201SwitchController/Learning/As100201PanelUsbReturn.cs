namespace PSCAutomation.Apps.AS100201SwitchController;

// Wi-Fi で使っている最中に Tab5 の USB が挿されたら、空いているときに USB へ戻すかを決める(2026-11-18 人の決定)。
// それまでは「確認」を押すまで Wi-Fi のままだった(測定の途中で COM ポートを探し直さないための AI の仮決め)。
// - Tab5 の COM ポートができたらすぐ戻す(2026-11-18 人の決定 ②。最初の作りは挿されてから 2 秒待っていた)。
//   COM ポートが分からないときだけ、挿されてから SettleTime 待つ(Windows が COM ポートを作り終えるまで)
// - 空いている(測定中でない・パネルで選んでいる途中でない・ほかの処理中でない)ときだけ戻す。
//   PC が USB を開くと Tab5 が再起動することがあり、数秒は操作できず、パネルの画面も 1 画面目に戻るため
// - 試すのは挿すたびに 1 回。戻せずに Wi-Fi のままなら、抜いて挿し直すか「確認」を押すまで試さない
//   (毎回 Tab5 を再起動させないため)
// 画面から切り離して模擬で確かめる。時刻は ms(Environment.TickCount64)。
internal sealed class As100201UsbReturnWatch
{
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);
    private long? _seenSince;
    private bool _tried;

    // path: 今の道。usb: Tab5 の USB の様子。idle: 空いているか。
    // USB へ戻すのを試すときだけ true を返す(返したら試したことにする)。
    public bool Update(As100201PanelPath path, As100201UsbState usb, bool idle, long nowMs)
    {
        if (path == As100201PanelPath.Usb)
        {
            // USB でつながった。次に Wi-Fi へ移ったら、また挿されるのを待つ。
            _seenSince = null;
            _tried = false;
            return false;
        }
        // つながっていない(切れた・選び直している途中)ときは見張りの再接続に任せる。試したかどうかは覚えたまま。
        if (path != As100201PanelPath.Wifi) return false;
        if (usb is As100201UsbState.Absent or As100201UsbState.Unknown)
        {
            // 抜かれた(分からないときも挿さっていない扱い)。次に挿されたらまた試せる。
            _seenSince = null;
            _tried = false;
            return false;
        }
        _seenSince ??= nowMs;
        if (_tried || !idle) return false;
        if (usb != As100201UsbState.PortReady && nowMs - _seenSince.Value < (long)SettleTime.TotalMilliseconds) return false;
        _tried = true;
        return true;
    }
}
