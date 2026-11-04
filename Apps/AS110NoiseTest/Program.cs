namespace PSCAutomation.Apps.AS110NoiseTest;

// 測定画面(設定 → 自動ループの操作)は次の段階で作る。シーケンス本体は Measurement/MeasurementSequence.cs。
// 現時点の確認は Tools/CurrentControlCheck(電流制御)と Tools/MeasurementCheck(シーケンス)で行う。
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        MessageBox.Show(
            "測定画面は未実装です。シーケンスの確認は Tools/MeasurementCheck、電流制御の確認は Tools/CurrentControlCheck を使ってください。",
            "AS110NoiseTest", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
