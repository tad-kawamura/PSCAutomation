using PSCAutomation.Apps.AS110AllCheck;

// 正式保存の前に同じ S/N の前の保存ファイルを探すこと(名前が違っても拾う)と、転送設定の画面で直す写し。
// 2026-11-13 の全体見直し(区切り 1「量産の流れ」)で直した所。画面・機器・Excel なし。
internal static class SaveConflictTests
{
    private static void Check(bool b, string name) { if (!b) throw new Exception(name); Console.WriteLine("PASS " + name); }

    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "AS110AllCheck.Verification", Guid.NewGuid().ToString("N"), "SN100-200");
        try
        {
            foreach (string folder in new[] { "Excel", "PDF", "LOGGER" }) Directory.CreateDirectory(Path.Combine(root, folder));
            string Make(string folder, string name) { string path = Path.Combine(root, folder, name); File.WriteAllText(path, ""); return path; }
            // S/N 150: 前の日に保存した 3 つ、前の版の名前(末尾 _6500)の PDF、今日の名前の Excel。ほかの S/N と、形式の違うファイルもある。
            string oldExcel = Make("Excel", "AS110-035_SN150_20261112.xlsx"), oldPdf = Make("PDF", "AS110-035_SN150_20261112.pdf"), oldLogger = Make("LOGGER", "AS110-035_SN150_20261112.xdt");
            string legacyPdf = Make("PDF", "AS110-035_SN0150_20261113_6500.pdf");
            string todayExcel = Make("Excel", "AS110-035_SN150_20261113.xlsx");
            Make("Excel", "AS110-035_SN151_20261113.xlsx"); Make("PDF", "AS110-035_SN1500_20261113.pdf"); Make("Excel", "メモ_SN150.xlsx");

            var found = SaveFolder.FindSerialFiles(root, "150");
            Check(found.Count == 5 && new[] { oldExcel, oldPdf, oldLogger, legacyPdf, todayExcel }.All(found.Contains),
                "files of the same serial are found across dates, the legacy name and leading zeros; other serials and other formats are not");
            Check(SaveFolder.FindSerialFiles(root, "0150").Count == 5 && SaveFolder.FindSerialFiles(root, "152").Count == 0 && SaveFolder.FindSerialFiles(root, "").Count == 0
                && SaveFolder.FindSerialFiles(Path.Combine(root, "無い"), "150").Count == 0, "leading zeros are ignored; no files, an empty serial or a missing folder give nothing");

            // 今日の名前で保存するとき: 同じ名前の Excel は上書き、前の日のファイルと前の版の名前は残る。
            string[] targets = [todayExcel, Path.Combine(root, "PDF", "AS110-035_SN150_20261113.pdf"), Path.Combine(root, "LOGGER", "AS110-035_SN150_20261113.xdt")];
            var (overwritten, kept) = SaveFolder.FindSaveConflicts(root, "150", targets);
            Check(overwritten.SequenceEqual([todayExcel]) && kept.Length == 4 && new[] { oldExcel, oldPdf, oldLogger, legacyPdf }.All(kept.Contains),
                "same-name files are overwritten, other names of the same serial are kept");
            var (none, alsoNone) = SaveFolder.FindSaveConflicts(root, "160", targets);
            Check(none.Length == 0 && alsoNone.Length == 0, "a serial without saved files has no conflict");

            string text = SaveFolder.DescribeSaveConflicts("150", "AS110-035_SN150_20261113", overwritten, kept);
            Check(text.Contains("S/N 150 の保存ファイルがすでにあります") && text.Contains("上書きします") && text.Contains(todayExcel)
                && text.Contains("消さずに残ります") && text.Contains(oldPdf) && text.Contains(legacyPdf) && text.Contains("2 組")
                && text.IndexOf(todayExcel, StringComparison.Ordinal) < text.IndexOf("別の名前", StringComparison.Ordinal)
                && text.Contains("AS110-035_SN150_20261113 の名前で保存しますか"), "the confirmation separates overwritten files from kept files and warns about two sets");
            string onlyKept = SaveFolder.DescribeSaveConflicts("150", "AS110-035_SN150_20261114", [], kept);
            Check(!onlyKept.Contains("上書きします") && onlyKept.Contains("消さずに残ります"), "with only other-name files the confirmation does not mention overwriting");
            string onlySame = SaveFolder.DescribeSaveConflicts("150", "AS110-035_SN150_20261113", overwritten, []);
            Check(onlySame.Contains("上書きします") && !onlySame.Contains("2 組"), "with only same-name files the confirmation does not warn about two sets");
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(root)!, true); } catch { } }

        // 転送設定の画面: 表で直すのは写し。写しを直しても元は変わらず、取り込むのは表に出る項目だけ。
        var settings = new TransferSettings { SaveRoot = @"C:\前の保存先", ExpectedSamples = 6500, AllowLeakOverride = false, LeakTestSeconds = 10 };
        var draft = settings.CreateDraft();
        draft.ExpectedSamples = 1234; draft.Compression = 2; draft.DataSheet = "別シート"; draft.SaveRoot = @"C:\新しい保存先"; draft.CurrentCell = "C7";
        draft.AllowLeakOverride = true; draft.LeakTestSeconds = 55;
        Check(settings.ExpectedSamples == 6500 && settings.Compression == 5 && settings.DataSheet == "sheet1" && settings.SaveRoot == @"C:\前の保存先" && settings.CurrentCell == "A4",
            "editing the draft does not change the settings (closing the dialog discards the edits)");
        settings.ApplyEdited(draft);
        Check(settings.ExpectedSamples == 1234 && settings.Compression == 2 && settings.DataSheet == "別シート" && settings.SaveRoot == @"C:\前の保存先" && settings.CurrentCell == "C7"
            && settings.CurrentChannel == 1 && settings.PressureChannel == 2 && settings.JudgmentSheet == "sheet1" && settings.PressureCell == "B4",
            "saving applies every edited grid item and keeps the others (the save folder is not a grid item)");
        Check(!settings.AllowLeakOverride && settings.LeakTestSeconds == 10, "items hidden from the grid (leak settings) are not taken from the draft");
    }
}
