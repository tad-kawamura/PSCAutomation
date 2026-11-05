namespace PSCAutomation.Apps.AS110AllCheck;

// AS110専用のスタンド一覧とCH1電流スケーリング。
// スタンド名と設定値を1か所にまとめる。
// XDTのGUI校正の初期値、およびYDTの既存設定に使用する公称値。
// XDTの校正では1点目の電圧・電流も編集できる。
// 他のプロジェクトではXdtScalingEditorへ任意の2点を渡せる。
// 入力はV、換算値は雛形で設定したmA単位を使う。
internal sealed record As110StandSettings(string Name, float InputVoltage, float CurrentMilliAmps)
{
    public static IReadOnlyList<As110StandSettings> All { get; } =
    [
        new("４方弁用スタンド", 5f, 30f),
        new("門馬さんの隣のスタンド", 1f, 30f) { HorizontalZoomSteps = 2 },
        new("増産用のスタンド", 3f, 30f)
    ];

    public int HorizontalZoomSteps { get; init; }

    public string Summary => $"{Name}：0 V＝0 mA／{InputVoltage:g} V＝{CurrentMilliAmps:g} mA";
    public void Apply(XdtScalingEditor editor) =>
        editor.SetScaling(channel: 1, input1: 0f, output1: 0f,
            input2: InputVoltage, output2: CurrentMilliAmps);
}
