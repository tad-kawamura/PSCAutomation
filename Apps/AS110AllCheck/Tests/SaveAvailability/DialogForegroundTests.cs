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
            "テストスタンド", "NR-500", initial)!;
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
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            var dialog = owner.OwnedForms.SingleOrDefault(f => f.Modal);
            if (dialog is null) { if (++ticks > 30) { Console.WriteLine("FAIL calibration dialog did not open"); Environment.Exit(1); } return; }
            bool above = IsAbove(dialog.Handle, owner.Handle);
            Console.WriteLine($"INFO calibration dialog TopMost={dialog.TopMost} aboveOwner={above}");
            if (!dialog.TopMost) failure = "Calibration dialog is not topmost";
            else if (!above) failure = "Calibration dialog is behind its topmost owner";
            timer.Stop();
            dialog.DialogResult = DialogResult.Cancel;
        };
        timer.Start();
        ShowCalibration(owner);
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
        Set("_operationInProgress", false);
        // 保護解除の画面は保存先の完了記録から一覧を作るので、完了済み S/N を 2 つ持つ一時の保存先を用意する。
        string saveRoot = Path.Combine(Path.GetTempPath(), "AS110AllCheck-release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(saveRoot);
        File.WriteAllText(Path.Combine(saveRoot, "完了記録.txt"), $"COMPLETE\t{DateTime.Now:O}\t3\tAS110-035\ta\nCOMPLETE\t{DateTime.Now:O}\t1\tAS110-035\tb\n");
        ((TextBox)type.GetField("_rootBox", flags)!.GetValue(owner)!).Text = saveRoot;
        try
        {
            foreach (bool topMost in new[] { true, false })
            foreach (string method in new[] { "ReleaseCompletedSerial", "EditTransferSettings", "CalibrationDialog" })
            {
                owner.TopMost = topMost;
                Exception? failure = null;
                int ticks = 0;
                using var timer = new System.Windows.Forms.Timer { Interval = 100 };
                timer.Tick += (_, _) =>
                {
                    var dialog = owner.OwnedForms.SingleOrDefault(f => f.Modal);
                    if (dialog is null)
                    {
                        if (++ticks > 30) { Console.WriteLine("FAIL Dialog did not open: " + method); Environment.Exit(1); }
                        return;
                    }
                    try
                    {
                        if (!dialog.TopMost) throw new Exception("Dialog is not topmost");
                        if (!IsAbove(dialog.Handle, owner.Handle)) throw new Exception("Dialog is behind MainForm");
                        if (ticks == 0 && method == "ReleaseCompletedSerial")
                        {
                            // 保護中の S/N が番号順に全部チェック付きで並ぶ。
                            var serials = dialog.Controls.OfType<CheckedListBox>().Single();
                            if (serials.Items.Count != 2 || serials.Items[0]!.ToString() != "S/N 1" || serials.Items[1]!.ToString() != "S/N 3" || serials.CheckedItems.Count != 2)
                                throw new Exception("Release dialog does not list completed serials checked in order: " + string.Join(",", serials.Items.Cast<object>()));
                        }
                        if (ticks == 0 && dialog.Controls.OfType<TextBox>().FirstOrDefault() is { } input)
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
                    if (++ticks >= 3 || failure is not null)
                    {
                        timer.Stop();
                        dialog.DialogResult = DialogResult.Cancel;
                    }
                };
                timer.Start();
                // スタンド選択時と同じく、MainFormを所有者として校正画面をモーダル表示する。
                if (method == "CalibrationDialog") ShowCalibration(owner);
                else Call(method);
                if (failure is not null) throw new Exception(method + " foreground regression", failure);
                if (owner.TopMost != topMost || !owner.Enabled) throw new Exception("Owner state changed after cancel");
                Console.WriteLine($"PASS {method}: owner TopMost={topMost}, dialog retains focus across foreground requests and cancels");
            }
        }
        finally
        {
            Set("_shutdownComplete", true);
            owner.Close();
            try { Directory.Delete(saveRoot, true); } catch (IOException) { }
        }
    }
}
