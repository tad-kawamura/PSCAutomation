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
            // すでに起動していたら、知らせて終わる(機器・LOGGER に触る前に確かめる)。
            using IDisposable? instance = SingleInstance.TryAcquire();
            if (instance is null)
            {
                // 起動している画面や LOGGER の後ろに隠れないよう、最前面の持ち主(画面には出さない)を付けて出す。
                using var owner = new Form { TopMost = true, ShowInTaskbar = false };
                MessageBox.Show(owner,
                    "このアプリはすでに起動しています。\nタスクバーから、起動している画面を選んでください。\n\n" +
                    "直前に終了したばかりのときは、終了処理(LOGGER の終了)がまだ続いています。数秒待ってから起動し直してください。",
                    Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            // 通信・診断ログより先に、異常ログの保存先と AS110 固有の異常種別を決める。
            As110Application.ConfigureDiagnostics();
            // 想定外のエラーを受ける(最初の窓を作る前に決める)。
            UnexpectedErrors.Install();
            Application.Run(new MainForm());
        }
    }
}
