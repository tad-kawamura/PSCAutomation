using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using PSCAutomation.Wrappers.Excel;

// TemplateWriter(元: AS110AllCheck / XdtConversion の TemplateWriter.cs)の検査。
// 旧 XdtConversion Verification の TemplateWriter 部分(末尾消去・数式列保護・同名停止・セル上限)を、
// 実際の雛形 xlsx の代わりにコードで作った最小の xlsx で確かめる。Excel のインストールは不要。
XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
void Fails<T>(Action action, string name) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"{name}: {typeof(T).Name} が出るべき操作が成功した");
}

string temp = Path.Combine(Path.GetTempPath(), "ExcelTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    string template = Path.Combine(temp, "template.xlsx");
    CreateTemplate(template);

    Check(TemplateWriter.GetSheets(template).SequenceEqual(new[] { "データ", "判定" }), "シート名一覧");
    Check(TemplateWriter.ParseCell("A6") == (1, 6) && TemplateWriter.ParseCell(" xfd1048576 ") == (16384, 1048576), "セル番地の解釈");
    Fails<ArgumentException>(() => TemplateWriter.ParseCell("XFE1"), "列上限");
    Fails<ArgumentException>(() => TemplateWriter.ParseCell("A0"), "行 0");

    string output = Path.Combine(temp, "out", "result.xlsx");
    TemplateWriter.Save(template, output, new[]
    {
        new TransferColumn("データ", "A2", new[] { 1.5, 2.25 }),
        new TransferColumn("データ", "B2", new[] { 10d, 20d, 30d, 40d, 50d, 60d }),
    });
    var cells = ReadCells(output, "xl/worksheets/sheet1.xml");
    Check(Value(cells, "A2") == "1.5" && Value(cells, "A3") == "2.25", "電流列の値");
    Check(Value(cells, "A4") is null && Value(cells, "A5") is null, "過去の末尾値が残った");
    Check(Value(cells, "B7") == "60" && cells["B7"].Attribute("s")?.Value == "3", "新しい行の値と開始セルの書式");
    Check(Value(cells, "A1") == null && cells["A1"].Element(s + "is") is not null, "見出しが変わった");
    Check(cells["C2"].Element(s + "f")?.Value == "A2*2", "数式が変わった");
    using (var before = ZipFile.OpenRead(template))
    using (var after = ZipFile.OpenRead(output))
    {
        Check(before.Entries.Count == after.Entries.Count, "パーツ数が変わった");
        foreach (var entry in before.Entries.Where(e => e.FullName is not ("xl/workbook.xml" or "xl/worksheets/sheet1.xml")))
            Check(ReadBytes(entry).SequenceEqual(ReadBytes(after.GetEntry(entry.FullName)!)), "対象外のパーツが変わった: " + entry.FullName);
        var calc = XDocument.Load(after.GetEntry("xl/workbook.xml")!.Open()).Root!.Element(s + "calcPr")!;
        Check(calc.Attribute("fullCalcOnLoad")?.Value == "1" && calc.Attribute("calcMode")?.Value == "auto", "開いたときの再計算設定");
    }
    Console.WriteLine("PASS TemplateWriter: 指定列だけ書き換え、末尾の旧値を消し、数式・見出し・他パーツを保持、再計算を設定");

    Fails<IOException>(() => TemplateWriter.Save(template, output, new[] { new TransferColumn("データ", "A2", new[] { 1d }) }), "保存先に同名ファイル");
    Fails<ArgumentException>(() => TemplateWriter.Save(template, template, new[] { new TransferColumn("データ", "A2", new[] { 1d }) }), "雛形と保存先が同じ");
    Fails<ArgumentException>(() => TemplateWriter.Save(template, Path.Combine(temp, "f.xlsx"), new[] { new TransferColumn("データ", "C2", new[] { 1d }) }), "数式の列");
    Fails<ArgumentException>(() => TemplateWriter.Save(template, Path.Combine(temp, "g.xlsx"), new[] { new TransferColumn("なし", "A2", new[] { 1d }) }), "存在しないシート");
    Fails<ArgumentException>(() => TemplateWriter.Save(template, Path.Combine(temp, "h.xlsx"), new[]
        { new TransferColumn("データ", "A2", new[] { 1d }), new TransferColumn("データ", "A9", new[] { 2d }) }), "同じ列へ 2 回");
    Fails<InvalidDataException>(() => TemplateWriter.Save(template, Path.Combine(temp, "i.xlsx"), new[] { new TransferColumn("データ", "A2", new[] { double.NaN }) }), "非数値");
    Fails<ArgumentException>(() => TemplateWriter.Save(template, Path.Combine(temp, "j.xlsx"), Array.Empty<TransferColumn>()), "転送対象なし");
    Check(Directory.GetFiles(temp, "*.tmp", SearchOption.AllDirectories).Length == 0, "一時ファイルが残った");
    Console.WriteLine("PASS TemplateWriter: 同名保存・雛形上書き・数式列・存在しないシート・重複列・非数値・空指定を止め、一時ファイルを残さない");
    Console.WriteLine("Excel: all checks passed.");
}
finally { Directory.Delete(temp, true); }

