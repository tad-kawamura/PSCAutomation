using PSCAutomation.Apps.AS110NoiseTest.Ui;

namespace PSCAutomation.Apps.AS110NoiseTest;

// AS110 ノイズ測定の画面を開く(Ui/MainForm。仕様は specs/AS110NoiseTest/SPEC.md「画面」)。
//   AS110NoiseTest.exe          実機(WF1973・7351E・NR-500)で測る
//   AS110NoiseTest.exe --sim    機器を使わずに模擬で測る(画面から Excel まで通す確認用。Excel は本物に書く)
// 確認ツールは Tools/MeasurementCheck(シーケンス)、Tools/CurrentControlCheck(電流制御)。
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        bool simulation = args.Any(a => string.Equals(a, "--sim", StringComparison.OrdinalIgnoreCase));
        Application.Run(new MainForm(simulation));
    }
}
