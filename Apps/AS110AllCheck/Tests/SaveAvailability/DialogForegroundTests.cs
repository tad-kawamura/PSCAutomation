using System.Reflection;
using System.Windows.Forms;

internal static class DialogForegroundTests
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    // Zオーダーを上から順にたどり、ダイアログが所有者より前にあるか確認する。
    private static bool IsAbove(IntPtr window, IntPtr other)
    {
        for (IntPtr next = GetWindow(window, 2); next != IntPtr.Zero; next = GetWindow(next, 2)) // GW_HWNDNEXT
            if (next == other) return true;
        return false;
    }

    private static void ShowCalibration(Form owner)
    {
        var assembly = Assembly.Load("AS110AllCheck");
        var calibrationType = assembly.GetType("PSCAutomation.Apps.AS110AllCheck.As110Calibration")!;
        var current = new PSCAutomation.Wrappers.LoggerFiles.ScalingPoints(0, 0, 1, 30);
        var pressure = new PSCAutomation.Wrappers.LoggerFiles.ScalingPoints(0, 0, 5, 539);
        object initial = Activator.CreateInstance(calibrationType, current, pressure)!;
        using var dialog = (Form)Activator.CreateInstance(assembly.GetType("PSCAutomation.Apps.AS110AllCheck.CalibrationDialog")!,
            "NR-500", "AS110データ調整・データ取り用.xdt", initial)!;
        dialog.ShowDialog(owner);
    }

    // MainFormを表示すると機器の初期化が始まる環境向け。準備画面と同じ最前面の所有者で校正画面だけを確認する。
    public static void RunCalibrationOnly()
    {
        using var owner = new Form { TopMost = true, Text = "TopMost owner", Size = new System.Drawing.Size(900, 700),
            StartPosition = FormStartPosition.CenterScreen };
        owner.Show();
        Application.DoEvents();
        string? failure = null;
        int ticks = 0;
        bool dialogSeen = false;
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            var dialog = owner.OwnedForms.SingleOrDefault(f => f.Modal);
            if (dialog is null) { if (++ticks > 30) { Console.WriteLine("FAIL calibration dialog did not open"); Environment.Exit(1); } return; }
            dialogSeen = true;
            bool above = IsAbove(dialog.Handle, owner.Handle);
            Console.WriteLine($"INFO calibration dialog TopMost={dialog.TopMost} aboveOwner={above}");
            if (!dialog.TopMost) failure = "Calibration dialog is not topmost";
            else if (!above) failure = "Calibration dialog is behind its topmost owner";
            timer.Stop();
            dialog.DialogResult = DialogResult.Cancel;
        };
        timer.Start();
        ShowCalibration(owner);
        // 画面を 1 度も見ないまま戻ったら、何も確かめていないので失敗にする(2026-11-20)。
        if (!dialogSeen) failure = "Calibration dialog returned without opening";
        if (failure is not null) { Console.WriteLine("FAIL " + failure); Environment.Exit(1); }
        Console.WriteLine("PASS calibration dialog opens topmost above the topmost preparation screen");
    }

    public static void Run()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = Assembly.Load("AS110AllCheck").GetType("PSCAutomation.Apps.AS110AllCheck.MainForm")!;
        using var owner = (Form)Activator.CreateInstance(type)!;
        void Set(string name, object value) => type.GetField(name, flags)!.SetValue(owner, value);
        void Call(string name) => type.GetMethod(name, flags)!.Invoke(owner, null);
        // 実機接続を開始せずに実際のMainFormとダイアログを表示する。
        Set("_operationInProgress", true);
        ((System.Windows.Forms.Timer)type.GetField("_statusHeartbeat", flags)!.GetValue(owner)!).Stop();
        owner.Show();
        // 画面が出たあとの処理(OnShown。最後に接続確認を始める)は、Show から戻ったあとのメッセージ処理で遅れて動く。
        // 処理中の印を下ろす前にここで動かして、接続確認を始めさせない(2026-11-20)。先に印を下ろすと、最初のダイアログを出している間に
        // 接続確認が始まり、画面を閉じたあとで終わって、閉じた画面のログ欄に書こうとして例外の画面(続行ボタン)で検査が止まった。
        Application.DoEvents();
        Set("_operationInProgress", false);
        // 保護解除の画面は保存先の完了記録から一覧を作るので、完了済み S/N 2 つと解除済み S/N 1 つを持つ一時の保存先を用意する。
        string saveRoot = Path.Combine(Path.GetTempPath(), "AS110AllCheck-release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(saveRoot);
        File.WriteAllText(Path.Combine(saveRoot, "完了記録.txt"), $"COMPLETE\t{DateTime.Now:O}\t3\tAS110-035\ta\nCOMPLETE\t{DateTime.Now:O}\t1\tAS110-035\tb\nCOMPLETE\t{DateTime.Now:O}\t2\tAS110-035\tc\nRELEASE\t{DateTime.Now:O}\t2\t-\t-\n");
        // 記録に無いデータ(S/N 5)も一覧に「記録なし」で出る。
        Directory.CreateDirectory(Path.Combine(saveRoot, "PDF"));
        File.WriteAllText(Path.Combine(saveRoot, "PDF", "AS110-035_SN5_20261106_6500.pdf"), "");
        ((TextBox)type.GetField("_rootBox", flags)!.GetValue(owner)!).Text = saveRoot;
        try
        {
            foreach (bool topMost in new[] { true, false })
            foreach (string method in new[] { "ReleaseCompletedSerial", "EditTransferSettings", "CalibrationDialog" })
            {
                owner.TopMost = topMost;
                Exception? failure = null;
                // 「まだ開いていない」回数(waited)と「開いてから確かめた」回数(checks)は別に数える(2026-11-20)。
                // 1 つの変数で数えていたときは、最初の確認にダイアログが間に合わないと「最初の 1 回だけの確かめ」
                // (S/N の一覧・入力欄)を飛ばし、前面要求も 3 回より減ったまま PASS と出る形だった。
                int waited = 0, checks = 0;
                bool dialogSeen = false;
                using var timer = new System.Windows.Forms.Timer { Interval = 100 };
                timer.Tick += (_, _) =>
                {
                    var dialog = owner.OwnedForms.SingleOrDefault(f => f.Modal);
                    // 見つけたダイアログが閉じたあとは数えない(閉じてから呼び出しが戻るまでの間に「開かなかった」と数えないため)。
                    if (dialog is null && dialogSeen) return;
                    if (dialog is null)
                    {
                        if (++waited > 30)
                        {
                            Console.WriteLine("FAIL Dialog did not open: " + method);
                            // ここで終わると finally が動かないので、先に実 MainForm を終了処理を通して閉じ、一時の保存先も消す(2026-11-20)。
                            // 閉じずに終わると、窓なしの WAVE LOGGER(NRWL.exe)が 1 つ残った。
                            new MainFormCloser(owner).Dispose();
                            try { Directory.Delete(saveRoot, true); } catch (IOException) { }
                            Environment.Exit(1);
                        }
                        return;
                    }
                    dialogSeen = true;
                    try
                    {
                        if (!dialog.TopMost) throw new Exception("Dialog is not topmost");
                        if (!IsAbove(dialog.Handle, owner.Handle)) throw new Exception("Dialog is behind MainForm");
                        if (checks == 0 && method == "ReleaseCompletedSerial")
                        {
                            // S/N が番号順に並び、保護中はチェック付き、解除済み・記録なしはチェックなし。
                            var serials = dialog.Controls.OfType<CheckedListBox>().Single();
                            string shown = string.Join(",", serials.Items.Cast<object>().Select((item, i) => item + (serials.GetItemChecked(i) ? "+" : "-")));
                            if (shown != "S/N 1+,S/N 2（解除済み）-,S/N 3+,S/N 5（記録なし）-")
                                throw new Exception("Release dialog does not list serials with protection state in order: " + shown);
                        }
                        if (checks == 0 && dialog.Controls.OfType<TextBox>().FirstOrDefault() is { } input)
                        {
                            input.Focus();
                            input.Text = "123";
                        }
                        // 状態監視からの前面要求を、モーダルのメッセージループ中に再現する。
                        bool ownerWasTopMost = owner.TopMost;
                        Form? activeBefore = Form.ActiveForm;
                        bool focusedBefore = dialog.ContainsFocus;
                        Call("KeepPreparationInFront");
                        if (owner.TopMost != ownerWasTopMost || Form.ActiveForm != activeBefore || dialog.ContainsFocus != focusedBefore)
                            throw new Exception("MainForm changed modal foreground/focus");
                    }
                    catch (Exception ex) { failure = ex; }
                    if (++checks >= 3 || failure is not null)
                    {
                        timer.Stop();
                        dialog.DialogResult = DialogResult.Cancel;
                    }
                };
                timer.Start();
                // 校正の適用時と同じく、MainFormを所有者として校正画面をモーダル表示する。
                if (method == "CalibrationDialog") ShowCalibration(owner);
                else Call(method);
                // 呼び出しがダイアログを出さずにすぐ戻ると(処理中の印などで何もせず戻る)、タイマーが 1 度も確かめないまま
                // ここへ来て PASS と出ていた。ダイアログを 1 度も見ていなければ失敗にする(2026-11-20)。
                if (!dialogSeen) throw new Exception("Dialog did not open (the call returned without showing it): " + method);
                if (failure is not null) throw new Exception(method + " foreground regression", failure);
                if (owner.TopMost != topMost || !owner.Enabled) throw new Exception("Owner state changed after cancel");
                Console.WriteLine($"PASS {method}: owner TopMost={topMost}, dialog retains focus across foreground requests and cancels");
            }
        }
        finally
        {
            // 終了処理を通して閉じる(2026-11-20)。終了処理を飛ばして閉じると、new MainForm() が始めた LOGGER 本体ソフトの事前起動が破棄されず、
            // 窓なしの WAVE LOGGER(NRWL.exe)が残ることがある(MainFormCloser.cs)。
            new MainFormCloser(owner).Dispose();
            try { Directory.Delete(saveRoot, true); } catch (IOException) { }
        }
    }
}
