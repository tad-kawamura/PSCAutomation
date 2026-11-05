namespace PSCAutomation.Apps.AS110AllCheck
{
    // アプリの起動入口。
    // Windows Formsの基本設定を初期化する。
    // MainFormを作成し、メイン画面として起動する。
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();
            // 通信・診断ログより先に、異常ログの保存先と AS110 固有の異常種別を決める。
            As110Application.ConfigureDiagnostics();
            Application.Run(new MainForm());
        }
    }
}
