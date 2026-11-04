using PSCAutomation.Apps.AS110NoiseTest.Analysis;

namespace PSCAutomation.Apps.AS110NoiseTest.Excel;

// 1 回の記録(1 つの電流)の結果。NoiseResultWorkbook.Write に渡す。
internal sealed record NoiseResultRecord(
    DateTime MeasuredAt,           // 記録日時(PP値シートの行は日付で分ける)
    string SerialNumber,           // SN
    string Memo,                   // 備忘録(空でもよい)
    double CommandMa,              // 指令電流 [mA]。雛形の列(1〜35 の整数)に一致する必要がある
    double? MeasuredMa,            // 記録前に 7351E で読んだ実電流 [mA]。無ければ null
    XdtNoiseAnalysis Analysis,     // xdt から求めた統計と判定
    string XdtFileName);           // 保存した xdt のファイル名(詳細シートに残す)
