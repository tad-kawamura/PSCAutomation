using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using static PSCAutomation.Wrappers.Excel.ExcelCom;

namespace PSCAutomation.Wrappers.Excel;

/// <summary>開いているブックの列に書く値。<paramref name="StartCell"/> から下へ書く。</summary>
public sealed record ColumnValues(string StartCell, double[] Values);

/// <summary>
/// 図形の中の文字の置き換え。<paramref name="Pattern"/> は正規表現で、置き換える部分を <c>(?&lt;value&gt;…)</c> で囲む。
/// 例: <c>new(@"S/N[ \t　]*(?&lt;value&gt;\d+|-)", "12345")</c> は「S/N」の後ろの番号だけを 12345 にする。
/// </summary>
public sealed record ShapeTextReplacement(string Pattern, string Value);

/// <summary>図形の名前と文字。</summary>
public sealed record ShapeText(string Name, string Text);

/// <summary><see cref="ExcelWorkbook.Sheet"/> で取ったシート。使い終わったら Dispose する。</summary>
/// <remarks>元: AS110AllCheck ExcelWorkSession(列への書き込み・判定セルの読み取り・図形内の S/N 書き換え・PDF)と AS110NoiseTest NoiseResultWorkbook(セルの読み書き・行のコピー・最終行)。</remarks>
[SupportedOSPlatform("windows")]
public sealed class ExcelSheet : IDisposable
{
    private readonly ExcelApplication _application;
    private object? _sheet;

    internal ExcelSheet(ExcelApplication application, object sheet)
    {
        _application = application;
        _sheet = sheet;
        Name = (string)((dynamic)sheet).Name;
    }

    public string Name { get; }

    private dynamic Sheet => _sheet ?? throw new ObjectDisposedException(nameof(ExcelSheet));

    private T WithRange<T>(string address, Func<dynamic, T> action)
    {
        object? range = null;
        try
        {
            range = Sheet.Range[address];
            return action(range!);
        }
        finally { Release(range); }
    }

    private void WithRange(string address, Action<dynamic> action) =>
        WithRange<bool>(address, r => { action(r); return true; });

    // ---- セルの読み書き ----

    /// <summary>セルの値(数値は double、文字は string、空は null)。</summary>
    public object? GetValue(string address) => WithRange(address, r => (object?)r.Value2);
    public object? GetValue(int row, int column) => GetValue(Address(row, column));

    /// <summary>セルの値を文字にして前後の空白を除いたもの。空なら ""。</summary>
    public string GetText(string address) => Convert.ToString(GetValue(address), CultureInfo.InvariantCulture)?.Trim() ?? "";
    public string GetText(int row, int column) => GetText(Address(row, column));

    /// <summary>セルに値を書く。日付は <see cref="DateTime.ToOADate"/> の数値で渡すと書式どおりに表示される。</summary>
    public void SetValue(string address, object? value) => WithRange(address, r => { r.Value2 = value; });
    public void SetValue(int row, int column, object? value) => SetValue(Address(row, column), value);

    /// <summary>範囲の値をまとめて読む。0 始まり([行, 列])。</summary>
    public object?[,] GetValues(string rangeAddress) => WithRange(rangeAddress, r => ToZeroBased((object?)r.Value2));

    /// <summary><paramref name="startCell"/> を左上として、値をまとめて書く(1 セルずつ書くより速い)。</summary>
    public void SetValues(string startCell, object?[,] values)
    {
        if (values.Length == 0) throw new ArgumentException("書く値がありません。", nameof(values));
        WithRange(startCell, start =>
        {
            object? target = null;
            try
            {
                target = start.Resize[values.GetLength(0), values.GetLength(1)];
                ((dynamic)target!).Value2 = values;
            }
            finally { Release(target); }
        });
    }

    /// <summary>範囲の値を消す(書式・罫線は残す)。</summary>
    public void ClearContents(string rangeAddress) => WithRange(rangeAddress, r => { r.ClearContents(); });
    public void ClearContents(int row, int firstColumn, int lastColumn) =>
        ClearContents(Address(row, firstColumn) + ":" + Address(row, lastColumn));

