using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using PSCAutomation.Apps.XdtConversion;
using PSCAutomation.Wrappers.Excel;
using PSCAutomation.Wrappers.LoggerFiles;

// XdtConversion アプリの検証(元: XdtConversion Verification/Program.cs)。GUI と Excel COM は使わない。
// xdt の全点照合は Wrappers/Tests/LoggerFiles.Tests、TemplateWriter 単体は Wrappers/Tests/Excel.Tests でも確かめている。
// ここではアプリの ConversionService(間引き・CH とセルの対応)を、同梱の雛形 xlsx と実際の xdt で通す。
//
// 旧検証は「雛形の Sheet1 の A6 以降に前回の転送値が入っている」前提で、新旧の値を比べていた。
// 同梱の雛形.xlsx は A6 以降が空なので、A6 で KeyNotFoundException になって止まっていた(2026-10-26 の記録)。
// ここでは比較の相手を、独立の参照値(Fixtures/PC.bin・CR.bin、メーカーソフトで出した全点の値)を 10 点おきに取ったものにした。
//
// 使い方(リポジトリのルートで):
//   dotnet run --project Apps/XdtConversion/Verification -c Release
//   dotnet run --project Apps/XdtConversion/Verification -c Release -- --compare-old <旧アプリが出力した xlsx>
//     旧アプリの出力(既定の転送設定・間引き 10)と、転送した値が一致するかも確かめる。

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static void MustFail(Action action, string name)
{
    bool failed = false;
    try { action(); } catch (ArgumentException) { failed = true; } catch (InvalidDataException) { failed = true; } catch (IOException) { failed = true; }
    Check(failed, "エラーになるべき操作が成功：" + name);
}

XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
XNamespace pr = "http://schemas.openxmlformats.org/package/2006/relationships";

string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures");
string template = Path.Combine(AppContext.BaseDirectory, "template.xlsx");
const int Stride = 10;

// 既定の転送設定(MainForm の初期値と同じ)
var mappings = new[] { new Mapping("PC", 5, "Sheet1", "A6"), new Mapping("PC", 6, "Sheet1", "B6"),
    new Mapping("CR", 5, "Sheet1", "H6"), new Mapping("CR", 6, "Sheet1", "I6") };
var sources = new Dictionary<string, string> { ["PC"] = Path.Combine(fixture, "PC.xdt"), ["CR"] = Path.Combine(fixture, "CR.xdt") };
var reference = new Dictionary<string, Dictionary<int, double[]>>
{
    ["PC"] = ReadReference(Path.Combine(fixture, "PC.bin")),
    ["CR"] = ReadReference(Path.Combine(fixture, "CR.bin")),
};
// 参照値と xdt の読み取りが合っていること(LoggerFiles.Tests と同じ照合。ここでは前提の確認)
foreach (var (name, channels) in reference)
{
    var result = XdtReader.Read(sources[name], channels.Keys);
    foreach (var (ch, expected) in channels)
    {
        double max = result.Values[ch].Zip(expected, (a, b) => Math.Abs(a - b)).Max();
        Check(result.Values[ch].Length == expected.Length && max <= 0.0002, $"{name} CH{ch} の読み取りが参照値と合わない {max}");
    }
}
Console.WriteLine("PC / CR：xdt の読み取りが参照値と一致(前提)");

