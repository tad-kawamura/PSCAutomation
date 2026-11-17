using System.Runtime.InteropServices;

namespace PSCAutomation.Apps.AS100201SwitchController;

// Tab5 専用の Wi-Fi 子機をつなぎ直す(SPEC_調整操作の記録.md「Wi-Fi・バッテリーへの変更」の追記、2026-11-17 人の決定 B)。
// 実機で分かったこと: Tab5 が再起動すると親機がやり直しになるが、PC の子機(BUFFALO WI-U2-150M)は気づかず
// 「接続中」のまま古いつながりに留まり、Tab5 へ届かなくなる。子機を切ってつなぎ直すとすぐ届く。
// アプリが触るのは、登録名 As100201PanelNetworkSettings.WifiProfileName を持つ子機だけ(内蔵の Wi-Fi・社内ネットには触らない)。

// 子機のつなぎ直しの結果。Ok なら Message は子機の名前、そうでなければ人に見せる理由。
// つながらないのはよくあること(Tab5 の電源が切れている等)なので、例外にしない(デバッガーが止まらないように)。
internal readonly record struct As100201WifiRefreshResult(bool Ok, string Message);

// 子機を切ってつなぎ直す(模擬に差し替えるための薄い interface)。
internal interface IAs100201WifiAdapterControl
{
    // 登録名 profileName を持つ子機を 1 台だけ探し、切ってその登録でつなぎ直す。つながるまで待つ。
    // 見つからない・2 台以上ある・つながらないときは Ok=false と理由を返す。
    Task<As100201WifiRefreshResult> ReconnectAsync(string profileName, CancellationToken cancellationToken = default);
}

// Wi-Fi の道(As100201NetworkLink)を包み、確認が 2 回続けて失敗したら子機のつなぎ直しを裏で始める。
// つなぎ直しは最大 20 秒ほどかかるので待たない(見張りは Arduino・LOGGER と同じ流れで回るため、止めない)。
// つなぎ直している間の確認はすぐ失敗にし、終わったあとの次の確認(見張りの再接続、約 2 秒おき)で Wi-Fi に移る。
// つなぎ直しは MinInterval に 1 回まで(Tab5 の電源が切れているときに繰り返さない)。
internal sealed class As100201WifiRefreshLink : IAs100201SerialLink
{
    public const int FailuresBeforeRefresh = 2;               // 人の決定「2 回続けてつながらないとき」
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);

    private readonly IAs100201SerialLink _inner;
    private readonly IAs100201WifiAdapterControl _adapter;
    private readonly string _profileName;
    private readonly Func<long> _nowMs;
    private int _failures;
    private long? _lastRefreshAt;
    private Task? _refresh;

    // 裏で進めているつなぎ直し(テストで終わりを待つため)。無ければ完了済みの Task。
    internal Task RefreshTask => _refresh ?? Task.CompletedTask;

    public As100201WifiRefreshLink(IAs100201SerialLink inner, IAs100201WifiAdapterControl adapter, string profileName,
        Func<long>? nowMs = null)
    {
        _inner = inner;
        _adapter = adapter;
        _profileName = profileName;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _inner.LineReceived += line => LineReceived?.Invoke(line);
        _inner.Disconnected += () => Disconnected?.Invoke();
    }

    public event Action<string>? LineReceived;
    public event Action? Disconnected;
    // 子機をつなぎ直した・つなぎ直せなかったことを人に見せる文(画面のログ用)。
    public event Action<string>? Notice;

    public bool IsConnected => _inner.IsConnected;

    // 見張り(As100201PanelProtocol)が順に呼ぶので、同時には呼ばれない。
    public async Task VerifyAsync(string request, Func<string, bool> isReply, Action<string>? timingLog = null)
    {
        if (_refresh is { IsCompleted: false })
            throw new InvalidOperationException($"Wi-Fi の子機を {_profileName} につなぎ直しています。");
        _refresh = null;
        try
        {
            await _inner.VerifyAsync(request, isReply, timingLog).ConfigureAwait(false);
            _failures = 0;
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            _failures++;
            long now = _nowMs();
            if (_failures < FailuresBeforeRefresh ||
                (_lastRefreshAt is long last && now - last < (long)MinInterval.TotalMilliseconds)) throw;
            _failures = 0;
            _lastRefreshAt = now;
            _refresh = RefreshAsync();
            throw new InvalidOperationException($"{ex.Message}（{FailuresBeforeRefresh} 回続けてつながらないため、子機を {_profileName} につなぎ直します）", ex);
        }
    }

    public Task DisconnectAsync() => _inner.DisconnectAsync();
    public void SendLine(string line) => _inner.SendLine(line);
    public void Dispose() => _inner.Dispose();

    private async Task RefreshAsync()
    {
        As100201WifiRefreshResult result;
        try { result = await _adapter.ReconnectAsync(_profileName).ConfigureAwait(false); }
        catch (Exception ex) { result = new As100201WifiRefreshResult(false, ex.Message); }
        Report(result.Ok
            ? $"Wi-Fi で {FailuresBeforeRefresh} 回続けてタッチパネルにつながらないため、子機（{result.Message}）を {_profileName} につなぎ直しました。"
            : $"Wi-Fi の子機を {_profileName} につなぎ直せませんでした：{result.Message}");
    }

    private void Report(string message)
    {
        PSCAutomation.Wrappers.Arduino.ArduinoDiagnosticLog.Write("PANEL_WIFI_REFRESH", message);
        Notice?.Invoke(message);
    }
}

