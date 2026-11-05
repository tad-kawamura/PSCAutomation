using System.Reflection;
using System.Runtime.InteropServices;

// Excel・Arduinoを起動せず、書込範囲・計算依存順・例外時の描画設定復元を検証する。
internal static class TransferOptimizationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run(Assembly assembly, Type type)
    {
        foreach (var (current, pressure, writes) in new[] { ("A4", "B4", 1), ("B4", "A4", 1), ("A4", "D8", 2) })
        foreach (bool initialScreenUpdating in new[] { true, false })
        {
            object session = Activator.CreateInstance(type, nonPublic: true)!;
            var app = new OptimizationExcel { ScreenUpdating = initialScreenUpdating };
            var book = new OptimizationBook();
            SessionAccess.Attach(session, app, null, book);
            object settings = Activator.CreateInstance(assembly.GetType("PSCAutomation.Apps.AS110AllCheck.TransferSettings")!)!;
            settings.GetType().GetProperty("CurrentCell")!.SetValue(settings, current);
            settings.GetType().GetProperty("PressureCell")!.SetValue(settings, pressure);
            void Update(double[] values) => type.GetMethod("UpdateMeasurements", Private)!.Invoke(session,
                [settings, values, Enumerable.Range(0, values.Length).Select(i => 500d + i).ToArray()]);
            var data = book.Worksheets["sheet1"];
            data.Cells[(3000, 30)] = "untouched";
            data.CurrentAddress = current;
            double[] initial = Enumerable.Range(0, 1300).Select(i => 1d + i / 1000d).ToArray();
            Update(initial);
            Check(data.Writes == writes && data.Read(current) is double c && c == 1 &&
                data.Read(pressure) is double p && p == 500 && Equals(data.Cells[(3000, 30)], "untouched"),
                $"transfer values/ranges preserved: {current}/{pressure}, writes={writes}");
            var currentStart = OptimizationRanges.Parse(current);
            var pressureStart = OptimizationRanges.Parse(pressure);
            for (int i = 0; i < initial.Length; i++)
            {
                CheckValue(data.Cells[(currentStart.Row + i, currentStart.Column)], initial[i]);
                CheckValue(data.Cells[(pressureStart.Row + i, pressureStart.Column)], 500d + i);
            }
            Console.WriteLine("PASS all 2600 current/pressure cells match input arrays");
            Check(app.ScreenUpdating == initialScreenUpdating, "screen updating restored after success");
            bool Pass() => ((Task<bool>)type.GetMethod("IsPassAsync")!.Invoke(session, null)!).GetAwaiter().GetResult();
            Check(Pass(), "judgment reads freshly calculated passing values");
            Check(book.Trace.SequenceEqual(new[] { "sheet1", "圧縮5" }), "input calculated first, every sheet exactly once");
            book.Trace.Clear();
            Update([0, 2]);
            Check(!Pass(), "changed measurements produce NG instead of stale OK");
            Check(book.Trace.SequenceEqual(new[] { "sheet1", "圧縮5" }), "calculation order preserved on retransfer");
            data.FailWrite = true;
            try { Update([1, 2]); throw new Exception("write failure was swallowed"); }
            catch (TargetInvocationException ex) when (ex.InnerException is COMException) { }
            Check(app.ScreenUpdating == initialScreenUpdating, "screen updating restored after write failure");
            data.FailWrite = false;
            data.ProtectedFormula = true;
            int before = data.Writes;
            try { Update([1, 2]); throw new Exception("formula protection was lost"); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { }
            Check(data.Writes == before && app.ScreenUpdating == initialScreenUpdating,
                "formula protection rejects transfer before any write");
        }
    }
    private static void CheckValue(object actual, double expected)
    {
        if (Convert.ToDouble(actual) != expected) throw new Exception("Transferred value differs from input");
    }
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
}

public sealed class OptimizationExcel
{
    public bool ScreenUpdating { get; set; }
    public int CalculationState => 0;
}
public sealed class OptimizationBook
{
    public bool ReadOnly => false;
    public string FullName => "optimization.xlsx";
    public List<string> Trace { get; } = new();
    public OptimizationSheets Worksheets { get; }
    public OptimizationBook() => Worksheets = new OptimizationSheets(Trace);
}
public sealed class OptimizationSheets
{
    private readonly OptimizationSheet[] _sheets;
    public OptimizationSheets(List<string> trace) => _sheets =
        [new("圧縮5", trace), new("sheet1", trace)];
    public int Count => _sheets.Length;
    [System.Runtime.CompilerServices.IndexerName("At")]
    public OptimizationSheet this[int index] => _sheets[index - 1];
    [System.Runtime.CompilerServices.IndexerName("At")]
    public OptimizationSheet this[string name] => _sheets.Single(s => s.Name == name);
    // 部品(ExcelWorkbook.Sheet・CalculateSheets)はシートを Item(名前) で取る。
    public OptimizationSheet Item(string name) => this[name];
}
public sealed class OptimizationSheet(string name, List<string> trace)
{
    public string Name => name;
    public Dictionary<(int Row, int Column), object> Cells { get; } = new();
    public int Writes;
    public bool FailWrite, ProtectedFormula;
    public string CurrentAddress = "A4";
    public OptimizationRanges Range => new(this);
    public object? Read(string address) => Cells.GetValueOrDefault(OptimizationRanges.Parse(address));
    public void Calculate()
    {
        trace.Add(name);
        if (name == "sheet1")
        {
            Cells[(21, 5)] = Convert.ToDouble(Read(CurrentAddress)) > 0 ? "OK" : "NG";
            Cells[(21, 12)] = "OK";
        }
    }
}
public sealed class OptimizationRanges(OptimizationSheet sheet)
{
    public static (int Row, int Column) Parse(string address)
    {
        // 部品は数式の確認に「A4:A1303」の形の範囲を渡す。模擬では左上だけを見る。
        address = address.Split(':')[0];
        int column = 0, i = 0;
        while (i < address.Length && char.IsLetter(address[i])) column = column * 26 + address[i++] - 'A' + 1;
        return (int.Parse(address[i..]), column);
    }
    public OptimizationRange this[string address] => new(sheet, Parse(address), 1, 1);
}
public sealed class OptimizationRange(OptimizationSheet sheet, (int Row, int Column) start, int rows, int columns)
{
    public OptimizationResize Resize => new(sheet, start);
    public bool HasFormula => sheet.ProtectedFormula;
    public object? Value2
    {
        get => sheet.Cells.GetValueOrDefault(start);
        set
        {
            if (sheet.FailWrite) throw new COMException("write rejected", unchecked((int)0x80010001));
            sheet.Writes++;
            var values = (object[,])value!;
            for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++) sheet.Cells[(start.Row + r, start.Column + c)] = values[r, c];
        }
    }
}
public sealed class OptimizationResize(OptimizationSheet sheet, (int Row, int Column) start)
{
    public OptimizationRange this[int rows, int columns] => new(sheet, start, rows, columns);
}
