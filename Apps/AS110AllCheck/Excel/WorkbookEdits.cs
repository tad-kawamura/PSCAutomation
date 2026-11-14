using System.Globalization;
using System.Text;

namespace PSCAutomation.Apps.AS110AllCheck;

// 作業用の Excel ブックを人が書き換えたか(2026-11-14、全体見直し・区切り 5、人と決めた。SPEC「作業用 Excel の書き換え」)。
// 雛形にはシートの保護が無く、例外的に規格値を直すことがある。書き換えは禁止せず、アプリが見つけて、パスワードを入れれば保存に進める。
// - 比べる相手: ブックを雛形から作って開いた直後の、全シートのセルに入れてあるもの(数式は数式の文字、値は値)。測定値の列は比べない
// - その 1 台だけ: 書き換えたブックは、次の番号では雛形から作り直す(ExcelWorkSession.Open)
// - 今後すべて: アプリでは持ち越さない。雛形フォルダーの Excel 雛形を人が直す
// ここは Excel を使わない部分(比べる・文を作る・パスワード・記録)。Excel から読むのは ExcelWorkSession。
internal sealed record WorkbookEdit(string Sheet, string Address, string Before, string After)
{
    public override string ToString() => $"{Sheet} の {Address}：{Show(Before)} → {Show(After)}";
    private static string Show(string value) => value.Length == 0 ? "（空）" : "「" + value + "」";
}

// 1 シート分の「セルに入れてあるもの」。空のセルは持たない。
internal sealed class SheetCells
{
    public Dictionary<(int Row, int Column), string> Cells { get; } = new();

    // Excel から読んだ範囲(左上の行・列と 0 始まりの値)を取り込む。skip に当たるセル(測定値の列)は持たない。
    public static SheetCells From(int firstRow, int firstColumn, object?[,] values, Func<int, int, bool>? skip = null)
    {
        var sheet = new SheetCells();
        for (int r = 0; r < values.GetLength(0); r++)
            for (int c = 0; c < values.GetLength(1); c++)
            {
                string text = Convert.ToString(values[r, c], CultureInfo.InvariantCulture) ?? "";
                if (text.Length == 0 || skip?.Invoke(firstRow + r, firstColumn + c) == true) continue;
                sheet.Cells[(firstRow + r, firstColumn + c)] = text;
            }
        return sheet;
    }
}

internal static class WorkbookEdits
{
    // 書き換えを進めるときのパスワード(人の決定: アプリに固定。事故を防ぐためのもので、秘密を守るためのものではない)。
    // 仮の値。発行(ver3.02)の前に人が決めた値に替える。
    private const string Password = "as110";

    public const string RecordFileName = "Excel書き換え記録.txt";
    public static readonly string RecordHeader = string.Join('\t', "日時", "S/N", "型式", "保存ファイル", "シート", "セル", "書き換え前", "書き換え後");

    public static bool PasswordMatches(string input) => string.Equals(input, Password, StringComparison.Ordinal);

    // 作った直後(baseline)と今(current)を比べ、違うセルをシート・行・列の順に返す。baseline にあったシートが無くなっていれば、それも 1 件。
    // 人が足したシートは見ない(判定に使うのは雛形のシート)。
    public static List<WorkbookEdit> Compare(IReadOnlyDictionary<string, SheetCells> baseline, IReadOnlyDictionary<string, SheetCells> current)
    {
        var edits = new List<WorkbookEdit>();
        foreach (var (name, before) in baseline)
        {
            if (!current.TryGetValue(name, out var after)) { edits.Add(new(name, "シート", "ある", "無い")); continue; }
            foreach (var cell in before.Cells.Keys.Union(after.Cells.Keys).OrderBy(k => k.Row).ThenBy(k => k.Column))
            {
                string old = before.Cells.GetValueOrDefault(cell, ""), now = after.Cells.GetValueOrDefault(cell, "");
                if (!string.Equals(old, now, StringComparison.Ordinal)) edits.Add(new(name, ColumnName(cell.Column) + cell.Row.ToString(CultureInfo.InvariantCulture), old, now));
            }
        }
        return edits;
    }

    // 同じ書き換えかを比べるための文字(パスワードで進めたあと、同じ書き換えでもう一度聞かないため)。
    public static string Signature(IEnumerable<WorkbookEdit> edits) => string.Join("\n", edits.Select(e => $"{e.Sheet}\t{e.Address}\t{e.Before}\t{e.After}"));

    // 画面・ログに出す一覧。多いときは最初の max 件と残りの件数。
    public static string Describe(IReadOnlyList<WorkbookEdit> edits, int max = 20) =>
        string.Join("\r\n", edits.Take(max).Select(e => "・" + e)) + (edits.Count > max ? $"\r\n・ほか {edits.Count - max} 件" : "");

    public static string RecordPath(string root) => Path.Combine(root, RecordFileName);

    // 書き換えたまま保存したことを、保存先の「Excel書き換え記録.txt」に残す(1 セル 1 行。タブ区切りの UTF-8。新しく作るときだけ BOM と見出し)。
    public static string AppendRecord(string root, DateTime at, string serial, string model, string baseName, IReadOnlyList<WorkbookEdit> edits)
    {
        string path = RecordPath(root);
        static string Clean(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        return FileAccessAdvice.Run("Excel 書き換え記録", path, () =>
        {
            using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            var text = new StringBuilder();
            if (file.Length == 0) text.Append('﻿').Append(RecordHeader).Append("\r\n");
            foreach (var edit in edits)
                text.Append(string.Join('\t', at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), serial, model, baseName,
                    Clean(edit.Sheet), edit.Address, Clean(edit.Before), Clean(edit.After))).Append("\r\n");
            file.Write(new UTF8Encoding(false).GetBytes(text.ToString()));
            file.Flush(true);
            return path;
        });
    }

    private static string ColumnName(int column)
    {
        string name = "";
        for (int c = column; c > 0; c = (c - 1) / 26) name = (char)('A' + (c - 1) % 26) + name;
        return name;
    }
}
