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
// 最大値の下限の列は 2026-11-12 続きで末尾に足した(前からある列の位置は変えない)。それより前に作ったファイルは見出しにこの列名が無い。
internal static class LeakTestRecordFile
{
    public const string FileName = "漏れ試験記録.txt";
    public const string OverrideResult = "人の判断で合格";
    public static readonly string Header = string.Join('\t',
        "日時", "S/N", "型式", "結果", "試験秒数", "しきい値(kPa)", "最大値(kPa)", "最後の値(kPa)", "差の最大(kPa)", "読んだ点数", "備考", "最大値の下限(kPa)");

    public static string PathOf(string root) => Path.Combine(root, FileName);

    // 試験の結果を 1 行追記する。書けなければ例外(呼び出し側で「合格」を押せなくする)。
    public static string Append(string root, string serial, string model, LeakTestResult result)
        => AppendLine(root, Line(result.At, serial, model, LeakOutcomeText.Of(result.Outcome), result));

    // 不合格・判定できなかった後に、人の判断で合格にしたことを 1 行追記する(値は直前の試験のもの)。
    public static string AppendOverride(string root, string serial, string model, LeakTestResult last, DateTime at)
        => AppendLine(root, Line(at, serial, model, OverrideResult, last with { Note = $"直前の判定：{LeakOutcomeText.Of(last.Outcome)}" + (last.Note.Length > 0 ? " / " + last.Note : "") }));

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
