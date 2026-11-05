using System.Reflection;
using System.Runtime.InteropServices;

// ExcelをインストールしたPCで実行。原本は読み取り専用で開き、保存しない。
if (args.Length < 2) throw new ArgumentException("検査アプリDLLとExcel雛形の絶対パスを指定してください。");
var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
var type = assembly.GetType("PSCAutomation.Apps.AS110AllCheck.ExcelWorkSession")!;
var modelType = assembly.GetType("PSCAutomation.Apps.AS110AllCheck.ProductModel")!;
if (args.Length == 3 && args[2] == "--optimization")
{
    TransferOptimizationTests.Run(assembly, type);
    return;
}
if (args.Length == 4 && args[2] == "--session")
{
    SessionOwnershipTests.Run(assembly, type, modelType, args[1], args[3]);
    return;
}
if (args.Length == 4 && args[2] == "--reuse")
{
    ReuseTests.Run(assembly, type, modelType, args[1], args[3]);
    return;
}
if (args.Length == 4 && args[2] == "--prepare")
{
    await PreparationTests.RunAsync(assembly, type, modelType, args[1], args[3]);
    return;
}
if (args.Length == 4 && args[2] == "--transfer")
{
    await TransferTests.RunAsync(assembly, type, modelType, args[1], args[3]);
    return;
}
object session = Activator.CreateInstance(type, nonPublic: true)!;
dynamic excel = Activator.CreateInstance(Type.GetTypeFromProgID("Excel.Application")!)!;
excel.Visible = true;
dynamic books = excel.Workbooks;
dynamic book = books.Open(Path.GetFullPath(args[1]), UpdateLinks: 0, ReadOnly: true);
try
{
    SessionAccess.Attach(session, (object)excel, (object)books, (object)book);
    foreach(string judgmentSheet in new[]{"sheet1","圧縮5"})
    for (int attempt=0; attempt<2; attempt++)
    {
        type.GetField("_judgmentSheet", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, judgmentSheet);
        book.Worksheets[judgmentSheet=="sheet1" ? "圧縮5" : "sheet1"].Activate();
        if(attempt==1) excel.WindowState=-4140; // xlMinimized
        bool activated=(bool)type.GetMethod("BringToFront")!.Invoke(session,null)!;
        if((string)book.ActiveSheet.Name!=judgmentSheet || (int)excel.ActiveWindow.WindowState==-4140)
            throw new Exception("Transfer result sheet was not selected/restored");
        Console.WriteLine($"PASS Excel judgment sheet={judgmentSheet} selection/restoration (minimized={attempt==1}); foreground accepted={activated}");
    }
    dynamic sheet = book.Worksheets["圧縮5"];
    dynamic range = sheet.Shapes.Item("テキスト ボックス 3").TextFrame2.TextRange;
    string Text() => (string)range.Text;
    string Style(string token)
    {
        int start = token.StartsWith("serial:")
            ? Text().IndexOf(token[7..], Text().IndexOf("S/N", StringComparison.Ordinal), StringComparison.Ordinal)
            : token.StartsWith("pressure:")
                ? Text().IndexOf(token[9..], Text().IndexOf("供給圧力", StringComparison.Ordinal), StringComparison.Ordinal)
                : Text().IndexOf(token, StringComparison.Ordinal);
        if (start < 0) throw new Exception("Missing header token: " + token);
        dynamic font = range.Characters(start + 1, 1).Font;
        return $"{font.Name}|{font.NameFarEast}|{font.Size}|{font.Bold}|{font.UnderlineStyle}";
    }
    var before = new[] { "型式", "AS110-035", "S/N", "serial:74233", "供給圧力", "pressure:539", "測定日", "2026.10.16" }.Select(Style).ToArray();
    var update = type.GetMethod("UpdateHeader", BindingFlags.Instance | BindingFlags.NonPublic)!;
    foreach (var values in new[] { ("AS110-116B350", 350, "1"), ("AS110-015", 588, "0001234567"), ("AS110-035", 539, "1") })
    {
        object model = Activator.CreateInstance(modelType, values.Item1, values.Item2)!;
        update.Invoke(session, new object[] { model, values.Item3, new DateTime(2026, 10, 28) });
        var tokens = new[] { "型式", values.Item1, "S/N", "serial:" + values.Item3, "供給圧力", "pressure:" + values.Item2, "測定日", "2026.10.28" };
        var after = tokens.Select(Style).ToArray();
        for (int i = 0; i < before.Length; i++)
            if (before[i] != after[i]) throw new Exception($"Header formatting changed ({tokens[i]}): {before[i]} => {after[i]}");
        Console.WriteLine("PASS header values and original font/size/bold/underline: " + values.Item1 + " / S/N " + values.Item3);
    }
    // 画面確認用。任意の第3引数で待機し、Enterで閉じる。
    sheet = book.Worksheets["圧縮5"];
    sheet.Activate(); excel.ActiveWindow.Zoom = 80;
    if (args.Length > 2) { Console.WriteLine("READY FOR VISUAL CHECK; press Enter to close"); Console.ReadLine(); }
}
finally
{
    book.Close(false); excel.Quit();
    Marshal.FinalReleaseComObject(book); Marshal.FinalReleaseComObject(books); Marshal.FinalReleaseComObject(excel);
}
