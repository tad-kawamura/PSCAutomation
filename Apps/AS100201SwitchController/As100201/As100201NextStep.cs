namespace PSCAutomation.Apps.AS100201SwitchController;

// 画面上部に大きく出す「次にすること」(2026-11-26、人の要望。AS110AllCheck の「次にすること」と同じ考え)。
// 測定を始められない理由のうち、いちばん先に片付けるもの 1 つと、その直し方を出す。
// 判定は測定の準備(RefreshReadiness)と学習モードの受け付け(As100201LearningGate)と同じ条件を、手で片付ける順に並べたもの。
// 版 6(2026-11-27): Arduino をなくし、タッチパネル(Tab5)は通常モードでも要る。
// 画面から切り離して模擬で確かめる。案内だけで、押せる・押せないは変えない。
internal enum As100201NextStepKind
{
    // 人が何かをしないと進まない(赤)
    Action,
    // 待てば進む(処理中・接続確認中、青)
    Wait,
    // 測定を始められる・測定中(緑)
    Ready
}

internal sealed record As100201NextStepState
{
    public bool Learning { get; init; }
    public bool CheckingConnections { get; init; }
    public bool LoggerAppMissing { get; init; }
    // WAVE LOGGER を初期化し、NR 本体を見つけた
    public bool LoggerReady { get; init; }
    public bool Wf1973Ready { get; init; }
    public bool TemplateLoaded { get; init; }
    // タッチパネル(版 6 から通常モードでも要る)
    public bool PanelReady { get; init; }
    // 学習モードだけ
    public bool SaveBlocked { get; init; }
    // 参照波形を読み込めた(2026-11-26)。読み込めなかった理由は ReferenceProblem。
    public bool ReferenceLoaded { get; init; }
    public string? ReferenceProblem { get; init; }
    public bool SerialMissing { get; init; }
    public bool SerialDialogOpen { get; init; }
    public bool PanelSelecting { get; init; }
    // 接続確認以外の処理中・位相の出し直し中
    public bool Busy { get; init; }
    public bool Measuring { get; init; }
    // 測定の準備ができている(版 5 までの READY LED が点く条件)
    public bool ReadyToMeasure { get; init; }
}

internal static class As100201NextStep
{
    public const string Prefix = "次にすること：";

    public static (string Text, As100201NextStepKind Kind) Describe(As100201NextStepState s)
    {
        // 測定中は止め方だけ出す(PC の画面の「測定停止」は保存の停止中・パネルが途切れていても受け付ける)。
        if (s.Measuring)
            return (s.Learning
                ? "測定中です。手を動かす前にタッチパネルの「バルブを動かす」を押してください（止まります。止めるだけなら「測定停止」）"
                : "測定中です。止めるときはタッチパネルか PC の画面の「測定停止」", As100201NextStepKind.Ready);
        if (s.CheckingConnections)
            return ("接続を確認しています。終わるまでお待ちください", As100201NextStepKind.Wait);
        if (s.LoggerAppMissing)
            return ("WAVE LOGGER がこの PC に入っていません。入れてからアプリを起動し直してください", As100201NextStepKind.Action);
        if (!s.LoggerReady)
            return ("NR-500 / NR-X の USB と電源を確かめて、「確認」を押してください", As100201NextStepKind.Action);
        if (!s.Wf1973Ready)
            return ("発振器（WF1973）の USB と電源を確かめて、「確認」を押してください", As100201NextStepKind.Action);
        if (!s.PanelReady)
            return ("タッチパネル（Tab5）の電源と、USB か Wi-Fi を確かめてください（自動でつながり直します）", As100201NextStepKind.Action);
        if (s.Learning && s.SaveBlocked)
            return ("台帳か xdt を保存できません。保存先を直して「再試行」を押してください", As100201NextStepKind.Action);
        if (!s.TemplateLoaded)
            return (s.Learning
                ? "雛形を読み込めませんでした。雛形（xdt）を下の枠にドラッグしてください"
                : "雛形（xdt）を下の枠にドラッグしてください", As100201NextStepKind.Action);
        // 学習モードの参照波形(2026-11-26)。読み込み中は下の「処理中」、読み込めなかったら直し方。
        if (s.Learning && !s.ReferenceLoaded && s.ReferenceProblem is not null)
            return ("参照波形を読み込めません。「雛形」フォルダーに参照波形（.xyb）を 1 つ置いて「確認」を押してください（" + s.ReferenceProblem + "）",
                As100201NextStepKind.Action);
        if (s.Learning && !s.ReferenceLoaded)
            return ("参照波形を読み込んでいます。終わるまでお待ちください", As100201NextStepKind.Wait);
        if (s.Learning && s.SerialDialogOpen)
            return ("シリアル番号の確認の窓に答えてください", As100201NextStepKind.Action);
        if (s.Learning && s.SerialMissing)
            return ("シリアル番号を入れて「決定」を押してください（入れるまで測定できません）", As100201NextStepKind.Action);
        if (s.Learning && s.PanelSelecting)
            return ("タッチパネルで操作を選び終えるか、「戻る」で 1 画面目に戻してください", As100201NextStepKind.Action);
        if (s.Busy)
            return ("処理中です。終わるまでお待ちください", As100201NextStepKind.Wait);
        if (!s.ReadyToMeasure)
            // 条件はそろっているのに、まだ準備に入っていない(見張りが次の周期で確かめ直す)。
            return ("測定の準備をしています。終わるまでお待ちください", As100201NextStepKind.Wait);
        return (s.Learning
            ? "準備完了です。タッチパネルの PC / CR を押すと、位相を出し直して測り始めます（手を動かす前に「バルブを動かす」）"
            : "準備完了です。タッチパネルの PC / CR を押すと、位相を出し直して測り始めます", As100201NextStepKind.Ready);
    }
}
