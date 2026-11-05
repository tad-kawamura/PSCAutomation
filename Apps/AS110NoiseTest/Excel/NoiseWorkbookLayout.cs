namespace PSCAutomation.Apps.AS110NoiseTest.Excel;

// 雛形 Excel(雛形/AS110ノイズ測定_雛形.xlsx)の位置。雛形を変えたら Tools/ExcelTemplate/make_template.py と
// 雛形/README.md と一緒にここも直す。
internal static class NoiseWorkbookLayout
{
    public const string PpSheet = "PP値";
    public const string MeanSheet = "平均値";
    public const string RedSheet = "赤判定";
    public const string DetailSheet = "詳細";
    public const string SettingSheet = "設定";

    // PP値・平均値・赤判定で共通(同じ行 = 同じ型式・SN・備忘録)
    public const int HeaderRow = 7;
    public const int FirstDataRow = 8;
    public const int LastSearchRow = 1007;      // 行を探す範囲(雛形の集計・条件付き書式もここまで)
    public const int DateColumn = 1;            // A
    public const int ModelColumn = 2;           // B(2026-11-05 追加)
    public const int SerialColumn = 3;          // C
    public const int MemoColumn = 4;            // D
    public const string MaHeaderName = "PP_Header";   // 名前定義: PP値!E7:AM7(見出しは mA の数値)

    // PP値だけにある列
    public const int MaxPpColumn = 40;          // AN 数式
    public const int YellowCountColumn = 41;    // AO 数式
    public const int RedCountColumn = 42;       // AP 数式
    public const int JudgeColumn = 43;          // AQ 数式(OK / 黄 / 赤)
    public const int NoteColumn = 44;           // AR 人が書く

    public const string RedMark = "赤";         // 赤判定シートに書く文字

    // 詳細シート(1 行目が見出し、2 行目から 1 測定 1 行)
    public const int DetailHeaderRow = 1;
    public const int DetailFirstRow = 2;
    public const int DetailColumnCount = 23;

    // 設定シート(C3〜C8 に判定のしきい値)
    public const int SettingValueColumn = 3;
    public const int SettingYellowRow = 3;
    public const int SettingPlateauKpaRow = 4;
    public const int SettingPlateauSamplesRow = 5;
    public const int SettingLowHighRatioRow = 6;
    public const int SettingStepKpaRow = 7;
    public const int SettingStepTRow = 8;
}
