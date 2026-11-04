namespace PSCAutomation.Apps.XdtConversion;

// 元: XdtConversion XdtConversion/Program.cs。変更は namespace のみ。

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
