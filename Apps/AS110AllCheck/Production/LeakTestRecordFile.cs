using System.Globalization;
using System.Text;

namespace PSCAutomation.Apps.AS110AllCheck;

// 1 回の漏れ試験の結果。値は kPa(自動判定しなかったとき・下限を比べなかったときは NaN)。
internal sealed record LeakTestResult(
    DateTime At, LeakOutcome Outcome, int Seconds, double Threshold,
    double Maximum, double Latest, double MaxDifference, long Points, string Note, double LowerLimit = double.NaN)
{
    public string Summary => Outcome switch
    {
        LeakOutcome.NotJudged => $"{LeakOutcomeText.Of(Outcome)}（{Seconds} 秒）",
        LeakOutcome.Pass => $"合格（差の最大 {LeakJudge.Format(MaxDifference)} kPa ≦ しきい値 {LeakJudge.Format(Threshold, 1)} kPa"
            + (double.IsFinite(LowerLimit) ? $"、最大値 {LeakJudge.Format(Maximum)} kPa ≧ 下限 {LeakJudge.Format(LowerLimit, 1)} kPa" : "") + $"、{Seconds} 秒）",
        _ => $"{LeakOutcomeText.Of(Outcome)}（{Note}）",
    };
}

// 漏れ試験の記録(SPEC「漏れ試験の自動判定」9)。データ取りの保存先の「漏れ試験記録.txt」に、試験のたびに 1 行追記する。
// 完了記録.txt は変えない(1 行 5 項目でないと ver2.05 までの版が「不正な行」として止まるため)。
// タブ区切りの UTF-8。新しく作るときだけ BOM と見出しの行を書く(Excel・メモ帳で開いて読めるように)。
// 途中で終わった試験も 1 行残す(2026-11-13)。判定が決まっていればその結果(備考に後の異常)、決まる前なら結果「中断」。
// 最大値の下限の列は 2026-11-12 続きで末尾に足した(前からある列の位置は変えない)。それより前に作ったファイルは見出しにこの列名が無い。
internal static class LeakTestRecordFile
{
    public const string FileName = "漏れ試験記録.txt";
    public const string OverrideResult = "人の判断で合格";
    // 判定が決まる前に途中で終わった試験(通信が切れた・停止に失敗した・アプリを閉じた など)。2026-11-13、全体見直しで足した。
    public const string AbortedResult = "中断";
    public static readonly string Header = string.Join('\t',
        "日時", "S/N", "型式", "結果", "試験秒数", "しきい値(kPa)", "最大値(kPa)", "最後の値(kPa)", "差の最大(kPa)", "読んだ点数", "備考", "最大値の下限(kPa)");

    public static string PathOf(string root) => Path.Combine(root, FileName);

    // 試験の結果を 1 行追記する。書けなければ例外(呼び出し側で「合格」を押せなくする)。
    public static string Append(string root, string serial, string model, LeakTestResult result)
        => AppendLine(root, Line(result.At, serial, model, LeakOutcomeText.Of(result.Outcome), result));

    // 不合格・判定できなかった後に、人の判断で合格にしたことを 1 行追記する(値は直前の試験のもの)。
    public static string AppendOverride(string root, string serial, string model, LeakTestResult last, DateTime at)
        => AppendLine(root, Line(at, serial, model, OverrideResult, last with { Note = $"直前の判定：{LeakOutcomeText.Of(last.Outcome)}" + (last.Note.Length > 0 ? " / " + last.Note : "") }));

    // 判定が決まる前に途中で終わった試験を 1 行追記する(結果の列は「中断」、備考に理由。値は読めたところまで)。
    // partial.Outcome は結果の列には使わない(自動判定なしの NotJudged のときに、読んだ点数を「-」にするだけ)。
    public static string AppendAborted(string root, string serial, string model, LeakTestResult partial)
        => AppendLine(root, Line(partial.At, serial, model, AbortedResult, partial));

    // 試験を始める前に、記録を書けるかを確かめる(書けなければ例外)。ほかで開かれていて書けないことに、試験が終わってから気づかないように
    // (2026-11-13、全体見直し)。ファイルが無ければ空のファイルができる(見出しは最初の追記のときに書く)。
    public static void EnsureWritable(string root)
    {
        if (root.Length == 0) throw new InvalidOperationException("保存先を選択してください。");
        using var file = new FileStream(PathOf(root), FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    // 記録を書けないときに確かめてもらうこと(始める前・試験の後の両方の案内に付ける)。
    public static string UnwritableAdvice(string root, Exception ex) =>
        $"・「{FileName}」を Excel やメモ帳で開いていたら閉じてください\n・保存先（USB メモリーなど）をエクスプローラーで開けるか確かめてください\n{PathOf(root)}\n{ex.Message}";

    private static string Line(DateTime at, string serial, string model, string result, LeakTestResult r) => string.Join('\t',
        at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), serial, model, result,
        r.Seconds.ToString(CultureInfo.InvariantCulture), LeakJudge.Format(r.Threshold, 1),
        LeakJudge.Format(r.Maximum), LeakJudge.Format(r.Latest), LeakJudge.Format(r.MaxDifference),
        r.Outcome == LeakOutcome.NotJudged ? "-" : r.Points.ToString(CultureInfo.InvariantCulture),
        r.Note.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '),
        LeakJudge.Format(r.LowerLimit, 1));

    private static string AppendLine(string root, string line)
    {
        if (root.Length == 0) throw new InvalidOperationException("保存先を選択してください。");
        string path = PathOf(root);
        using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        var encoding = new UTF8Encoding(false);
        var text = new StringBuilder();
        if (file.Length == 0) text.Append('﻿').Append(Header).Append("\r\n");
        text.Append(line).Append("\r\n");
        file.Write(encoding.GetBytes(text.ToString()));
        file.Flush(true);
        return path;
    }
}
