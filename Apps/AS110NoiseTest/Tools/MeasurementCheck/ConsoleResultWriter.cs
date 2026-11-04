using PSCAutomation.Apps.AS110NoiseTest.Analysis;
using PSCAutomation.Apps.AS110NoiseTest.Excel;

namespace PSCAutomation.Apps.AS110NoiseTest.Tools.MeasurementCheck;

// Excel を使わずに結果をコンソールへ出す(--no-excel)。
internal sealed class ConsoleResultWriter : INoiseResultWriter
{
    public List<NoiseResultRecord> Records { get; } = [];

    public void WriteSettings(NoiseJudgeSettings settings) =>
        Console.WriteLine($"  [結果] しきい値: 黄 {settings.YellowPeakToPeakKpa} kPa / 台地 {settings.PlateauThresholdKpa} kPa×{settings.PlateauMinSamples} / 低周波比 {settings.LowHighPowerRatioMin} / 段差 {settings.StepMinKpa} kPa t≥{settings.StepMinT}");

    public int Write(NoiseResultRecord record)
    {
        Records.Add(record);
        var a = record.Analysis;
        Console.WriteLine($"  [結果] {record.MeasuredAt:yyyy/MM/dd HH:mm:ss} {record.SerialNumber} {record.Memo} {record.CommandMa}mA(実測 {record.MeasuredMa:F3}) " +
                          $"電流 {a.Current.Mean:F2}mA 供給 {a.SupplyPressure.Mean:F1}kPa 制御 {a.ControlPressure.Mean:F2}kPa p-p {a.Metrics.PeakToPeakKpa:F1} {a.Metrics.Grade} {record.XdtFileName}");
        return 0;
    }

    public void Save() { }
}
