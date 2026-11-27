namespace PSCAutomation.Apps.AS100201SwitchController;

// 学習モードでスイッチを受け付けるかの決まり(SPEC_調整操作の記録.md「異常時」)。画面から切り離して模擬で確かめる。
// 版 6(2026-11-27)で Arduino のスイッチはなくなったが、名前は今までどおり使う(PC_go / CR_go = Tab5 の PC / CR、
// F8 = 測り始める、sw4 = 止める)。readyLedOn は前の READY LED が点く条件(測定の準備ができている)。
//   - sw4(停止)だけは、タッチパネルが途切れていても・台帳や xdt に書けずに止まっていても受け付ける(止めるものがあれば止める)
//   - PC_go / CR_go / F8 は、接続と雛形がそろい、パネルがつながっていて、保存先が決まっていて、保存の停止中でなく、
//     シリアル番号が入っていて、パネルで操作を選んでいる途中でないときだけ
internal static class As100201LearningGate
{
    public const string NotReady = "接続確認と雛形読み込みが完了するまで操作できません。";
    public const string PanelLost = "タッチパネルの通信が途切れているため、測定を始められません（止めるときは PC の画面の「測定停止」）。";
    public const string NoSaveDirectory = "保存先が決まっていないため、学習モードでは測定を始められません。";
    public const string SaveBlocked = "台帳か xdt を保存できるまで測定を始められません（保存先を直して「再試行」を押してください）。";
    public const string PanelSelecting = "タッチパネルで操作を選んでいる途中のため、測定を始められません（選び終わるか「戻る」で 1 画面目に戻ると始められます）。";

    public const string NoSerial = "シリアル番号を入れるまで測定を始められません（PC の画面の「シリアル番号」に入れて「決定」を押してください）。";

    // panelSelecting: パネルが 2 画面目以降(操作を選んでいる途中)にいる(版 4、2026-11-13 人の指示)。選び終わるまで測定を始めない。
    // serialMissing: シリアル番号が入っていない(起動直後・完了のあと。2026-11-26 人の決定)。入れるまで測定を始めない。
    public static bool Allows(string button, bool baseReady, bool panelReady, bool hasSession, bool saveBlocked, bool readyLedOn, out string refusal,
        bool panelSelecting = false, bool serialMissing = false)
    {
        refusal = NotReady;
        if (button == "sw4") return baseReady;
        if (!baseReady) return false;
        if (!panelReady) { refusal = PanelLost; return false; }
        if (!hasSession) { refusal = NoSaveDirectory; return false; }
        if (saveBlocked) { refusal = SaveBlocked; return false; }
        if (serialMissing) { refusal = NoSerial; return false; }
        if (panelSelecting) { refusal = PanelSelecting; return false; }
        return readyLedOn;
    }
}
