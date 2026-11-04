namespace PSCAutomation.Apps.AS100201SwitchController;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        As100201Application.ConfigureDiagnostics();
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
