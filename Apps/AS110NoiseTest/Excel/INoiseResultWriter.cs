using PSCAutomation.Apps.AS110NoiseTest.Analysis;

namespace PSCAutomation.Apps.AS110NoiseTest.Excel;

// 測定シーケンスが結果を書く先。本番は NoiseResultWorkbook(Excel)。
// 機器なしの確認ツールでは Excel を使わずにコンソールへ出す実装に差し替える。
internal interface INoiseResultWriter
{
    // 判定のしきい値を残す(開始時に 1 回)。
    void WriteSettings(NoiseJudgeSettings settings);
    // 1 つの電流の結果を書く。戻り値は PP値シートの行番号(Excel 以外は 0 でよい)。
    int Write(NoiseResultRecord record);
    void Save();
}
