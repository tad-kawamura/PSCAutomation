namespace PSCAutomation.Apps.AS100201SwitchController;

internal enum As100201PanelCommandKind
{
    // 操作ボタン(着磁・減磁・ノズル P/R・スペーサー P/R)
    Operation,
    // 完了ボタン(盲栓確認 OK。1 個体の終わり)
    Done,
    // 取消ボタン(直前の操作の記録を無効にする。パネル側で確認「はい」を押したあとに届く)
    Undo
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

// タッチパネル(ESP32-S3)から届く確定済みの 1 行(SPEC_調整操作の記録.md「タッチパネルとのやり取り」)。
// 2 画面目の選択と取消の確認はパネルの中で完結し、PC には確定した 1 行だけが届く。
//   ACT:MAG / ACT:DEMAG:FULL|TAP / ACT:NOZZLE:P|R:IN|OUT:1|2|3 / ACT:SPACER:P|R:UP|DOWN / DONE / UNDO
// 台帳に書く言葉(操作・方向・程度)はここで決める。スペーサーの厚さは「いずれ決める」なので、まだ受け取らない。
internal readonly record struct As100201PanelCommand(
    string Line, As100201PanelCommandKind Kind, As100201Operation? Operation, string Direction, string Degree)
{
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

    // ログに出す名前。例: 「ノズル P 側（押し込む・普通）」「減磁（しっかり）」「完了」
    public string DisplayName
    {
        get
        {
            if (Kind == As100201PanelCommandKind.Done) return "完了";
            if (Kind == As100201PanelCommandKind.Undo) return "取消";
            string name = Operation is As100201Operation.NozzleP or As100201Operation.NozzleR
                or As100201Operation.SpacerP or As100201Operation.SpacerR ? OperationText + " 側" : OperationText;
            string details = string.Join("・", new[] { Direction, Degree }.Where(s => s.Length > 0));
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
            case "NOZZLE" when parts.Length == 5 && parts[2] is "P" or "R" && parts[3] is "IN" or "OUT" && parts[4] is "1" or "2" or "3":
                result = new(line, As100201PanelCommandKind.Operation,
                    parts[2] == "P" ? As100201Operation.NozzleP : As100201Operation.NozzleR,
                    parts[3] == "IN" ? "押し込む" : "外に出す",
                    parts[4] switch { "1" => "微調整", "2" => "普通", _ => "大きく" });
                return true;
            case "SPACER" when parts.Length == 4 && parts[2] is "P" or "R" && parts[3] is "UP" or "DOWN":
                result = new(line, As100201PanelCommandKind.Operation,
                    parts[2] == "P" ? As100201Operation.SpacerP : As100201Operation.SpacerR,
                    parts[3] == "UP" ? "上げる" : "下げる", "");
                return true;
            default:
                return false;
        }
    }
}