string temp = Path.Combine(Path.GetTempPath(), "XdtConversionVerify-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    string output = Path.Combine(temp, "result.xlsx");
    var report = ConversionService.Convert(template, output, sources, mappings, Stride);
    Check(report.Details.Length == 4, "変換の明細が 4 行でない");
    string sheetPart = SheetPart(template, "Sheet1");
    using (var before = ZipFile.OpenRead(template))
    using (var after = ZipFile.OpenRead(output))
    {
        Check(before.Entries.Count == after.Entries.Count, "Excelパーツ数相違");
        foreach (var entry in before.Entries.Where(e => e.FullName != "xl/workbook.xml" && e.FullName != sheetPart))
            Check(ReadBytes(entry).SequenceEqual(ReadBytes(after.GetEntry(entry.FullName)!)), "非対象パーツを変更：" + entry.FullName);
        var oldCells = Cells(before, sheetPart);
        var newCells = Cells(after, sheetPart);
        foreach (var cell in oldCells.Values.Where(c => c.Element(s + "f") != null))
            Check(XNode.DeepEquals(cell.Element(s + "f"), newCells[(string)cell.Attribute("r")!].Element(s + "f")), "数式を変更");
        foreach (var mapping in mappings)
        {
            var expected = reference[mapping.Source][mapping.Channel];
            int count = (expected.Length - 1) / Stride + 1;
            var (_, firstRow) = TemplateWriter.ParseCell(mapping.StartCell);
            string letter = mapping.StartCell.TrimEnd("0123456789".ToCharArray());
            for (int i = 0; i < count; i++)
            {
                string address = letter + (firstRow + i);
                var v = newCells.TryGetValue(address, out var cell) ? cell.Element(s + "v") : null;
                Check(v is not null, $"{mapping.Source} CH{mapping.Channel}：{address} に値がない");
                double actual = double.Parse(v!.Value, CultureInfo.InvariantCulture);
                Check(Math.Abs(actual - expected[i * Stride]) <= 0.0002, $"{mapping.Source} CH{mapping.Channel}：{address} が参照値と違う {actual} / {expected[i * Stride]}");
            }
            string next = letter + (firstRow + count);
            Check(!newCells.TryGetValue(next, out var after1) || after1.Element(s + "v") is null, $"{next} に値が残った");
            Check(count == 5000, $"{mapping.Source} CH{mapping.Channel}：転送点数 {count}(50,000 点を間引き 10 で 5,000 点のはず)");
        }
    }
    Console.WriteLine("間引き10・PC/CR各5,000点を参照値と照合・数式／グラフパーツ保持 OK");

    if (args.Length == 2 && args[0] == "--compare-old")
    {
        // 旧アプリ(移植前)が同じ xdt・同じ設定で出力した xlsx と、転送した列の値を比べる
        string old = Path.GetFullPath(args[1]);
        Check(File.Exists(old), "旧アプリの出力がない：" + old);
        using var oldZip = ZipFile.OpenRead(old);
        using var newZip = ZipFile.OpenRead(output);
        var oldCells = Cells(oldZip, SheetPart(old, "Sheet1"));
        var newCells = Cells(newZip, sheetPart);
        int compared = 0;
        foreach (string letter in new[] { "A", "B", "H", "I" })
            for (int row = 6; row <= 5005; row++, compared++)
                Check(oldCells[letter + row].Element(s + "v")!.Value == newCells[letter + row].Element(s + "v")!.Value,
                    $"旧アプリの出力と違う：{letter}{row}");
        Console.WriteLine($"旧アプリの出力と転送値 {compared:N0} セルが文字列として一致 OK");
    }

    string shortOutput = Path.Combine(temp, "short.xlsx");
    TemplateWriter.Save(template, shortOutput, new[] { new TransferColumn("Sheet1", "A6", new[] { 1d, 2d }) });
    using (var zip = ZipFile.OpenRead(shortOutput))
    {
        var cells = Cells(zip, sheetPart);
        Check(cells["A6"].Element(s + "v")!.Value == "1" && cells["A7"].Element(s + "v")!.Value == "2", "先頭値相違");
        Check(!cells.TryGetValue("A8", out var a8) || a8.Element(s + "v") == null, "過去の末尾値が残る");
    }
    MustFail(() => ConversionService.Convert(template, output, sources, mappings, Stride), "同名上書き");
    MustFail(() => ConversionService.Convert(template, Path.Combine(temp, "stride0.xlsx"), sources, mappings, 0), "間引き数 0");
    MustFail(() => ConversionService.Convert(template, Path.Combine(temp, "nocr.xlsx"),
        new Dictionary<string, string> { ["PC"] = sources["PC"], ["CR"] = "" }, mappings, Stride), "CR の xdt 未指定");
    MustFail(() => TemplateWriter.Save(template, Path.Combine(temp, "formula.xlsx"), new[] { new TransferColumn("Sheet1", "C6", new[] { 1d }) }), "数式列保護");
    MustFail(() => XdtReader.Read(sources["PC"], new[] { 3 }), "不存在CH");
    MustFail(() => TemplateWriter.ParseCell("XFE1"), "列上限");
    string truncated = Path.Combine(temp, "truncated.xdt"); File.WriteAllBytes(truncated, File.ReadAllBytes(sources["PC"])[..^1]);
    MustFail(() => XdtReader.Inspect(truncated), "破損ファイル");
    Console.WriteLine("末尾消去・同名停止・間引き数 0・xdt 未指定・数式列保護・不正CH・破損ファイル検出 OK");
}
finally { Directory.Delete(temp, true); }
Console.WriteLine("全検証成功。Excel COM（ExcelPresenter）と GUI は別途 Windows の実機で確認してください。");

// 参照値: int 点数、int CH 数、(int CH 番号、double × 点数) × CH 数
static Dictionary<int, double[]> ReadReference(string path)
{
    using var gold = new BinaryReader(File.OpenRead(path));
    int n = gold.ReadInt32(), count = gold.ReadInt32();
    var values = new Dictionary<int, double[]>();
    for (int j = 0; j < count; j++)
    {
        int ch = gold.ReadInt32();
        var column = new double[n];
        for (int i = 0; i < n; i++) column[i] = gold.ReadDouble();
        values.Add(ch, column);
    }
    return values;
}

static byte[] ReadBytes(ZipArchiveEntry entry)
{
    using var stream = entry.Open();
    using var memory = new MemoryStream();
    stream.CopyTo(memory);
    return memory.ToArray();
}

Dictionary<string, XElement> Cells(ZipArchive zip, string part)
{
    using var stream = zip.GetEntry(part)!.Open();
    return XDocument.Load(stream).Descendants(s + "c").ToDictionary(c => (string)c.Attribute("r")!);
}

// シート名からワークシートのパーツ名を引く(旧検証は sheet2.xml を直書きしていた)
string SheetPart(string path, string sheetName)
{
    using var zip = ZipFile.OpenRead(path);
    XDocument workbook, rels;
    using (var stream = zip.GetEntry("xl/workbook.xml")!.Open()) workbook = XDocument.Load(stream);
    using (var stream = zip.GetEntry("xl/_rels/workbook.xml.rels")!.Open()) rels = XDocument.Load(stream);
    string id = (string)workbook.Descendants(s + "sheet").Single(x => (string)x.Attribute("name")! == sheetName).Attribute(r + "id")!;
    string target = (string)rels.Descendants(pr + "Relationship").Single(x => (string)x.Attribute("Id")! == id).Attribute("Target")!;
    return target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
}