// Windows の Wi-Fi の仕組み(wlanapi.dll)で子機をつなぎ直す。netsh の表示は Windows の言語で変わるので使わない。
// 子機は「登録名 profileName を持つ Wi-Fi アダプター」で見つける(アダプターの名前「Wi-Fi 2」は PC ごとに変わるため)。
// パスワードは扱わず、Windows に保存された登録だけを使う。管理者の権限は要らない。
internal sealed class As100201WlanAdapterControl : IAs100201WifiAdapterControl
{
    // つながるまで待つ長さと、接続を頼み直す間隔。Tab5 が再起動している最中(親機が数秒消える)に頼むと
    // Windows がすぐ諦め、自動接続の次の機会(実機で約 30 秒後)まで切れたままになったため、数秒おきに頼み直す(2026-11-17 実機)。
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan AddressWait = TimeSpan.FromSeconds(1.5);   // つながってから IP が配られるまでの余裕

    public Task<As100201WifiRefreshResult> ReconnectAsync(string profileName, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            // 例外は理由に変えて返す(呼ぶ側はつながらなかったことを人に見せるだけ)。
            try { return ReconnectCore(profileName, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return new As100201WifiRefreshResult(false, ex.Message); }
        }, cancellationToken);

    private static As100201WifiRefreshResult ReconnectCore(string profileName, CancellationToken cancellationToken)
    {
        uint error = WlanOpenHandle(2, IntPtr.Zero, out _, out IntPtr handle);
        if (error != 0) return Failed("Wi-Fi の仕組みを開けません", error);
        try
        {
            var matches = FindAdaptersWithProfile(handle, profileName, out string? listError);
            if (listError is not null) return new(false, listError);
            if (matches.Count == 0)
                return new(false, $"Wi-Fi の登録「{profileName}」を持つ子機が見つかりません（子機が挿さっているか、子機で {profileName} に 1 回つないだかを確かめてください）。");
            if (matches.Count > 1)
                return new(false, $"Wi-Fi の登録「{profileName}」を持つアダプターが {matches.Count} 台あります（{string.Join("、", matches.Select(m => m.Description))}）。" +
                    "内蔵の Wi-Fi まで切らないよう、つなぎ直しません。子機以外の登録を消してください。");
            var (guid, description) = matches[0];

            // 切断の結果は問わない(もともと切れていることもある)。
            WlanDisconnect(handle, ref guid, IntPtr.Zero);
            Thread.Sleep(500);
            var parameters = new WlanConnectionParameters
            {
                ConnectionMode = 0,        // wlan_connection_mode_profile
                Profile = profileName,
                Ssid = IntPtr.Zero,
                DesiredBssidList = IntPtr.Zero,
                BssType = 1,               // dot11_BSS_type_infrastructure
                Flags = 0
            };
            var deadline = DateTime.UtcNow + ConnectTimeout;
            var nextRequest = DateTime.MinValue;
            uint lastError = 0;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTime.UtcNow >= nextRequest)
                {
                    lastError = WlanConnect(handle, ref guid, ref parameters, IntPtr.Zero);
                    nextRequest = DateTime.UtcNow + RetryInterval;
                }
                Thread.Sleep(250);
                if (QueryState(handle, guid) == 1)   // wlan_interface_state_connected
                {
                    Thread.Sleep(AddressWait);
                    return new(true, description);
                }
            }
            string detail = lastError == 0 ? "" : $"、最後の接続の頼みは Windows のエラー {lastError}";
            return new(false, $"子機（{description}）が {ConnectTimeout.TotalSeconds:0} 秒以内に {profileName} につながりません（Tab5 の電源が切れている・離れすぎている{detail}）。");
        }
        finally
        {
            WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    private static As100201WifiRefreshResult Failed(string what, uint error) => new(false, $"{what}（Windows のエラー {error}）。");

    private static List<(Guid Guid, string Description)> FindAdaptersWithProfile(IntPtr handle, string profileName, out string? error)
    {
        var result = new List<(Guid, string)>();
        error = null;
        uint code = WlanEnumInterfaces(handle, IntPtr.Zero, out IntPtr list);
        if (code != 0)
        {
            error = Failed("Wi-Fi アダプターの一覧を取れません", code).Message;
            return result;
        }
        try
        {
            int count = Marshal.ReadInt32(list, 0);
            for (int i = 0; i < count; i++)
            {
                IntPtr item = list + 8 + i * InterfaceInfoSize;
                Guid guid = Marshal.PtrToStructure<Guid>(item);
                string description = Marshal.PtrToStringUni(item + 16) ?? "";
                if (HasProfile(handle, guid, profileName)) result.Add((guid, description));
            }
        }
        finally
        {
            WlanFreeMemory(list);
        }
        return result;
    }

    private static bool HasProfile(IntPtr handle, Guid guid, string profileName)
    {
        if (WlanGetProfileList(handle, ref guid, IntPtr.Zero, out IntPtr list) != 0) return false;
        try
        {
            int count = Marshal.ReadInt32(list, 0);
            for (int i = 0; i < count; i++)
            {
                string name = Marshal.PtrToStringUni(list + 8 + i * ProfileInfoSize) ?? "";
                if (string.Equals(name, profileName, StringComparison.Ordinal)) return true;
            }
            return false;
        }
        finally
        {
            WlanFreeMemory(list);
        }
    }

    private static int QueryState(IntPtr handle, Guid guid)
    {
        if (WlanQueryInterface(handle, ref guid, 6 /* wlan_intf_opcode_interface_state */, IntPtr.Zero,
                out _, out IntPtr data, IntPtr.Zero) != 0) return -1;
        try { return Marshal.ReadInt32(data); }
        finally { WlanFreeMemory(data); }
    }

    // WLAN_INTERFACE_INFO: GUID(16) + WCHAR[256](512) + 状態(4)。WLAN_PROFILE_INFO: WCHAR[256](512) + フラグ(4)。
    // どちらの一覧も先頭に件数と位置の 2 つの DWORD(8 バイト)がある。
    private const int InterfaceInfoSize = 16 + 512 + 4;
    private const int ProfileInfoSize = 512 + 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanConnectionParameters
    {
        public int ConnectionMode;
        [MarshalAs(UnmanagedType.LPWStr)] public string Profile;
        public IntPtr Ssid;
        public IntPtr DesiredBssidList;
        public int BssType;
        public uint Flags;
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);
    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);
    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);
    [DllImport("wlanapi.dll")]
    private static extern uint WlanGetProfileList(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr reserved, out IntPtr profileList);
    [DllImport("wlanapi.dll")]
    private static extern uint WlanDisconnect(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr reserved);
    [DllImport("wlanapi.dll")]
    private static extern uint WlanConnect(IntPtr clientHandle, ref Guid interfaceGuid, ref WlanConnectionParameters parameters, IntPtr reserved);
    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, int opCode, IntPtr reserved,
        out int dataSize, out IntPtr data, IntPtr opcodeValueType);
    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);
}
