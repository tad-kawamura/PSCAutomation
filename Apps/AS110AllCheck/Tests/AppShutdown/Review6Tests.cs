using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;
using PSCAutomation.Apps.AS110AllCheck;
using PSCAutomation.Wrappers.Arduino;

// 全体見直し・区切り 6「画面の土台」(2026-11-14)の確かめ。機器なし。
// 終了の確認・想定外のエラー・画面ログのファイル・二重起動の防止・表示倍率。
internal static class Review6Tests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Field(object target, string name) => target.GetType().GetField(name, Private)!.GetValue(target);
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

    private static void PumpUntil(Func<bool> completed)
    {
        var timer = Stopwatch.StartNew();
        while (!completed())
        {
            Application.DoEvents();
            if (timer.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Form did not close");
            Thread.Sleep(5);
        }
    }

    public static void Run()
    {
        CloseConfirmationTests();
        UnexpectedErrorTests();
        ScreenLogTests();
        SingleInstanceTests();
        DisplayScaleTests();
    }

    // 機器なしで終了処理まで通せる画面(模擬の発振器と LOGGER)。
    private static MainForm NewForm(List<string> trace, out FakeGenerator generator)
    {
        var form = new MainForm();
        ((System.Windows.Forms.Timer)Field(form, "_statusHeartbeat")!).Stop();
        generator = new FakeGenerator(trace);
        Set(form, "_generator", generator);
        Set(Field(form, "_logger")!, "_application", new ShutdownLogger(trace));
        _ = form.Handle;
        return form;
    }

    private static void CloseQuietly(MainForm form)
    {
        Set(form, "_confirmClose", (Func<string, bool>)(_ => true));
        form.Close();
        PumpUntil(() => form.IsDisposed);
    }

    // 終了の確認: 測定の途中・保存や完了をしていない結果があるときだけ聞き、「いいえ」なら何も止めずに続ける。
    private static void CloseConfirmationTests()
    {
        var model = ProductModel.All[0];
        var cases = new (string Name, Action<ProductionWorkflow> Prepare, string? Expected)[]
        {
            ("production idle", f => { f.Mode = TestMode.Production; f.Configure("100", model); }, null),
            ("adjustment result", f => { f.Mode = TestMode.Adjustment; f.ConfigureAdjustment(model); f.BeginMeasurement(DateTime.Now); f.CaptureSucceeded(); }, null),
            ("motion result", f => { f.Mode = TestMode.Motion; f.BeginMeasurement(DateTime.Now); f.CaptureSucceeded(); }, null),
            ("measuring", f => { f.Mode = TestMode.Adjustment; f.ConfigureAdjustment(model); f.BeginMeasurement(DateTime.Now); }, "測定の途中"),
            ("unsaved result", f => { f.Mode = TestMode.Production; f.Configure("100", model); f.BeginMeasurement(DateTime.Now); f.CaptureSucceeded(); }, "S/N 100 の測定結果を、まだ保存していません"),
            ("transferred result", f => { f.Mode = TestMode.Production; f.Configure("100", model); f.BeginMeasurement(DateTime.Now); f.CaptureSucceeded(); f.TransferSucceeded(); }, "まだ保存していません"),
            ("capture failed", f => { f.Mode = TestMode.Production; f.Configure("100", model); f.BeginMeasurement(DateTime.Now); f.CaptureError(); }, "一時保存に失敗したまま"),
            ("save failed", f => { f.Mode = TestMode.Production; f.Configure("100", model); f.BeginMeasurement(DateTime.Now); f.CaptureSucceeded(); f.TransferSucceeded(); f.SaveFailed = true; }, "保存に失敗したまま"),
            ("saved, not completed", f => { f.Mode = TestMode.Production; f.Configure("100", model); f.BeginMeasurement(DateTime.Now); f.CaptureSucceeded(); f.TransferSucceeded(); f.SaveSucceeded(); }, "「合格」（完了）をまだ押していません"),
        };
        foreach (var (name, prepare, expected) in cases)
        {
            var trace = new List<string>();
            using var form = NewForm(trace, out _);
            prepare((ProductionWorkflow)Field(form, "_flow")!);
            var asked = new List<string>();
            bool answer = false;
            Set(form, "_confirmClose", (Func<string, bool>)(w => { asked.Add(w); return answer; }));
            form.Close();
            if (expected is null)
            {
                PumpUntil(() => form.IsDisposed);
                Check(asked.Count == 0, name + ": close asked although nothing would be lost: " + string.Join("|", asked));
                Console.WriteLine($"PASS close confirmation: {name} closes without asking");
                continue;
            }
            var wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 200) { Application.DoEvents(); Thread.Sleep(5); }
            Check(asked.Count == 1 && asked[0].Contains(expected), name + ": wrong question: " + string.Join("|", asked));
            Check(!form.IsDisposed && form.Enabled && !(bool)Field(form, "_closeRequested")! && !(bool)Field(form, "_shutdownStarted")! && trace.Count == 0,
                name + ": answering No must leave the app running and untouched: " + string.Join(",", trace));
            Check(((TextBox)Field(form, "_logTextBox")!).Text.Contains("アプリ終了：取りやめました"), name + ": cancelled close was not logged");
            answer = true;
            form.Close();
            PumpUntil(() => form.IsDisposed);
            Check(asked.Count == 2 && trace.Contains("generator-stop"), name + ": answering Yes must close: asked=" + asked.Count);
            Console.WriteLine($"PASS close confirmation: {name} asks, No keeps running, Yes closes");
        }

        // 実際の確認の画面: 既定は「いいえ」(Enter・Esc・× では終了しない)。
        var dialogTrace = new List<string>();
        using var owner = NewForm(dialogTrace, out _);
        foreach (string key in new[] { "enter", "escape", "close", "yes" })
        {
            string seen = "";
            using var presser = new System.Windows.Forms.Timer { Interval = 50 };
            presser.Tick += (_, _) =>
            {
                var dialog = Application.OpenForms.OfType<DefaultNoDialog>().FirstOrDefault(d => d.Visible);
                if (dialog is null) return;
                presser.Stop();
                seen = dialog.Text + "|" + (ReferenceEquals(dialog.AcceptButton, dialog.NoButton) && ReferenceEquals(dialog.CancelButton, dialog.NoButton));
                if (key == "enter") dialog.AcceptButton!.PerformClick();
                else if (key == "escape") dialog.CancelButton!.PerformClick();
                else if (key == "close") dialog.Close();
                else dialog.YesButton.PerformClick();
            };
            presser.Start();
            bool result = (bool)Call(owner, "AskClose", "測定の途中です。")!;
            Check(seen == "アプリ終了の確認|True", key + ": close dialog must default to No: " + seen);
            Check(result == (key == "yes"), key + ": close dialog returned " + result);
        }
        Console.WriteLine("PASS close confirmation dialog: Enter / Esc / x do not close the app, only Yes does");
        CloseQuietly(owner);
    }

    // 想定外のエラー: 発振器の出力 OFF を試し、ログに残して、日本語で知らせる。
    private static void UnexpectedErrorTests()
    {
        var original = UnexpectedErrors.Notify;
        var shown = new List<string>();
        try
        {
            var trace = new List<string>();
            using var form = NewForm(trace, out var generator);
            UnexpectedErrors.Notify = (_, text, caption) => shown.Add(caption + "\n" + text);
            Set(form, "_leakOutputReady", true);
            UnexpectedErrors.HandleOnScreenThread(new InvalidOperationException("boom-1"));
            Check(trace.Count(t => t == "generator-stop") == 1 && !(bool)Field(form, "_leakOutputReady")!, "Unexpected error did not send output OFF");
            Check(shown.Count == 1 && shown[0].StartsWith("想定外のエラー\n") && shown[0].Contains("boom-1") && shown[0].Contains("発振器へ出力OFFを送信しました") && shown[0].Contains("起動し直してください"),
                "Unexpected error message: " + string.Join("|", shown));
            Check(!form.IsDisposed && ((TextBox)Field(form, "_logTextBox")!).Text.Contains("想定外のエラー：InvalidOperationException / boom-1"), "Unexpected error was not logged on screen");

            // 出力 OFF を送れなかったときは、発振器本体で止めるよう知らせる。
            shown.Clear(); generator.FailStop = true; Set(form, "_leakOutputReady", true);
            UnexpectedErrors.HandleOnScreenThread(new InvalidOperationException("boom-2"));
            Check(shown.Count == 1 && shown[0].Contains("出力OFFを確認できません") && shown[0].Contains("発振器本体で出力をOFF") && (bool)Field(form, "_generatorOutputUnknown")!,
                "Failed output OFF was not reported: " + string.Join("|", shown));

            // 知らせを出している間の同じエラーは、画面を重ねない。
            shown.Clear(); generator.FailStop = false;
            UnexpectedErrors.Notify = (_, text, _) => { shown.Add(text); UnexpectedErrors.HandleOnScreenThread(new Exception("nested")); };
            UnexpectedErrors.HandleOnScreenThread(new Exception("boom-3"));
            Check(shown.Count == 1, "Nested unexpected errors stacked " + shown.Count + " messages");

            // 画面以外のスレッドの例外(アプリは終了する)。別のスレッドから出力 OFF を送り、終了すると知らせる。
            shown.Clear();
            UnexpectedErrors.Notify = (_, text, _) => shown.Add(text);
            int before = trace.Count(t => t == "generator-stop");
            var thread = new Thread(() => UnexpectedErrors.HandleFatal(new InvalidOperationException("boom-4")));
            thread.Start(); thread.Join();
            Check(trace.Count(t => t == "generator-stop") == before + 1 && shown.Count == 1 && shown[0].Contains("アプリを終了します") && shown[0].Contains("boom-4") && shown[0].Contains("発振器へ出力OFFを送信しました"),
                "Fatal error handling: " + string.Join("|", shown));

            string log = File.ReadAllText(ArduinoDiagnosticLog.PathName);
            Check(log.Contains("[APP_ERROR]") && log.Contains("boom-1") && log.Contains("boom-4"), "Unexpected errors were not persisted to the incident log");
            ScreenLogFile.Default.Flush(TimeSpan.FromSeconds(5));
            string screenLog = File.ReadAllText(ScreenLogFile.Default.LastPath!);
            Check(screenLog.Contains("想定外のエラー / System.InvalidOperationException: boom-1"), "Unexpected errors were not written to the screen log file");
            Console.WriteLine("PASS unexpected errors: output OFF is tried, the error is logged and reported once, the app keeps running on the screen thread");
            CloseQuietly(form);
        }
        finally { UnexpectedErrors.Notify = original; }
    }

    // 画面ログのファイル: 日ごとのファイルに全部残る。画面のログ欄は古い行を外す。
    private static void ScreenLogTests()
    {
        string root = Path.Combine(Path.GetTempPath(), "AS110AllCheck-screenlog-" + Guid.NewGuid().ToString("N"));
        try
        {
            string primary = Path.Combine(root, "Logs"), fallback = Path.Combine(root, "Fallback");
            var log = new ScreenLogFile(() => primary, () => fallback);
            log.Write(new DateTime(2026, 11, 14, 23, 59, 59, 900), "前の日の行\r\n2 行目");
            log.Write(new DateTime(2026, 11, 15, 0, 0, 0, 100), "次の日の行");
            Check(log.Flush(TimeSpan.FromSeconds(5)), "Screen log was not written");
            string first = Path.Combine(primary, "画面ログ_20261114.txt"), second = Path.Combine(primary, "画面ログ_20261115.txt");
            Check(File.ReadAllText(first) == "2026-11-14 23:59:59.900  前の日の行\\r\\n2 行目" + Environment.NewLine, "First day file: " + File.ReadAllText(first));
            Check(File.ReadAllText(second) == "2026-11-15 00:00:00.100  次の日の行" + Environment.NewLine && log.LastPath == second, "Second day file: " + File.ReadAllText(second));

            // 起動し直しても同じ日のファイルに続けて書く(先頭の印は 1 回だけ)。
            var again = new ScreenLogFile(() => primary, () => fallback);
            again.Write(new DateTime(2026, 11, 15, 8, 0, 0), "起動し直した後の行");
            Check(again.Flush(TimeSpan.FromSeconds(5)), "Screen log was not appended");
            byte[] bytes = File.ReadAllBytes(second);
            Check(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF && File.ReadAllLines(second).Length == 2 && !File.ReadAllText(second)[1..].Contains('﻿'),
                "Appending must keep one file with one BOM");

            // exe の隣に書けないときは、もう 1 つの置き場に書く。
            string blocked = Path.Combine(root, "blocked"); File.WriteAllText(blocked, "x");
            var moved = new ScreenLogFile(() => blocked, () => fallback);
            moved.Write(new DateTime(2026, 11, 15, 9, 0, 0), "別の置き場の行");
            Check(moved.Flush(TimeSpan.FromSeconds(5)) && File.ReadAllText(Path.Combine(fallback, "画面ログ_20261115.txt")).Contains("別の置き場の行"), "Fallback folder was not used");

            // どこにも書けない間は待たせず(Flush は期限で戻る)、ためておく。
            var stuck = new ScreenLogFile(() => blocked, () => blocked);
            var timer = Stopwatch.StartNew();
            stuck.Write(DateTime.Now, "書けない行");
            Check(timer.ElapsedMilliseconds < 200 && !stuck.Flush(TimeSpan.FromMilliseconds(300)), "An unwritable log must not block or report success");
            Console.WriteLine("PASS screen log file: one file per day, appended across restarts, falls back when the folder is unwritable");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }

        // 実際の画面: 出した行は全部ファイルに残り、ログ欄は古い行を外す。
        var trace = new List<string>();
        using var form = NewForm(trace, out _);
        string mark = "trim-" + Guid.NewGuid().ToString("N");
        for (int i = 1; i <= 2100; i++) Call(form, "AppendLog", $"{mark}-{i:0000}");
        var box = (TextBox)Field(form, "_logTextBox")!;
        string[] lines = box.Lines.Where(l => l.Length > 0).ToArray();
        Check(lines.Length is > 1000 and <= 1200 && lines[0].Contains("これより前のログは") && lines[^1].EndsWith(mark + "-2100") && !box.Text.Contains(mark + "-0001"),
            $"Screen log was not trimmed: {lines.Length} lines / first={lines[0]} / last={lines[^1]}");
        Check(ScreenLogFile.Default.Flush(TimeSpan.FromSeconds(5)), "Screen log file was not flushed");
        string path = ScreenLogFile.Default.LastPath!;
        string file = File.ReadAllText(path);
        Check(Path.GetFileName(path) == ScreenLogFile.FileName(DateTime.Now) && file.Contains(mark + "-0001") && file.Contains(mark + "-2100"), "Trimmed lines must stay in the file");
        Console.WriteLine($"PASS screen log: 2100 lines are all in {Path.GetFileName(path)}, the screen keeps the latest {lines.Length - 1}");
        CloseQuietly(form);
    }

    // 二重起動の防止: 持っている間は 2 つ目を断り、手放したらまた取れる。
    private static void SingleInstanceTests()
    {
        string name = @"Local\AS110AllCheck-test-" + Guid.NewGuid().ToString("N");
        using (var first = SingleInstance.TryAcquire(name))
        {
            Check(first is not null, "First instance was refused");
            Check(SingleInstance.TryAcquire(name) is null, "Second instance was not refused");
        }
        using var afterExit = SingleInstance.TryAcquire(name);
        Check(afterExit is not null, "A new instance was refused after the first one ended");
        Console.WriteLine("PASS single instance: a second start is refused while the first is running");
    }

    // 表示倍率: 画面の大きさ・位置を Windows の倍率ぶん拡大する。
    private static void DisplayScaleTests()
    {
        var trace = new List<string>();
        using var form = NewForm(trace, out _);
        double scale = form.DeviceDpi / 96.0;
        var area = Screen.PrimaryScreen!.WorkingArea;
        Check(form.AutoScaleMode == AutoScaleMode.Dpi && form.CurrentAutoScaleDimensions.Width == form.DeviceDpi, "Main form does not follow the display scale");
        int expectedWidth = (int)Math.Round(760 * scale);
        Check(form.Width <= area.Width && form.Height <= area.Height && (form.Width == area.Width || Math.Abs(form.ClientSize.Width - expectedWidth) <= 2),
            $"Main form size {form.Size} (client {form.ClientSize}) at scale {scale} on {area.Size}");
        // 窓の枠は拡大しないので、最小の大きさは 680 × 倍率より枠のぶんだけ小さい。
        Check(form.MinimumSize.Width <= 680 * scale + 2 && form.MinimumSize.Width >= 680 * scale - 20 * scale, "Minimum size was not scaled: " + form.MinimumSize);
        using var dialog = new DefaultNoDialog("確認の文", form.Font, "確認");
        Check(Math.Abs(dialog.NoButton.Width - 110 * scale) <= 2 && Math.Abs(dialog.NoButton.Height - 36 * scale) <= 2 && dialog.NoButton.Bottom < dialog.ClientSize.Height && dialog.NoButton.Right < dialog.ClientSize.Width,
            $"Dialog buttons were not scaled: {dialog.NoButton.Bounds} in {dialog.ClientSize}");
        using var judge = new LeakJudgeWindow(10, 1.0, 530, 2, form.Font);
        Check(Math.Abs(judge.ClientSize.Width - 520 * scale) <= 2 && Math.Abs(judge.Left + judge.Width / 2 - (area.Left + area.Width / 2)) <= 2, "Leak judge window was not scaled or centred: " + judge.Bounds);

        // 「次にすること」は操作欄の幅で折り返す(窓を最小の幅にしても右が切れない)。
        var viewport = (Panel)Field(form, "_operationViewport")!;
        var next = (Label)Field(form, "_nextStepLabel")!;
        form.Width = form.MinimumSize.Width;
        Application.DoEvents();
        Check(next.MaximumSize.Width <= viewport.ClientSize.Width && next.MaximumSize.Width > viewport.ClientSize.Width - 40 * scale && next.Right <= viewport.ClientSize.Width,
            $"Next-step label {next.Bounds} (max {next.MaximumSize.Width}) does not fit the operation area {viewport.ClientSize}");
        Console.WriteLine($"PASS display scale {scale:P0}: main form {form.ClientSize}, dialogs and the next-step label follow the scale and the window width");
        CloseQuietly(form);
    }
}
