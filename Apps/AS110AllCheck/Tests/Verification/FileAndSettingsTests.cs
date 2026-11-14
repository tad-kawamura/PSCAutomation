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