    /// <summary>範囲に数式のセルが 1 つでもあれば true。</summary>
    public bool HasFormula(string rangeAddress) => WithRange(rangeAddress, r => !Equals((object?)r.HasFormula, false));

    // ---- 測定値の列 ----

    /// <summary>
    /// 開いているブックの列に測定値を書く(元: AllCheck UpdateMeasurements)。
    /// 書き込み先に数式があれば何も書かずに <see cref="InvalidDataException"/>。同じ列への 2 回の指定、非数値は止める。
    /// 隣り合う列で開始行と点数が同じものは 1 回でまとめて書き、書く間は画面の描き直しを止める。
    /// </summary>
    /// <remarks>
    /// 開始セルより下に前回の値が残っていても消さない(同じ点数で上書きする使い方を想定)。
    /// Excel を起動せずにファイルへ書くときは <see cref="TemplateWriter.Save"/> を使う。
    /// </remarks>
    public void WriteColumns(IReadOnlyList<ColumnValues> columns)
    {
        if (columns.Count == 0) throw new ArgumentException("書く列がありません。", nameof(columns));
        var parsed = new List<(int Column, int Row, double[] Values)>();
        foreach (var column in columns)
        {
            var (col, row) = TemplateWriter.ParseCell(column.StartCell);
            if (column.Values.Length == 0 || (long)row + column.Values.Length - 1 > 1048576)
                throw new ArgumentException("点数が Excel の行数を超えるか、データがありません。");
            if (column.Values.Any(v => !double.IsFinite(v))) throw new InvalidDataException("非数値を Excel へ書き込めません。");
            if (parsed.Any(p => p.Column == col)) throw new ArgumentException($"同じ列へ重複して書けません:{Name} {ColumnName(col)}列");
            parsed.Add((col, row, column.Values));
        }
        // 人が測定値の欄に数式を置いた場合は上書きしない。
        foreach (var p in parsed)
        {
            string range = Address(p.Row, p.Column) + ":" + Address(p.Row + p.Values.Length - 1, p.Column);
            if (HasFormula(range)) throw new InvalidDataException($"{Name}!{range} に数式があります。書き込み先の設定を確認してください。");
        }
        // 隣り合う列(開始行・点数が同じ)をまとめる。
        var blocks = new List<List<(int Column, int Row, double[] Values)>>();
        foreach (var p in parsed.OrderBy(p => p.Column))
        {
            var last = blocks.LastOrDefault();
            var prev = last?[^1];
            if (last is not null && prev!.Value.Column + 1 == p.Column && prev.Value.Row == p.Row && prev.Value.Values.Length == p.Values.Length)
                last.Add(p);
            else
                blocks.Add(new() { p });
        }
        bool screenUpdating = _application.ScreenUpdating;
        try
        {
            _application.ScreenUpdating = false;
            foreach (var block in blocks)
            {
                int rows = block[0].Values.Length;
                var values = new object?[rows, block.Count];
                for (int c = 0; c < block.Count; c++)
                    for (int r = 0; r < rows; r++)
                        values[r, c] = block[c].Values[r];
                SetValues(Address(block[0].Row, block[0].Column), values);
            }
        }
        finally { _application.ScreenUpdating = screenUpdating; }
    }

    // ---- 行 ----

    /// <summary>列の中で値の入っている最後の行。列が空なら 0。</summary>
    public int LastUsedRow(int column)
    {
        object? rows = null, cells = null, bottom = null, last = null;
        try
        {
            rows = Sheet.Rows;
            int count = (int)((dynamic)rows).Count;
            cells = Sheet.Cells;
            bottom = ((dynamic)cells)[count, column];
            if (((dynamic)bottom).Value2 is not null) return count;
            last = ((dynamic)bottom).End(XlUp);
            int row = (int)((dynamic)last).Row;
            return row == 1 && ((dynamic)last).Value2 is null ? 0 : row;
        }
        finally { Release(last); Release(bottom); Release(cells); Release(rows); }
    }

