using System.Globalization;

namespace PSCAutomation.Apps.AS100201SwitchController;

// タッチパネル(Tab5)の電池残量(SPEC_調整操作の記録.md「Wi-Fi・バッテリーへの変更」、2026-11-13 承認)。
// パネルは 30 秒ごとと、PC の確認に答えた直後に今の道へ BATT:<残量%>:<電圧mV>:<充電中 0/1> を送る(やり取りの版 2)。
// 残量は Tab5 の M5Unified(getBatteryLevel)が電池の電圧から出した値。電池が付いていないときは残量 -1。
internal sealed record As100201PanelBattery(int Level, int Millivolts, bool Charging)
{
    public const string Prefix = "BATT:";

    public bool Present => Level >= 0;

    public string DisplayText => !Present ? "電池なし" : Charging ? $"充電中 {Level}%" : $"電池 {Level}%";

    public static bool TryParse(string line, out As100201PanelBattery battery)
    {
        battery = new As100201PanelBattery(-1, 0, false);
        if (!line.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        string[] parts = line[Prefix.Length..].Split(':');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int level) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int millivolts) ||
            parts[2] is not ("0" or "1") || level is < -1 or > 100)
            return false;
        battery = new As100201PanelBattery(level, millivolts, parts[2] == "1");
        return true;
    }
}

// 残量が少なくなったら PC のログに 1 回だけ出す(人の決定。20% 以下で出し、30% を超えたら次に下がったときにまた出す。数字は AI の仮決め)。
// 充電中は出さない(作業者がもう手を打っているため)。電池なし(-1)は出さない。
internal sealed class As100201BatteryWatch
{
    public const int LowLevel = 20;
    public const int RearmLevel = 30;
    private bool _armed = true;

    // ログに出す文言。出さないときは null。
    public string? Update(As100201PanelBattery battery)
    {
        if (!battery.Present) return null;
        if (battery.Level > RearmLevel)
        {
            _armed = true;
            return null;
        }
        if (battery.Charging || battery.Level > LowLevel || !_armed) return null;
        _armed = false;
        return $"タッチパネルの電池が残り {battery.Level}% です。充電してください（USB-C を挿せば、充電しながらそのまま使えます）。";
    }
}
