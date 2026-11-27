using System.Globalization;

namespace PSCAutomation.Apps.AS100201SwitchController;

internal enum As100201PanelCommandKind
{
    // 操作ボタン(着磁・減磁・ノズル P/R の合わせ始めと合わせた・スペーサー P/R)
    Operation,
    // 完了ボタン(盲栓確認 OK。1 個体の終わり)
    Done,
    // 取消ボタン(直前の記録を無効にする。パネル側で確認「はい」を押したあとに届く)
    Undo,
    // 発振器のボタン(版 6、2026-11-27)。位相を出し直し、止まっていれば測り始める。PhaseButton に PC1 / PC2 / CR1 / CR2
    Phase,
    // 「バルブを動かす」(版 6)。測定中なら止めて保存する。パネルは操作の画面へ移る
    Valve,
    // 「測り直す」(版 6、ノズルの画面)。測定中なら保存して、同じ位相ですぐ測り直す
    Remeasure,
    // 「測定停止」(版 6、通常モードの 1 画面目)
    Stop,
    // ノズルの画面の「戻る」(版 6、押し間違い)。合わせ始めの行を取り消す
    NozzleCancel
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

// ノズルの立ち上がりを合わせる作業の段階(版 6、2026-11-26 承認)。ノズル以外は None。
internal enum As100201NozzleStep
{
    None,
    // ノズル P 側(R 側)を選んだ = 合わせ始め
    Start,
    // 「P 側の立ち上がりを合わせた」
    Aligned
}

// タッチパネル(M5Stack Tab5)から届く確定済みの 1 行(SPEC_調整操作の記録.md「タッチパネルとのやり取り」「版 6 の変更」)。
// 2 画面目以降の選択と取消の確認はパネルの中で完結し、PC には確定した 1 行だけが届く。
//   ACT:MAG / ACT:DEMAG:FULL|TAP / ACT:NOZZLE:P|R:START|DONE / ACT:SPACER:P|R:UP|DOWN:<厚さ>:<差> / DONE / UNDO
//   PHASE:PC1|PC2|CR1|CR2 / VALVE / REMEASURE / STOP / ACT:NOZZLE:P|R:CANCEL
// 版 3(2026-11-13、人の指示): スペーサーは向きのあとに厚さ(箱の表示、mm。例 0.450)と、表示との差(μm。例 +2 / -1 / 0、
// 測っていなければ NA)を選ぶ。台帳に書く言葉(操作・方向・程度・厚さ)はここで決める。スペーサーの差は「程度」の列に書く。
// 版 5(2026-11-26、人の決定): 操作の行(ACT:、戻るを除く)の後ろに :MOVED(測定中に動かした)が付くことがある。
// 版 6(2026-11-27、人の承認): Arduino のスイッチを Tab5 にまとめた(発振器・バルブを動かす・測り直す・測定停止)。
// ノズルは方向(近づける / 離す)をやめ、合わせ始め(START)と合わせた(DONE)にした。パネルに触れた合図(HOLD)はなくした。
internal readonly record struct As100201PanelCommand(
    string Line, As100201PanelCommandKind Kind, As100201Operation? Operation, string Direction, string Degree, string Thickness = "")
{
    // 表示との差として受け付ける範囲(μm)。パネルが出すのは ±3 程度だが、パネル側の選択肢を広げても PC を直さなくてよいようにする。
    public const int MaxThicknessDifferenceMicrometers = 50;
    public const string NotMeasured = "NA";
    // 版 5(2026-11-26): 「測定中に動かした」の印。操作の行の後ろに付く(例 ACT:SPACER:P:UP:0.450:NA:MOVED)。
    public const string MovedSuffix = ":MOVED";
    public const string MovedText = "測定中に動かした";
    // 台帳の「備考」に書くノズルの段階(版 6)。
    public const string NozzleStartText = "合わせ始め";
    public const string NozzleAlignedText = "合わせた";
    // 発振器のボタンの名前(版 6)。PC の画面の位相 4 つ(PC 1 回押し・2 回押し・CR 1 回押し・2 回押しだった欄)に当たる。
    public static readonly string[] PhaseButtons = ["PC1", "PC2", "CR1", "CR2"];

    // 測定中に動かした(パネルを押す前に、測定したまま手を動かした)印が付いている(版 5)。
    public bool Moved { get; init; }
    // ノズルの段階(版 6)。ノズル以外は None。
    public As100201NozzleStep NozzleStep { get; init; }
    // 発振器のボタン(PC1 / PC2 / CR1 / CR2)。Phase 以外は空。
    public string PhaseButton { get; init; } = "";

    // 押されたボタンのノズルの側(ノズルの行・戻るのとき 'P' / 'R'。それ以外は null)。
    public char? NozzleSide => Operation switch
    {
        As100201Operation.NozzleP => 'P',
        As100201Operation.NozzleR => 'R',
        _ => null
    };

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

    // ノズルの段階を台帳の備考に書く言葉(合わせ始め / 合わせた)。ノズル以外は空。
    public string NozzleStepText => NozzleStep switch
    {
        As100201NozzleStep.Start => NozzleStartText,
        As100201NozzleStep.Aligned => NozzleAlignedText,
        _ => ""
    };

    // 発振器のボタンを、今までの PC_go / CR_go の 1 回押し・2 回押しに置き換える(位相の設定と台帳の「側」を今までどおり使うため)。
    public As100201Switch ToSwitch() => PhaseButton switch
    {
        "PC1" => new As100201Switch("PC_go", "PC_go", As100201PressKind.Single),
        "PC2" => new As100201Switch("PC_go_2", "PC_go", As100201PressKind.Double),
        "CR1" => new As100201Switch("CR_go", "CR_go", As100201PressKind.Single),
        "CR2" => new As100201Switch("CR_go_2", "CR_go", As100201PressKind.Double),
        _ => throw new InvalidOperationException("発振器のボタンの行ではありません: " + Line)
    };

    // ログに出す名前。例: 「ノズル P 側（合わせ始め）」「スペーサー R 側（上げる・0.450・差+2μm）」「減磁（しっかり）」「PC 位相1」
    public string DisplayName
    {
        get
        {
            switch (Kind)
            {
                case As100201PanelCommandKind.Done: return "完了";
                case As100201PanelCommandKind.Undo: return "取消";
                case As100201PanelCommandKind.Phase: return PhaseDisplayName(PhaseButton);
                case As100201PanelCommandKind.Valve: return "バルブを動かす";
                case As100201PanelCommandKind.Remeasure: return "測り直す";
                case As100201PanelCommandKind.Stop: return "測定停止";
                case As100201PanelCommandKind.NozzleCancel: return OperationText + " 側（戻る）";
            }
            string name = Operation is As100201Operation.NozzleP or As100201Operation.NozzleR
                or As100201Operation.SpacerP or As100201Operation.SpacerR ? OperationText + " 側" : OperationText;
            string details = string.Join("・", new[] { NozzleStepText, Direction, Thickness, Degree, Moved ? MovedText : "" }.Where(s => s.Length > 0));
            return details.Length == 0 ? name : $"{name}（{details}）";
        }
    }

    public static string PhaseDisplayName(string button) => button.Length == 3 ? $"{button[..2]} 位相{button[2]}" : button;

    public static As100201PanelCommand Parse(string line) =>
        TryParse(line, out var result) ? result : throw new FormatException("タッチパネルの操作の行ではありません: " + line);

    public static bool TryParse(string line, out As100201PanelCommand result)
    {
        result = default;
        As100201PanelCommandKind? simple = line switch
        {
            "DONE" => As100201PanelCommandKind.Done,
            "UNDO" => As100201PanelCommandKind.Undo,
            "VALVE" => As100201PanelCommandKind.Valve,
            "REMEASURE" => As100201PanelCommandKind.Remeasure,
            "STOP" => As100201PanelCommandKind.Stop,
            _ => null
        };
        if (simple is As100201PanelCommandKind kind)
        {
            result = new(line, kind, null, "", "") { PhaseButton = "" };
            return true;
        }
        if (line.StartsWith("PHASE:", StringComparison.Ordinal))
        {
            string button = line["PHASE:".Length..];
            if (!PhaseButtons.Contains(button)) return false;
            result = new(line, As100201PanelCommandKind.Phase, null, "", "") { PhaseButton = button };
            return true;
        }
        if (!line.StartsWith("ACT:", StringComparison.Ordinal)) return false;
        // 版 5: 後ろの :MOVED は印。外した残りを今までどおり読み、読めたら印を付ける(戻るには付かない)。
        if (line.EndsWith(MovedSuffix, StringComparison.Ordinal))
        {
            string body = line[..^MovedSuffix.Length];
            if (body.EndsWith(MovedSuffix, StringComparison.Ordinal) || !TryParse(body, out var inner) ||
                inner.Kind != As100201PanelCommandKind.Operation) return false;
            result = inner with { Line = line, Moved = true };
            return true;
        }
        string[] parts = line.Split(':');
        switch (parts[1])
        {
            case "MAG" when parts.Length == 2:
                result = new(line, As100201PanelCommandKind.Operation, As100201Operation.Magnetize, "", "") { PhaseButton = "" };
                return true;
            case "DEMAG" when parts.Length == 3 && parts[2] is "FULL" or "TAP":
                result = new(line, As100201PanelCommandKind.Operation, As100201Operation.Demagnetize, "",
                    parts[2] == "FULL" ? "しっかり" : "ちょんちょん") { PhaseButton = "" };
                return true;
            case "NOZZLE" when parts.Length == 4 && parts[2] is "P" or "R" && parts[3] is "START" or "DONE" or "CANCEL":
                var operation = parts[2] == "P" ? As100201Operation.NozzleP : As100201Operation.NozzleR;
                result = parts[3] == "CANCEL"
                    ? new(line, As100201PanelCommandKind.NozzleCancel, operation, "", "") { PhaseButton = "" }
                    : new(line, As100201PanelCommandKind.Operation, operation, "", "")
                    {
                        PhaseButton = "",
                        NozzleStep = parts[3] == "START" ? As100201NozzleStep.Start : As100201NozzleStep.Aligned
                    };
                return true;
            case "SPACER" when parts.Length == 6 && parts[2] is "P" or "R" && parts[3] is "UP" or "DOWN" &&
                IsThickness(parts[4]) && TryDescribeDifference(parts[5], out string difference):
                result = new(line, As100201PanelCommandKind.Operation,
                    parts[2] == "P" ? As100201Operation.SpacerP : As100201Operation.SpacerR,
                    parts[3] == "UP" ? "上げる" : "下げる", difference, parts[4]) { PhaseButton = "" };
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