    /// <summary>行を丸ごと(値・数式・書式・罫線)別の行へコピーする(元: NoiseTest。雛形の行が足りないときに上の行の形を写す)。</summary>
    public void CopyRow(int sourceRow, int destinationRow)
    {
        object? rows = null, source = null, destination = null;
        try
        {
            rows = Sheet.Rows;
            source = ((dynamic)rows)[sourceRow];
            destination = ((dynamic)rows)[destinationRow];
            ((dynamic)source).Copy(destination);
        }
        finally { Release(destination); Release(source); Release(rows); }
    }

    // ---- 図形の中の文字 ----

    /// <summary>テキストボックスとオートシェイプの名前と文字の一覧。置き換えるパターンを決めるときの見本に使う。</summary>
    public IReadOnlyList<ShapeText> GetShapeTexts()
    {
        var result = new List<ShapeText>();
        ForEachTextShape((name, text, _) => result.Add(new ShapeText(name, text)));
        return result;
    }

    /// <summary>
    /// 図形(テキストボックス・オートシェイプ)の中の文字を、文字ごとの書式(フォント・大きさ・太字・下線)を保ったまま一部だけ置き換える
    /// (元: AllCheck UpdateHeader の、図形内の S/N・測定日の書き換え)。
    /// </summary>
    /// <param name="shapeFilter">対象にする図形を、図形の文字で選ぶ(例: 「S/N」と「測定日」を両方含む)。</param>
    /// <param name="replacements">置き換え。同じ値なら何もしない。桁数が変わっても前の文字の位置はずれない。</param>
    /// <returns><paramref name="shapeFilter"/> に当てはまった図形の数。0 なら図形が見つからなかった。</returns>
    public int ReplaceShapeText(Func<string, bool> shapeFilter, IReadOnlyList<ShapeTextReplacement> replacements)
    {
        var regexes = replacements.Select(r =>
        {
            var regex = new Regex(r.Pattern);
            if (!regex.GetGroupNames().Contains("value"))
                throw new ArgumentException($"パターンに (?<value>…) がありません:{r.Pattern}", nameof(replacements));
            return (Regex: regex, r.Value);
        }).ToArray();
        int matched = 0;
        ForEachTextShape((_, text, range) =>
        {
            if (!shapeFilter(text)) return;
            matched++;
            // 全文を代入すると文字ごとの書式が消える。値の部分だけを後ろから置き換える。
            var edits = new List<(int Start, int Length, string Value)>();
            foreach (var (regex, value) in regexes)
            {
                foreach (Match match in regex.Matches(text))
                {
                    Group part = match.Groups["value"];
                    if (part.Success && part.Value != value) edits.Add((part.Index + 1, part.Length, value));
                }
            }
            foreach (var edit in edits.OrderByDescending(e => e.Start))
            {
                object? characters = null;
                try
                {
                    characters = range.Characters(edit.Start, edit.Length);
                    ((dynamic)characters!).Text = edit.Value;
                }
                finally { Release(characters); }
            }
        });
        return matched;
    }

    private void ForEachTextShape(Action<string, string, dynamic> action)
    {
        object? shapes = null;
        try
        {
            shapes = Sheet.Shapes;
            int count = (int)((dynamic)shapes).Count;
            for (int i = 1; i <= count; i++)
            {
                object? shape = null, frame = null, range = null;
                try
                {
                    shape = ((dynamic)shapes).Item(i);
                    int kind = (int)((dynamic)shape).Type;
                    if (kind != MsoTextBox && kind != MsoAutoShape) continue;
                    frame = ((dynamic)shape).TextFrame2;
                    range = ((dynamic)frame).TextRange;
                    action((string)((dynamic)shape).Name, (string)((dynamic)range).Text, range!);
                }
                finally { Release(range); Release(frame); Release(shape); }
            }
        }
        finally { Release(shapes); }
    }

    // ---- その他 ----

    /// <summary>このシートを PDF に保存する(元: AllCheck)。</summary>
    public void ExportAsPdf(string path) => Sheet.ExportAsFixedFormat(XlTypePdf, Path.GetFullPath(path));

    /// <summary>このシートを表示する。</summary>
    public void Activate() => Sheet.Activate();

    /// <summary>このシートだけを再計算する。</summary>
    public void Calculate() => Sheet.Calculate();

    public void Dispose()
    {
        Release(_sheet);
        _sheet = null;
    }
}
