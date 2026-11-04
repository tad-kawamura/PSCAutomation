using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PSCAutomation.Wrappers.Excel;

public sealed record TransferColumn(string Sheet, string StartCell, double[] Values);

/// <summary>Excelを起動せずに雛形のZIP/XMLだけを更新。変更しないパーツはそのままコピー。</summary>
/// <remarks>元: AS110AllCheck Conversion/TemplateWriter.cs(XdtConversion の同名ファイルと同一)。Excel のインストールは不要。</remarks>
public static class TemplateWriter
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public static (int Column, int Row) ParseCell(string address)
    {
        var match = Regex.Match(address.Trim().ToUpperInvariant(), @"^([A-Z]{1,3})([1-9][0-9]{0,6})$");
        if (!match.Success) throw new ArgumentException($"開始セルが不正です：{address}（例 A6）");
        int col = 0;
        foreach (char c in match.Groups[1].Value) col = col * 26 + c - 'A' + 1;
        int row = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        if (col > 16384 || row > 1048576) throw new ArgumentException("Excelのセル範囲を超えています。");
        return (col, row);
    }
    private static string ColumnName(int column)
    {
        string name = "";
        while (column > 0) { column--; name = (char)('A' + column % 26) + name; column /= 26; }
        return name;
    }
    private static XDocument Load(ZipArchive zip, string path)
    {
        using var stream = (zip.GetEntry(path) ?? throw new InvalidDataException($"雛形に{path}がありません。")).Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }
    private static Dictionary<string, string> SheetPaths(ZipArchive zip)
    {
        var workbook = Load(zip, "xl/workbook.xml");
        var relationships = Load(zip, "xl/_rels/workbook.xml.rels").Root!.Elements()
            .ToDictionary(e => (string)e.Attribute("Id")!, e => (string)e.Attribute("Target")!);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sheet in workbook.Root!.Element(S + "sheets")!.Elements(S + "sheet"))
        {
            string target = relationships[(string)sheet.Attribute(R + "id")!];
            var uri = new Uri(new Uri("http://xlsx/xl/workbook.xml"), target);
            result.Add((string)sheet.Attribute("name")!, uri.AbsolutePath.TrimStart('/'));
        }
        return result;
    }
    public static string[] GetSheets(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return SheetPaths(zip).Keys.ToArray();
    }
    public static void Save(string template, string destination, IReadOnlyList<TransferColumn> columns)
    {
        if (columns.Count == 0) throw new ArgumentException("転送対象がありません。");
        if (string.Equals(Path.GetFullPath(template), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("雛形と保存先に同じファイルは指定できません。");
        if (File.Exists(destination)) throw new IOException("保存先に同名ファイルがあります。別の名前を指定してください。");
        using var input = ZipFile.OpenRead(template);
        var paths = SheetPaths(input);
        var changes = new Dictionary<string, XDocument>();
        foreach (var group in columns.GroupBy(x => x.Sheet))
        {
            if (!paths.TryGetValue(group.Key, out string? path)) throw new ArgumentException($"シートがありません：{group.Key}");
            var doc = Load(input, path);
            var sheetData = doc.Root!.Element(S + "sheetData") ?? throw new InvalidDataException("sheetDataがありません。");
            var rows = sheetData.Elements(S + "row").ToDictionary(e => (int)e.Attribute("r")!);
            var cells = rows.Values.SelectMany(e => e.Elements(S + "c"))
                .ToDictionary(e => (string)e.Attribute("r")!, StringComparer.OrdinalIgnoreCase);
            var usedColumns = new HashSet<int>();
            foreach (var column in group)
            {
                var (col, start) = ParseCell(column.StartCell);
                if (!usedColumns.Add(col)) throw new ArgumentException($"同一列へ重複転送できません：{group.Key} {ColumnName(col)}列");
                if (column.Values.Length == 0 || (long)start + column.Values.Length - 1 > 1048576)
                    throw new ArgumentException("転送点数がExcelの行数制限を超えるか、データがありません。");
                string letter = ColumnName(col);
                var targetCells = cells.Values.Where(e => {
                    var p = ParseCell((string)e.Attribute("r")!); return p.Column == col && p.Row >= start;
                }).ToArray();
                // 開始セル以降のこの列は測定データ専用。数式がある列は破壊せずに停止。
                if (targetCells.Any(e => e.Element(S + "f") != null))
                    throw new ArgumentException($"{group.Key}!{letter}{start}以降に数式があります。測定値用の列を指定してください。");
                foreach (var e in targetCells)
                {
                    e.Elements(S + "v").Remove(); e.Elements(S + "is").Remove(); e.Attribute("t")?.Remove();
                }
                XElement? styleCell = cells.GetValueOrDefault(letter + start);
                string? style = (string?)styleCell?.Attribute("s");
                for (int i = 0; i < column.Values.Length; i++)
                {
                    double value = column.Values[i];
                    if (!double.IsFinite(value)) throw new InvalidDataException("非数値をExcelへ書き込めません。");
                    int number = start + i;
                    if (!rows.TryGetValue(number, out var row))
                    {
                        row = new XElement(S + "row", new XAttribute("r", number)); rows.Add(number, row);
                    }
                    string address = letter + number;
                    if (!cells.TryGetValue(address, out var cell))
                    {
                        cell = new XElement(S + "c", new XAttribute("r", address));
                        if (style != null) cell.SetAttributeValue("s", style);
                        row.Add(cell); cells.Add(address, cell);
                    }
                    cell.Add(new XElement(S + "v", value.ToString("R", CultureInfo.InvariantCulture)));
                }
            }
            foreach (var row in rows.Values)
            {
                var ordered = row.Elements(S + "c").OrderBy(e => ParseCell((string)e.Attribute("r")!).Column).ToArray();
                row.Elements(S + "c").Remove(); row.AddFirst(ordered); row.Attribute("spans")?.Remove();
            }
            sheetData.Elements(S + "row").Remove(); sheetData.Add(rows.OrderBy(x => x.Key).Select(x => x.Value));
            int maxCol = cells.Keys.Select(x => ParseCell(x).Column).Max();
            int maxRow = rows.Keys.Max();
            doc.Root.Element(S + "dimension")?.SetAttributeValue("ref", $"A1:{ColumnName(maxCol)}{maxRow}");
            changes[path] = doc;
        }
        var book = Load(input, "xl/workbook.xml");
        var calc = book.Root!.Element(S + "calcPr");
        if (calc == null) { calc = new XElement(S + "calcPr"); book.Root.Add(calc); }
        calc.SetAttributeValue("calcMode", "auto"); calc.SetAttributeValue("fullCalcOnLoad", "1"); calc.SetAttributeValue("forceFullCalc", "1");
        changes["xl/workbook.xml"] = book;
        string folder = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(folder);
        string temporary = Path.Combine(folder, ".xdt-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                foreach (var entry in input.Entries)
                {
                    var copy = output.CreateEntry(entry.FullName, CompressionLevel.Fastest);
                    using var stream = copy.Open();
                    if (changes.TryGetValue(entry.FullName, out var xml)) xml.Save(stream, SaveOptions.DisableFormatting);
                    else { using var original = entry.Open(); original.CopyTo(stream); }
                }
            }
            File.Move(temporary, destination, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
