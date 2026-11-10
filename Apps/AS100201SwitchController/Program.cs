namespace PSCAutomation.Apps.AS100201SwitchController;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        As100201Application.ConfigureDiagnostics();
        ApplicationConfiguration.Initialize();
        // 開発用: --virtual-panel で起動すると、起動の窓を出さずに学習モードにし、タッチパネルの代わりを画面に出す
        // (Tab5 が無くても学習モードの PC 側を実機の LOGGER で確かめられる。2026-11-10)。
        if (args.Any(a => string.Equals(a, As100201VirtualPanelLink.CommandLineSwitch, StringComparison.OrdinalIgnoreCase)))
        {
            Application.Run(new MainForm(As100201StartupMode.Learning, virtualPanel: true));
            return;
        }
        // 起動時に通常モード / 学習モード / 学習モード(開発用の代わり)を選ぶ(× は通常モード)。通常モードは今までと同じ動き。
        var (mode, virtualPanel) = StartupModeDialog.Ask();
        Application.Run(new MainForm(mode, virtualPanel));
    }
}
