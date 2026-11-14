using System.Runtime.InteropServices;

namespace PSCAutomation.Apps.AS110AllCheck;

// 想定していないエラー(どの処理も受け止めなかった例外)の受け皿(2026-11-14、全体見直し・区切り 6、人の決定)。
// それまでは、画面の処理で出ると .NET 標準の英語の画面(続行/終了)が出て、画面以外のスレッドで出ると何も出さずにアプリが落ちた
// (ログも残らず、発振器の出力もそのまま)。
// ここでは、発振器の出力 OFF を試し、異常ログ(Logs の ArduinoIncident_…)と画面ログに残し、日本語で知らせる。
// 画面の処理で出たときはアプリを落とさない(表示と実際の状態が合わないおそれがあるので、起動し直すよう案内する)。
// 画面以外のスレッドで出たときは .NET がアプリを終わらせるので、その前に同じことをする。
internal static class UnexpectedErrors
{
    // 異常ログの種別(ArduinoDiagnosticLog.Configure に渡して、ファイルに保存する種別にする)。
    public const string IncidentKind = "APP_ERROR";

    private static MainForm? _form;
    private static int _handling;

    // 知らせ方(検査では画面を出さずに受け取る)。owner は null のことがある。
    internal static Action<IWin32Window?, string, string> Notify = ShowMessage;

    public static void Install()
    {
        // 画面のスレッドの例外を .NET 標準の画面に任せず、ここで受ける(最初の窓を作る前に決める)。
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => HandleOnScreenThread(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => HandleFatal(e.ExceptionObject as Exception);
        // 結果を誰も見なかった処理の失敗。アプリは落ちないので、ログにだけ残す。
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Record("見ていない処理の失敗", e.Exception);
            e.SetObserved();
        };
    }

    public static void Register(MainForm form) => _form = form;
    public static void Unregister(MainForm form) { if (ReferenceEquals(_form, form)) _form = null; }

    // 画面のスレッドで出た例外。アプリは続くので、出力 OFF を試して知らせる。
    internal static void HandleOnScreenThread(Exception ex)
    {
        // 知らせを出している間に同じ処理(タイマーなど)がまた失敗しても、画面は重ねない(ログには残す)。
        if (Interlocked.Exchange(ref _handling, 1) == 1) { Record("想定外のエラー（続き）", ex); return; }
        try
        {
            Record("想定外のエラー", ex);
            MainForm? form = _form is { IsDisposed: false } alive ? alive : null;
            string output = form?.EmergencyOutputOff(onScreenThread: true) ?? "";
            if (form is not null) try { form.LogUnexpectedError(ex, output); } catch { }
            Notify(form, "想定外のエラーが起きました。\n" +
                (output.Length > 0 ? output + "\n" : "") +
                "\nこのまま使い続けると、画面の表示と実際の状態が合わないおそれがあります。\n" +
                "保存していない測定結果は測り直しになります。「アプリ終了」で終了して、起動し直してください。\n\n" +
                "内容：" + ex.Message + "\n" + LogGuide(), "想定外のエラー");
        }
        catch { }
        finally { Interlocked.Exchange(ref _handling, 0); }
    }

    // 画面以外のスレッドで出た例外。この後 .NET がアプリを終わらせる。
    internal static void HandleFatal(Exception? ex)
    {
        try
        {
            ex ??= new Exception("内容不明のエラー");
            Record("想定外のエラー（アプリを終了します）", ex);
            string output = _form?.EmergencyOutputOff(onScreenThread: false) ?? "";
            if (output.Length > 0) Record("想定外のエラー：発振器", null, output);
            try { ArduinoDiagnosticLog.Write("END", "想定外のエラーで終了"); ArduinoDiagnosticLog.FlushOnExit(); } catch { }
            ScreenLogFile.Default.Flush(TimeSpan.FromSeconds(2));
            Notify(null, "想定外のエラーが起きたため、アプリを終了します。\n" +
                (output.Length > 0 ? output + "\n" : "") +
                "\nWAVE LOGGER と Excel の画面が残っていたら、閉じてから起動し直してください。\n" +
                "保存していない測定結果は測り直しになります。\n\n" +
                "内容：" + ex.Message + "\n" + LogGuide(), "想定外のエラー");
        }
        catch { }
    }

    private static void Record(string title, Exception? ex, string? detail = null)
    {
        string text = title + " / " + (detail ?? ex?.ToString() ?? "");
        try { ArduinoDiagnosticLog.Write(IncidentKind, text); } catch { }
        try { ScreenLogFile.Default.Write(DateTime.Now, text); } catch { }
    }

    private static string LogGuide() =>
        "ログ：" + ArduinoDiagnosticLog.PrimaryDirectory + " の「" + ScreenLogFile.FilePrefix + "日付.txt」と「ArduinoIncident_…」";

    private const uint MbIconError = 0x10, MbSetForeground = 0x00010000, MbTopmost = 0x00040000;

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NativeMessageBox(IntPtr owner, string text, string caption, uint type);

    // LOGGER・Excel の後ろに隠れないよう最前面で出す。持ち主の窓が無くても(別スレッドからでも)出せる。
    private static void ShowMessage(IWin32Window? owner, string text, string caption)
    {
        IntPtr handle = IntPtr.Zero;
        try { if (owner is Control { IsHandleCreated: true, InvokeRequired: false } control) handle = control.Handle; } catch { }
        NativeMessageBox(handle, text, caption, MbIconError | MbSetForeground | MbTopmost);
    }
}

// 二重起動を止める(2026-11-14、全体見直し・区切り 6、人の決定)。起動に数秒かかるので 2 回ダブルクリックすると 2 つ立ち上がり、
// 2 つ目の接続確認が、1 つ目が使っている LOGGER・M5Stack・発振器に触るおそれがあった。
// 版の違う exe(データ調整ver3.01 と ver3.02)どうしも同時には動かさない(同じ機器を使うため)。
internal static class SingleInstance
{
    public const string Name = @"Local\PSCAutomation.AS110AllCheck";

    // 最初の 1 つなら、持っている間ほかの起動を止める印を返す(終了するときに Dispose)。すでに起動していたら null。
    public static IDisposable? TryAcquire(string name = Name)
    {
        var mutex = new Mutex(false, name, out bool createdNew);
        if (createdNew) return mutex;
        mutex.Dispose();
        return null;
    }
}
