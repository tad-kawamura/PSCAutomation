namespace PSCAutomation.Apps.AS100201SwitchController;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        As100201Application.ConfigureDiagnostics();
        ApplicationConfiguration.Initialize();
        // 起動時に通常モード / 学習モードを選ぶ(× は通常モード)。通常モードは今までと同じ動き。
        As100201StartupMode mode = StartupModeDialog.Ask();
        Application.Run(new MainForm(mode));
    }
}
