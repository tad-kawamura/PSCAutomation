namespace PSCAutomation.Apps.AS110AllCheck;

// 型式ごとの測り方(2026-11-12、人と決めた。SPEC「AS110-006 の雛形と測り方」)。
// AS110-006 は、データ調整・データ取りで測る時間をほかの型式の 2 倍にする(過去の AS110-006 と同じ測り方)。
// - 発振器の周波数を半分にする(WF1973 0.04 → 0.02 Hz、SDG1032X 0.08 → 0.04 Hz)。振幅は同じなので電流の傾きが半分になる
// - LOGGER の点数と Excel へ平均する点数を倍にする(6500 → 13000 点、5 → 10 点平均)。Excel へは同じ 1300 点なので、雛形の判定式はほかの型式と同じ
// - LOGGER の雛形は型式専用(「AS110データ調整・データ取り用_AS110-006.xdt」「AS110データ調整・データ取り_NRX100_AS110-006.ydt」)。
//   人が WAVE LOGGER で共用の雛形の点数だけを 13000 にして作る。無ければ止める(6500 点の共用の雛形では測らない)
// 動き検査は型式を使わないので変えない。
internal static class MeasurementProfile
{
    private static readonly Dictionary<string, int> LengthFactors =
        new(StringComparer.OrdinalIgnoreCase) { ["AS110-006"] = 2 };

    // 測る時間の倍率(ふつうは 1)。
    public static int LengthFactor(ProductModel model) =>
        LengthFactors.TryGetValue(model.Name, out int factor) ? factor : 1;

    // 型式専用の LOGGER 雛形を使うか(測る時間が違う型式)。
    public static bool HasOwnLoggerTemplate(ProductModel model) => LengthFactor(model) != 1;

    // データ調整・データ取りの LOGGER 雛形のファイル名(拡張子なし)。型式専用なら共用の名前の後ろに「_型式名」を付ける。
    public static string LoggerTemplateName(string commonName, ProductModel model) =>
        HasOwnLoggerTemplate(model) ? $"{commonName}_{model.Name}" : commonName;
}
