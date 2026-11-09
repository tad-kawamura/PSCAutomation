namespace PSCAutomation.Apps.AS100201SwitchController;

internal enum As100201PressKind
{
    Single,
    Double,
    // 2 回押しの後に続けて押された分。ログだけに残す。
    Extra
}

// Arduino(スケッチ版 2)から届くスイッチの行。2 回押しの判定はスケッチが行う。
// PC_go / CR_go: 1 回押し(離してから待ち時間後)、PC_go_2 / CR_go_2: 2 回押し、*_EXTRA: 続けての押下。
// F8 / sw4 は 2 回押しなし(押した瞬間)。
internal readonly record struct As100201Switch(string Line, string Button, As100201PressKind Kind)
{
    private static readonly string[] Buttons = ["F8", "sw4", "CR_go", "PC_go"];
    private static readonly string[] DoubleButtons = ["CR_go", "PC_go"];

    public bool IsWaveform => Button is "PC_go" or "CR_go";

    public string DisplayName => Kind switch
    {
        As100201PressKind.Double => Button + "（2回押し）",
        As100201PressKind.Extra => Button + "（続けての押下）",
        _ => Button
    };

    public static As100201Switch Parse(string line) =>
        TryParse(line, out var result) ? result : throw new FormatException("スイッチ名ではありません: " + line);

    public static bool TryParse(string line, out As100201Switch result)
    {
        foreach (string button in Buttons)
        {
            if (line == button)
            {
                result = new(line, button, As100201PressKind.Single);
                return true;
            }
        }
        foreach (string button in DoubleButtons)
        {
            if (line == button + "_2")
            {
                result = new(line, button, As100201PressKind.Double);
                return true;
            }
            if (line == button + "_EXTRA")
            {
                result = new(line, button, As100201PressKind.Extra);
                return true;
            }
        }
        result = default;
        return false;
    }
}
