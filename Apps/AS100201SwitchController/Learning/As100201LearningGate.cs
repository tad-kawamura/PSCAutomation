namespace PSCAutomation.Apps.AS100201SwitchController;

// 学習モードでスイッチを受け付けるかの決まり(SPEC_調整操作の記録.md「異常時」)。画面から切り離して模擬で確かめる。
//   - sw4(停止)だけは、タッチパネルが途切れていても・台帳や xdt に書けずに止まっていても受け付ける(止めるものがあれば止める)
//   - PC_go / CR_go / F8 は、接続と雛形がそろい、パネルがつながっていて、保存先が決まっていて、保存の停止中でないときだけ
internal static class As100201LearningGate
{
    public const string NotReady = "接続確認と雛形読み込みが完了するまで操作できません。";
    public const string PanelLost = "タッチパネルの通信が途切れているため、測定を始められません（sw4 の停止だけ受け付けます）。";
    public const string NoSaveDirectory = "雛形が未登録のため、学習モードでは測定を始められません（保存先は雛形と同じフォルダー）。";
    public const string SaveBlocked = "台帳か xdt を保存できるまで測定を始められません（保存先を直して「再試行」を押してください）。";

    public static bool Allows(string button, bool baseReady, bool panelReady, bool hasSession, bool saveBlocked, bool readyLedOn, out string refusal)
    {
        refusal = NotReady;
        if (button == "sw4") return baseReady;
        if (!baseReady) return false;
        if (!panelReady) { refusal = PanelLost; return false; }
        if (!hasSession) { refusal = NoSaveDirectory; return false; }
        if (saveBlocked) { refusal = SaveBlocked; return false; }
        return readyLedOn;
    }
}
