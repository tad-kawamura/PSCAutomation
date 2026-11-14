using PSCAutomation.Apps.AS110AllCheck;

// 2026-11-14 の全体見直し(区切り 5「Excel・ファイル・出荷データ」)で直した所。画面・機器・Excel なし。
// - 転送設定は「設定保存」の前に値を確かめる(TransferSettings.FindProblems)
// - ファイルをほかで開いているときの文を日本語にする(FileAccessAdvice。完了記録・保存先)
internal static class FileAndSettingsTests
{
    private static void Check(bool b, string name) { if (!b) throw new Exception(name); Console.WriteLine("PASS " + name); }

    public static void Run()
    {
        // ---- 転送設定の確かめ ----
        Check(new TransferSettings().FindProblems().Count == 0, "default transfer settings have no problems");
        Check(new TransferSettings().ForModel(ProductModel.All.First(m => m.Name == "AS110-006"), TestMode.Production).FindProblems().Count == 0,
            "the doubled settings of AS110-006 have no problems");
        bool Has(TransferSettings s, string part) => s.FindProblems().Any(p => p.Contains(part));
        Check(Has(new() { PressureChannel = 1 }, "同じ（CH1）"), "the same channel for current and pressure is rejected");
        Check(Has(new() { CurrentChannel = 0 }, "1 以上") && Has(new() { PressureChannel = -2 }, "1 以上"), "channels below 1 are rejected");
        Check(Has(new() { Compression = 0 }, "平均する点数") && Has(new() { ExpectedSamples = 0 }, "測定点数は 1 以上") && Has(new() { ExpectedSamples = -5 }, "測定点数は 1 以上"),
            "zero or negative compression and sample count are rejected");
        Check(Has(new() { ExpectedSamples = 6501 }, "割り切れません") && !Has(new() { ExpectedSamples = 13000, Compression = 10 }, "割り切れません"),
            "a sample count not divisible by the compression is rejected");
        Check(Has(new() { NrxCurrentCsvColumn = -1 }, "CSV 列") && Has(new() { NrxCurrentCsvColumn = 3, NrxPressureCsvColumn = 3 }, "同じ（3）")
            && new TransferSettings { NrxCurrentCsvColumn = 2, NrxPressureCsvColumn = 3 }.FindProblems().Count == 0, "NR-X CSV columns: negative or equal are rejected, distinct are accepted");
        Check(Has(new() { DataSheet = " " }, "データ転送先シートが空") && Has(new() { JudgmentSheet = "" }, "判定セルのシートが空"), "empty sheet names are rejected");
        Check(Has(new() { CurrentCell = "4A" }, "電流の開始セル「4A」") && Has(new() { PressureCell = "" }, "圧力の開始セル「」") && Has(new() { PressureCell = "B0" }, "圧力の開始セル「B0」"),
            "start cells that are not cell addresses are rejected");
        Check(Has(new() { PressureCell = "a10" }, "同じ列") && new TransferSettings { CurrentCell = " c4 ", PressureCell = "D4" }.FindProblems().Count == 0,
            "start cells in the same column are rejected; case and spaces are accepted as before");
        Check(Has(new() { CurrentCell = "A1048000" }, "最後の行を超えます"), "a start row that pushes the values past the last row is rejected");
        Check(new TransferSettings { PressureChannel = 1, Compression = 0, CurrentCell = "?" }.FindProblems().Count == 3, "every problem is listed at once");

        // シート名は、いまある Excel 雛形にあるかを確かめる。読めない雛形は飛ばす。
        string? templates = FindTemplateFolder();
        if (templates is null) Console.WriteLine("SKIP sheet names against the Excel templates (雛形 folder not found from the test output)");
        else
        {
            string[] files = Directory.GetFiles(templates, "*雛形.xlsx");
            Check(files.Length >= 5 && new TransferSettings().FindProblems(files).Count == 0, "default sheet names exist in every Excel template");
            var wrong = new TransferSettings { DataSheet = "Sheet1", JudgmentSheet = "判定" }.FindProblems(files);
            Check(wrong.Count == files.Length * 2 && wrong.All(p => p.Contains("にありません") && p.Contains("sheet1")) && wrong.Any(p => p.Contains("AS110-035雛形.xlsx")),
                "sheet names missing from a template are rejected with the template name and its sheets (case-sensitive, as the transfer is)");
            Check(new TransferSettings { DataSheet = "無い" }.FindProblems([Path.Combine(templates, "無い雛形.xlsx")]).Count == 0, "a template that cannot be read is skipped");
        }

        // ---- ほかで開いているファイルの文 ----
        string root = Path.Combine(Path.GetTempPath(), "AS110AllCheck.Verification", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CompletionStore(root);
            using (store.Lock()) store.Append("COMPLETE", "10", "AS110-035", "a");
            string journal = Path.Combine(root, "完了記録.txt");
            using (new FileStream(journal, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                string read = Message(() => store.IsComplete("10")), append = Message(() => store.Append("COMPLETE", "11", "AS110-035", "b"));
                Check(read.Contains("完了記録を開けません") && read.Contains("「完了記録.txt」を Excel やメモ帳で開いていたら閉じてください") && read.Contains(journal) && read.Contains("詳細："),
                    "reading the completion journal while it is open elsewhere explains what to close in Japanese");
                Check(append.Contains("完了記録を開けません") && append.Contains("「完了記録.txt」を Excel やメモ帳で開いていたら閉じてください"), "appending to an open completion journal explains the same");
            }
            Check(store.IsComplete("10") && !store.IsComplete("11"), "the journal is intact after the failed append");
            using (store.Lock())
                Check(Message(() => store.Lock().Dispose()).Contains("もう 1 つ起動して同じ保存先"), "a second lock explains that another instance uses the same folder");

            // 保存の置き換え: 前に保存したファイルを開いたままだと止まる。閉じるファイルの名前と、そのあとすることを出す。
            string from = Path.Combine(root, "result.pdf"), to = Path.Combine(root, "AS110-035_SN10_20261114.pdf");
            File.WriteAllText(from, "new"); File.WriteAllText(to, "old");
            using (new FileStream(to, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                string move = Message(() => FileAccessAdvice.Run("保存先のファイル", to, () => File.Move(from, to, true), "閉じたあと、もう一度「保存」を押してください。", "「AS110-035_SN10_20261114.pdf」を Excel や PDF の画面で開いていたら閉じてください"));
                Check(move.Contains("保存先のファイルを開けません") && move.Contains("「AS110-035_SN10_20261114.pdf」を Excel や PDF の画面で開いていたら閉じてください")
                    && move.Contains("もう一度「保存」") && move.Contains(to), "replacing a saved file that is open elsewhere explains which file to close and to press save again");
            }
            Check(File.ReadAllText(to) == "old" && File.Exists(from), "the open file is not replaced");

            // 案内に包むのは「開けない・書けない」だけ。ファイルが無い・中身が不正・すでに包んだものはそのまま。
            bool Wrapped(Exception ex) { try { FileAccessAdvice.Run("x", "p", () => throw ex); return false; } catch (FileAccessAdviceException) { return true; } catch { return false; } }
            Check(Wrapped(new IOException("a")) && Wrapped(new UnauthorizedAccessException("a")) && Wrapped(new DirectoryNotFoundException("a"))
                && !Wrapped(new FileNotFoundException("a")) && !Wrapped(new InvalidDataException("a")) && !Wrapped(new InvalidOperationException("a")),
                "only access problems are explained; missing files and bad contents keep their own message");
            var once = new FileAccessAdviceException("一度目", new IOException("a"));
            try { FileAccessAdvice.Run("x", "p", () => throw once); } catch (Exception ex) { Check(ReferenceEquals(ex, once), "an explained error is not wrapped twice"); }
            Check(FileAccessAdvice.Run("x", "p", () => 5) == 5, "a successful action returns its value");

            // ---- 正式保存の置き換え: 途中で止まったら元に戻す ----
            string dest = Path.Combine(root, "dest"), stage = Path.Combine(root, "stage");
            Directory.CreateDirectory(dest); Directory.CreateDirectory(stage);
            (string From, string To)[] Files() => new[] { "xlsx", "pdf", "xdt" }.Select(e => (Path.Combine(stage, "result." + e), Path.Combine(dest, "AS110-035_SN10_20261114." + e))).ToArray();
            void Fill(string text) { foreach (var f in Files()) File.WriteAllText(f.From, text); }
            string Read() => string.Join(",", Files().Select(f => File.Exists(f.To) ? File.ReadAllText(f.To) : "-"));

            Fill("first"); SaveReplace.ReplaceAll(Files(), stage);
            Check(Read() == "first,first,first" && Directory.GetFiles(stage).Length == 0, "the first save moves every file into place");
            Fill("second"); SaveReplace.ReplaceAll(Files(), stage);
            Check(Read() == "second,second,second", "saving again replaces every file");
            foreach (string leftover in Directory.GetFiles(stage)) File.Delete(leftover);

            // 2 つ目(PDF)を開いたまま保存し直す: 1 つ目(Excel)は置き換えたあとで元に戻り、保存先は前のまま。
            Fill("third");
            using (new FileStream(Files()[1].To, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                SaveReplaceException? stopped = null;
                try { SaveReplace.ReplaceAll(Files(), stage); } catch (SaveReplaceException ex) { stopped = ex; }
                Check(stopped is { Restored: true } && stopped.Path == Files()[1].To, "a save that stops at an open file reports that file and that the folder was restored");
            }
            Check(Read() == "second,second,second" && Files().All(f => File.ReadAllText(f.From) == "third"),
                "after the stop the folder holds the previous set only, and this save's files are back in the stage folder");
            SaveReplace.ReplaceAll(Files(), stage);
            Check(Read() == "third,third,third", "after closing the file the same save goes through");
            foreach (string leftover in Directory.GetFiles(stage)) File.Delete(leftover);

            // 3 つ目(LOGGER)だけ前のファイルが無い保存先で、3 つ目の元ファイルが無くて止まる: 新しく置いたものは残さない。
            File.Delete(Files()[2].To); Fill("fourth"); File.Delete(Files()[2].From);
            bool stoppedAtMissing = false;
            try { SaveReplace.ReplaceAll(Files(), stage); } catch (SaveReplaceException ex) { stoppedAtMissing = ex.Restored; }
            Check(stoppedAtMissing && Read() == "third,third,-", "a stop at the last file restores the files already replaced and leaves no new file");
        }
        finally { try { Directory.Delete(root, true); } catch { } }

        // ---- 設定ファイル: 壊れていても初期値で起動し、壊れたファイルは名前を変えて残す ----
        string folder = Path.Combine(Path.GetTempPath(), "AS110AllCheck.Verification", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "ver3.json");
            Check(TransferSettings.LoadFrom(path) is { LoadWarning: null, ExpectedSamples: 6500 }, "no settings file starts with defaults and no warning");
            var saved = new TransferSettings { ExpectedSamples = 1234, SaveRoot = @"C:\保存先" };
            saved.LeakMaximumLowerLimitKpa["AS110-035"] = 531.5;
            saved.SaveTo(path);
            var loaded = TransferSettings.LoadFrom(path);
            Check(loaded.LoadWarning is null && loaded.ExpectedSamples == 1234 && loaded.SaveRoot == @"C:\保存先" && loaded.LeakMaximumLowerLimitKpa["AS110-035"] == 531.5
                && !File.Exists(path + ".tmp") && !File.ReadAllText(path).Contains("LoadWarning"), "settings are written through a temporary file and read back; the warning is not stored");
            saved.ExpectedSamples = 4321; saved.SaveTo(path);
            Check(TransferSettings.LoadFrom(path).ExpectedSamples == 4321, "saving again replaces the settings file");
            foreach (string broken in new[] { "", "{\"ExpectedSamples\": 12", "これは設定ではない" })
            {
                File.WriteAllText(path, broken);
                var fallback = TransferSettings.LoadFrom(path);
                string[] kept = Directory.GetFiles(folder, "ver3_読めなかった設定_*.json");
                Check(fallback.ExpectedSamples == 6500 && fallback.LoadWarning is { } warning && warning.Contains("初期値に戻して起動しました") && warning.Contains("名前を変えて残しました")
                    && !File.Exists(path) && kept.Length == 1 && File.ReadAllText(kept[0]) == broken && warning.Contains(kept[0]),
                    $"a broken settings file ({broken.Length} chars) starts with defaults, warns, and is kept under another name");
                File.Delete(kept[0]);
            }
            File.WriteAllText(path, "null");
            Check(TransferSettings.LoadFrom(path) is { LoadWarning: null, ExpectedSamples: 6500 }, "a settings file holding null starts with defaults (as before)");
            saved.SaveTo(path);
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(TransferSettings.LoadFrom(path).LoadWarning is { } locked && locked.Contains("開けなかった") && Directory.GetFiles(folder, "*読めなかった*").Length == 0,
                    "a settings file that is open elsewhere starts with defaults, warns, and is not renamed");
            Check(TransferSettings.LoadFrom(path).ExpectedSamples == 4321, "the settings file that was only locked is intact");
            var draft = loaded.CreateDraft(); loaded.ApplyEdited(draft);
            Check(new TransferSettings().FindProblems().Count == 0, "the warning does not disturb the settings dialog copy");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // 作業用 Excel の書き換えを見つける部分(Excel を使わない所)と、保護の解除で出荷データを消去する部分。
    public static void RunEditsAndShippingData()
    {
        // ---- 書き換えを比べる ----
        object?[,] before = { { "規格", 10d, null }, { "=A1+B1", "", 3.5 }, { 0d, 0d, "x" } };
        Dictionary<string, SheetCells> Book(object?[,] sheet1, bool withSecond = true)
        {
            var book = new Dictionary<string, SheetCells> { ["sheet1"] = SheetCells.From(4, 1, sheet1, (row, column) => row == 6 && column <= 2) };
            if (withSecond) book["圧縮5"] = SheetCells.From(1, 1, new object?[,] { { "見出し" } });
            return book;
        }
        var baseline = Book(before);
        Check(baseline["sheet1"].Cells.Count == 5 && !baseline["sheet1"].Cells.ContainsKey((6, 1)) && baseline["sheet1"].Cells[(5, 3)] == "3.5",
            "cells are kept as entered text; empty cells and the measurement columns are not kept");
        Check(WorkbookEdits.Compare(baseline, Book(before)).Count == 0, "an untouched workbook has no edits");
        Check(WorkbookEdits.Compare(baseline, Book(new object?[,] { { "規格", 10d, null }, { "=A1+B1", "", 3.5 }, { 123d, 456d, "x" } })).Count == 0,
            "new measurement values are not edits");
        var edits = WorkbookEdits.Compare(baseline, Book(new object?[,] { { "規格", 12d, "追記" }, { "OK", "", null }, { 0d, 0d, "x" } }));
        Check(edits.Select(e => e.ToString()).SequenceEqual(["sheet1 の B4：「10」 → 「12」", "sheet1 の C4：（空） → 「追記」", "sheet1 の A5：「=A1+B1」 → 「OK」", "sheet1 の C5：「3.5」 → （空）"]),
            "changed values, added cells, overwritten formulas and cleared cells are listed in sheet order: " + string.Join(" / ", edits));
        var missing = WorkbookEdits.Compare(baseline, Book(before, withSecond: false));
        Check(missing.Count == 1 && missing[0].ToString() == "圧縮5 の シート：「ある」 → 「無い」", "a deleted sheet is an edit");
        var added = Book(before); added["メモ"] = SheetCells.From(1, 1, new object?[,] { { "a" } });
        Check(WorkbookEdits.Compare(baseline, added).Count == 0, "a sheet added by a person is not compared");
        Check(WorkbookEdits.Signature(edits) == WorkbookEdits.Signature(edits.ToList()) && WorkbookEdits.Signature(edits) != WorkbookEdits.Signature(edits.Take(3)),
            "the same edits have the same signature; one more edit changes it");
        string described = WorkbookEdits.Describe(edits, 2);
        Check(described.Contains("・sheet1 の B4") && described.Contains("・ほか 2 件") && !described.Contains("A5") && !WorkbookEdits.Describe(edits).Contains("ほか"), "a long list shows the first ones and the rest as a count");
        Check(!WorkbookEdits.PasswordMatches("") && !WorkbookEdits.PasswordMatches("x") && !WorkbookEdits.PasswordMatches(" ") && !WorkbookEdits.PasswordMatches("as110"), "an empty or wrong password (and the old provisional one) is rejected");

        string root = Path.Combine(Path.GetTempPath(), "AS110AllCheck.Verification", Guid.NewGuid().ToString("N"), "SN1-100");
        try
        {
            Directory.CreateDirectory(root);
            // ---- 書き換えの記録 ----
            string record = WorkbookEdits.AppendRecord(root, new DateTime(2026, 11, 14, 9, 8, 7), "12", "AS110-035", "AS110-035_SN12_20261114", edits);
            WorkbookEdits.AppendRecord(root, new DateTime(2026, 11, 14, 10, 0, 0), "13", "AS110-035", "AS110-035_SN13_20261114", [new("sheet1", "H5", "a\tb", "c\r\nd")]);
            string[] lines = File.ReadAllLines(record);
            Check(record == Path.Combine(root, "Excel書き換え記録.txt") && File.ReadAllBytes(record)[0] == 0xEF && lines.Length == 6 && lines[0].TrimStart('﻿') == WorkbookEdits.RecordHeader
                && lines[1] == "2026-11-14 09:08:07\t12\tAS110-035\tAS110-035_SN12_20261114\tsheet1\tB4\t10\t12" && lines[5].EndsWith("\tsheet1\tH5\ta b\tc  d"),
                "edits saved with a password are recorded one cell per line, with a header once, tabs and line breaks removed");
            using (new FileStream(record, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(Message(() => WorkbookEdits.AppendRecord(root, DateTime.Now, "14", "AS110-035", "x", edits)).Contains("「Excel書き換え記録.txt」を Excel などで開いていたら閉じてください"),
                    "a record that is open elsewhere explains what to close");

            // ---- 保護の解除で出荷データを消去する ----
            Check(ShippingDataModels.ExistingRoots(root).Length == 0 && ShippingDataModels.FindData(root, ["5"]).Count == 0 && !Directory.Exists(Path.Combine(root, "出荷データ(350kPa)")),
                "without a shipping data folder nothing is found and no folder is created");
            string shipping = Path.Combine(root, "出荷データ(350kPa)");
            foreach (string folder in new[] { "Excel", "PDF", "LOGGER" }) Directory.CreateDirectory(Path.Combine(shipping, folder));
            string Make(string folder, string name) { string path = Path.Combine(shipping, folder, name); File.WriteAllText(path, "x"); return path; }
            string[] five = [Make("Excel", "AS110-116B350_SN5_20261113.xlsx"), Make("PDF", "AS110-116B350_SN5_20261113.pdf"), Make("LOGGER", "AS110-116B350_SN0005_20261113.xdt")];
            string six = Make("Excel", "AS110-116B350_SN6_20261113.xlsx"), fifty = Make("Excel", "AS110-116B350_SN50_20261113.xlsx");
            var shippingStore = new CompletionStore(shipping);
            using (shippingStore.Lock()) { shippingStore.Append("COMPLETE", "5", "AS110-116B350", "a"); shippingStore.Append("COMPLETE", "7", "AS110-116B350", "b"); shippingStore.Append("COMPLETE", "50", "AS110-116B350", "c"); }
            var found = ShippingDataModels.FindData(root, ["5", "6", "7", "8"]);
            Check(found.Count == 3 && found[0] is { Serial: "5", Completed: true } && found[0].Files.Order().SequenceEqual(five.Order()) && found[0].Root == shipping
                && found[1] is { Serial: "6", Completed: false } && found[1].Files.SequenceEqual([six]) && found[2] is { Serial: "7", Completed: true, Files.Length: 0 },
                "shipping data is found by serial: saved files, completion, or both; a serial with neither is not listed");
            Check(ShippingDataModels.FindData(root, []).Count == 0, "no released serial finds nothing");

            // 1 つでもほかで開いていたら、何も消さずに止める。
            var deleted = new List<string>();
            using (new FileStream(five[1], FileMode.Open, FileAccess.Read, FileShare.Read))
                Check(Message(() => ShippingDataModels.Remove(found, deleted.Add)).Contains("「AS110-116B350_SN5_20261113.pdf」を Excel などで開いていたら閉じてください") && deleted.Count == 0,
                    "a shipping data file that is open elsewhere stops the removal before anything is deleted");
            using (shippingStore.Lock()) Check(shippingStore.IsComplete("5") && shippingStore.IsComplete("7"), "the completion records are untouched after the stop");

            ShippingDataModels.Remove(found, file => { deleted.Add(file); File.Delete(file); });
            using (shippingStore.Lock())
                Check(deleted.Order().SequenceEqual(five.Append(six).Order()) && !File.Exists(five[0]) && File.Exists(fifty)
                    && !shippingStore.IsComplete("5") && shippingStore.WasReleased("5") && shippingStore.WasReleased("7") && !shippingStore.WasReleased("6") && shippingStore.IsComplete("50"),
                    "removal deletes the files of the released serials, releases their completion, and leaves other serials alone");
            Check(ShippingDataModels.FindData(root, ["5", "6", "7"]).Count == 0, "nothing is left to remove afterwards");
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(root)!, true); } catch { } }
    }

    // 異常終了で残った一時フォルダーの後片付け(SessionFolders)。
    public static void RunSessionFolders()
    {
        string parent = Path.Combine(Path.GetTempPath(), "AS110AllCheck.Verification", Guid.NewGuid().ToString("N"));
        try
        {
            string Folder(string name) { string path = Path.Combine(parent, name); Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "latest.xdt"), "x"); return path; }
            string Id() => Guid.NewGuid().ToString("N");
            var now = DateTime.Now;
            string own = Folder("ver3-" + Id()), running = Folder("ver3-" + Id()), crashed = Folder("ver3-" + Id()), oldRecent = Folder("ver3-" + Id()), oldStale = Folder(Id());
            string other = Folder("メモ"), almost = Folder("ver3-123");
            using var ownLock = SessionFolders.Hold(own);
            using var runningLock = SessionFolders.Hold(running);
            // 異常終了したフォルダー: lock のファイルだけが残っている(持ち主はいない)。Excel の覚えもある。
            File.WriteAllText(Path.Combine(crashed, SessionFolders.LockFileName), "");
            File.WriteAllText(Path.Combine(crashed, SessionFolders.ExcelFileName), "123	456");
            Directory.SetLastWriteTime(oldStale, now - SessionFolders.UnlockedAge - TimeSpan.FromHours(1));
            Check(ownLock is not null && runningLock is not null && File.Exists(Path.Combine(own, SessionFolders.LockFileName)), "a folder in use holds its lock file");

            var asked = new List<string>();
            var notes = SessionFolders.Cleanup(parent, [own], now, folder => { asked.Add(folder); return folder == crashed ? "Excel を終了しました" : null; });
            Check(Directory.Exists(own) && Directory.Exists(running), "the own folder and a folder locked by a running app are left alone");
            Check(!Directory.Exists(crashed) && asked.Contains(crashed) && notes.Contains("Excel を終了しました") && notes.Any(n => n.Contains("消しました") && n.Contains(crashed)),
                "a folder whose owner is gone is removed after its hidden Excel is stopped");
            Check(Directory.Exists(oldRecent) && !Directory.Exists(oldStale), "a folder without a lock (an older version) is removed only when older than three days");
            Check(Directory.Exists(other) && Directory.Exists(almost) && !asked.Contains(other), "folders that are not session folders are never touched");
            Check(SessionFolders.Cleanup(Path.Combine(parent, "無い"), [], now).Count == 0, "a missing parent folder does nothing");

            // 消せないフォルダー(中のファイルを開いている)は残し、次にまた試す。
            string busy = Folder("ver3-" + Id()); File.WriteAllText(Path.Combine(busy, SessionFolders.LockFileName), "");
            using (new FileStream(Path.Combine(busy, "latest.xdt"), FileMode.Open, FileAccess.Read, FileShare.None))
                Check(SessionFolders.Cleanup(parent, [own], now, _ => null).Any(n => n.Contains("消せませんでした") && n.Contains(busy)) && Directory.Exists(busy), "a folder that cannot be removed is reported and kept");
            Check(SessionFolders.Cleanup(parent, [own], now, _ => null).Any(n => n.Contains("消しました")) && !Directory.Exists(busy), "it is removed at the next start");

            // 覚えた Excel かどうか: 名前が EXCEL で、起動時刻が合うものだけ(プロセス ID は使い回される)。
            Check(SessionFolders.ReadExcelProcess(own) is null, "no record gives nothing");
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            SessionFolders.WriteExcelProcess(own, self.Id);
            Check(SessionFolders.ReadExcelProcess(own) == (self.Id, self.StartTime.Ticks), "the process id and start time are recorded");
            Check(SessionFolders.StopHiddenExcel(own) is null && !self.HasExited, "a recorded process that is not Excel is never stopped");
            long t = now.Ticks;
            Check(SessionFolders.IsRecordedExcel("EXCEL", t, t) && SessionFolders.IsRecordedExcel("excel", t + TimeSpan.FromSeconds(1).Ticks, t)
                && !SessionFolders.IsRecordedExcel("EXCEL", t + TimeSpan.FromSeconds(5).Ticks, t) && !SessionFolders.IsRecordedExcel("notepad", t, t),
                "only an Excel process started at the recorded time matches");
            File.WriteAllText(Path.Combine(own, SessionFolders.ExcelFileName), "こわれた");
            Check(SessionFolders.ReadExcelProcess(own) is null && SessionFolders.StopHiddenExcel(own) is null, "a broken record is ignored");
            ownLock!.Dispose();
            Check(!File.Exists(Path.Combine(own, SessionFolders.LockFileName)), "the lock file disappears when it is released");
        }
        finally { try { Directory.Delete(parent, true); } catch { } }
    }

    private static string Message(Action action)
    {
        try { action(); return ""; }
        catch (IOException ex) { return ex.Message; }
    }

    // 検査の出力フォルダーから上へたどって、アプリの「雛形」フォルダーを探す(無ければ null)。
    private static string? FindTemplateFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "雛形");
            if (File.Exists(Path.Combine(candidate, "AS110-035雛形.xlsx"))) return candidate;
        }
        return null;
    }
}