static byte[] ReadBytes(ZipArchiveEntry entry)
{
    using var stream = entry.Open();
    using var memory = new MemoryStream();
    stream.CopyTo(memory);
    return memory.ToArray();
}

Dictionary<string, XElement> ReadCells(string path, string part)
{
    using var zip = ZipFile.OpenRead(path);
    using var stream = zip.GetEntry(part)!.Open();
    return XDocument.Load(stream).Descendants(s + "c").ToDictionary(c => (string)c.Attribute("r")!);
}

string? Value(Dictionary<string, XElement> cells, string address) =>
    cells.TryGetValue(address, out var cell) ? cell.Element(s + "v")?.Value : null;

// 最小の xlsx(シート 2 枚)。データ: A1 見出し(インライン文字列)、A2〜A5 旧値、B2 旧値(書式 3)、C2〜C5 数式。
static void CreateTemplate(string path)
{
    const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    var parts = new Dictionary<string, string>
    {
        ["[Content_Types].xml"] =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/></Types>",
        ["_rels/.rels"] =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"" + rel + "/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>",
        ["xl/workbook.xml"] =
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"{ns}\" xmlns:r=\"{rel}\"><sheets>" +
            "<sheet name=\"データ\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"判定\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>",
        ["xl/_rels/workbook.xml.rels"] =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"" + rel + "/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"" + rel + "/worksheet\" Target=\"worksheets/sheet2.xml\"/></Relationships>",
        ["xl/worksheets/sheet1.xml"] =
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"{ns}\"><dimension ref=\"A1:C5\"/><sheetData>" +
            "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>電流</t></is></c></row>" +
            "<row r=\"2\"><c r=\"A2\"><v>9</v></c><c r=\"B2\" s=\"3\"><v>9</v></c><c r=\"C2\"><f>A2*2</f><v>18</v></c></row>" +
            "<row r=\"3\"><c r=\"A3\"><v>9</v></c><c r=\"C3\"><f>A3*2</f><v>18</v></c></row>" +
            "<row r=\"4\"><c r=\"A4\"><v>9</v></c><c r=\"C4\"><f>A4*2</f><v>18</v></c></row>" +
            "<row r=\"5\"><c r=\"A5\"><v>9</v></c><c r=\"C5\"><f>A5*2</f><v>18</v></c></row>" +
            "</sheetData></worksheet>",
        ["xl/worksheets/sheet2.xml"] =
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"{ns}\"><sheetData>" +
            "<row r=\"1\"><c r=\"A1\"><f>データ!C2</f></c></row></sheetData></worksheet>",
    };
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var (name, xml) in parts)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(xml);
    }
}
