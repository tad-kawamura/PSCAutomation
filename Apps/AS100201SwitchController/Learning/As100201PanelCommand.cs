using System.Globalization;

namespace PSCAutomation.Apps.AS100201SwitchController;

internal enum As100201PanelCommandKind
{
    // 操作ボタン(着磁・減磁・ノズル P/R・スペーサー P/R)
    Operation,
    // 完了ボタン(盲栓確認 OK。1 個体の終わり)
    Done,
    // 取消ボタン(直前の操作の記録を無効にする。パネル側で確認「はい」を押したあとに届く)
    Undo,
    // パネルに触れた(版 3、2026-11-13)。人が次の行動を決めた合図。測定中なら LOGGER を止める。台帳の操作の行にはしない
    Hold
}

internal enum As100201Operation
{
    Magnetize,
    Demagnetize,
    NozzleP,
    NozzleR,
    SpacerP,
    SpacerR
}

// タッチパネル(M5Stack Tab5)から届く確定済みの 1 行(SPEC_調整操作の記録.md「タッチパネルとのやり取り」)。
// 2 画面目以降の選択と取消の確認はパネルの中で完結し、PC には確定した 1 行だけが届く。
//   ACT:MAG / ACT:DEMAG:FULL|TAP / ACT:NOZZLE:P|R:IN|OUT / ACT:SPACER:P|R:UP|DOWN:<厚さ>:<差> / DONE / UNDO / HOLD
// 版 3(2026-11-13、人の指示): ノズルは程度を選ばず方向だけ(IN=近づける、OUT=離す)。
// スペーサーは向きのあとに厚さ(箱の表示、mm。例 0.450)と、表示との差(μm。例 +2 / -1 / 0、測っていなければ NA)を選ぶ。
// 台帳に書く言葉(操作・方向・程度・厚さ)はここで決める。スペーサーの差は「程度」の列に書く(列を増やさないため。仮決め)。
internal readonly record struct As100201PanelCommand(
    string Line, As100201PanelCommandKind Kind, As100201Operation? Operation, string Direction, string Degree, string Thickness = "")
{
    // 表示との差として受け付ける範囲(μm)。パネルが出すのは ±3 程度だが、パネル側の選択肢を広げても PC を直さなくてよいようにする。
    public const int MaxThicknessDifferenceMicrometers = 50;
    public const string NotMeasured = "NA";

    public string OperationText => Operation switch
    {
        As100201Operation.Magnetize => "着磁",
        As100201Operation.Demagnetize => "減磁",
        As100201Operation.NozzleP => "ノズル P",
        As100201Operation.NozzleR => "ノズル R",
        As100201Operation.SpacerP => "スペーサー P",
        As100201Operation.SpacerR => "スペーサー R",
        _ => ""
    };

    // ログに出す名前。例: 「ノズル P 側（近づける）」「スペーサー R 側（上げる・0.450・差+2μm）」「減磁（しっかり）」「完了」
    public string DisplayName
    {
        get
        {
            if (Kind == As100201PanelCommandKind.Done) return "完了";
            if (Kind == As100201PanelCommandKind.Undo) return "取消";
            if (Kind == As100201PanelCommandKind.Hold) return "パネルに触れた";
            string name = Operation is As100201Operation.NozzleP or As100201Operation.NozzleR
                or As100201Operation.SpacerP or As100201Operation.SpacerR ? OperationText + " 側" : OperationText;
            string details = string.Join("・", new[] { Direction, Thickness, Degree }.Where(s => s.Length > 0));
            return details.Length == 0 ? name : $"{name}（{details}）";
        }
    }

    public static As100201PanelCommand Parse(string line) =>
        TryParse(line, out var result) ? result : throw new FormatException("タッチパネルの操作の行ではありません: " + line);

    public static bool TryParse(string line, out As100201PanelCommand result)
    {
        result = default;
        if (line == "DONE")
        {
            result = new(line, As100201PanelCommandKind.Done, null, "", "");
            return true;
        }
        if (line == "UNDO")
        {
            result = new(line, As100201PanelCommandKind.Undo, null, "", "");
            return true;
        }
        if (line == "HOLD")
        {
            result = new(line, As100201PanelCommandKind.Hold, null, "", "");
            return true;
        }
        if (!line.StartsWith("ACT:", StringComparison.Ordinal)) return false;
        string[] parts = line.Split(':');
        switch (parts[1])
        {
            case "MAG" when parts.Length == 2:
                result = new(line, As100201PanelCommandKind.Operation, As100201Operation.Magnetize, "", "");
                return true;
            case "DEMAG" when parts.Length == 3 && parts[2] is "FULL" or "TAP":
                result = new(line, As100201PanelCommandKind.Operation, As100201Operation.Demagnetize, "",
                    parts[2] == "FULL" ? "しっかり" : "ちょんちょん");
                return true;
            case "NOZZLE" when parts.Length == 4 && parts[2] is "P" or "R" && parts[3] is "IN" or "OUT":
                result = new(line, As100201PanelCommandKind.Operation,
                    parts[2] == "P" ? As100201Operation.NozzleP : As100201Operation.NozzleR,
                    parts[3] == "IN" ? "近づける" : "離す", "");
                return true;
            case "SPACER" when parts.Length == 6 && parts[2] is "P" or "R" && parts[3] is "UP" or "DOWN" &&
                IsThickness(parts[4]) && TryDescribeDifference(parts[5], out string difference):
                result = new(line, As100201PanelCommandKind.Operation,
                    parts[2] == "P" ? As100201Operation.SpacerP : As100201Operation.SpacerR,
                    parts[3] == "UP" ? "上げる" : "下げる", difference, parts[4]);
                return true;
            default:
                return false;
        }
    }

    // 箱の表示の厚さ(mm)。「0.450」の形(整数 1 桁・小数 3 桁)だけ受け付ける。
    private static bool IsThickness(string text) =>
        text.Length == 5 && char.IsAsciiDigit(text[0]) && text[1] == '.' &&
        char.IsAsciiDigit(text[2]) && char.IsAsciiDigit(text[3]) && char.IsAsciiDigit(text[4]);

    // 表示との差(μm)。NA(測っていない)・0・+n・-n。台帳の「程度」の列に書く言葉にする。
    private static bool TryDescribeDifference(string text, out string description)
    {
        description = "";
        if (text == NotMeasured)
        {
            description = "差未測定";
            return true;
        }
        if (text.Length == 0 || (text != "0" && text[0] is not ('+' or '-'))) return false;
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value) ||
            Math.Abs(value) > MaxThicknessDifferenceMicrometers || (value == 0 && text != "0")) return false;
        description = value == 0 ? "差0μm" : $"差{value:+0;-0}μm";
        return true;
    }
}
