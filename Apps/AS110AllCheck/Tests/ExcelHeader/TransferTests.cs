using System.Diagnostics;
using System.Reflection;

internal static class TransferTests
{
    public static async Task RunAsync(Assembly assembly, Type type, Type modelType, string template, string captured)
    {
        var settingsType = assembly.GetType("PSCAutomation.Apps.AS110AllCheck.TransferSettings")!;
        object settings = Activator.CreateInstance(settingsType)!;
        object model = Activator.CreateInstance(modelType, "AS110-035", 539)!;
        string output = Path.Combine(Path.GetTempPath(), "as110-transfer-" + Guid.NewGuid() + ".xlsx");
        object? session = null;
        object? app = null;
        try
        {
            for (int run = 1; run <= 2; run++)
            {
                var timer = Stopwatch.StartNew();
                // このテストは判定セルを意図的に編集するため、次回は原本から作り直す。
                if (session is not null) type.GetMethod("CloseVerified")!.Invoke(session, null);
                if (File.Exists(output)) File.Delete(output);
                session = type.GetMethod("Open")!.Invoke(null, new object?[] {
                    template, output, captured, settings, model, "-", DateTime.Now, session, (Action<string>)Console.WriteLine });
                object currentApp = SessionAccess.Application(session)!;
                if (app is not null && !ReferenceEquals(app, currentApp)) throw new Exception("Excel instance was restarted");
                app = currentApp;
                bool pass = await (Task<bool>)type.GetMethod("IsPassAsync")!.Invoke(session, null)!;
                Console.WriteLine($"PASS transfer {run}: calculation completed / judgment={pass} / total={timer.ElapsedMilliseconds}ms");
                Console.WriteLine("READ judgment: "+type.GetProperty("JudgmentDescription")!.GetValue(session));
                dynamic book=SessionAccess.RawBook(session)!;
                dynamic sheet=book.Worksheets["sheet1"];
                Console.WriteLine($"CELL E21: formula=[{sheet.Range["E21"].Formula}] value=[{sheet.Range["E21"].Value2}] text=[{sheet.Range["E21"].Text}] / L21: formula=[{sheet.Range["L21"].Formula}] value=[{sheet.Range["L21"].Value2}] text=[{sheet.Range["L21"].Text}]");
                if(!((string)sheet.Range["E21"].Formula).StartsWith("=IF(") || !((string)sheet.Range["L21"].Formula).StartsWith("=IF("))
                    throw new Exception("Original judgment formulas are missing from sheet1");
                // 判定セルは置換せず、元の数式への入力だけを合格範囲にする。
                sheet.Range["D10"].Value2=250;
                sheet.Range["D13"].Value2=390;
                sheet.Range["D16"].Value2=470;
                sheet.Range["L11"].Value2=3;
                sheet.Range["N11"].Value2=3;
                if (!await (Task<bool>)type.GetMethod("IsPassAsync")!.Invoke(session, null)!) throw new Exception("fresh OK formulas were not calculated");
                sheet=book.Worksheets["sheet1"];
                sheet.Range["D10"].Value2=0;
                if (await (Task<bool>)type.GetMethod("IsPassAsync")!.Invoke(session, null)!) throw new Exception("stale OK accepted after formula change");
                Console.WriteLine("PASS original sheet1 judgment formulas accept valid inputs and reject invalid pressure");
                bool? CurrentPass() => (bool?)type.GetMethod("ReadCurrentPass")!.Invoke(session, null);
                sheet=book.Worksheets["sheet1"];
                sheet.Range["E21"].Value2="OK";
                sheet.Range["L21"].Value2="OK";
                if(CurrentPass()!=true)throw new Exception("monitor did not detect later OK without retransfer");
                sheet=book.Worksheets["sheet1"];
                sheet.Range["L21"].Value2="NG";
                if(CurrentPass()!=false)throw new Exception("monitor kept stale OK after NG");
                sheet=book.Worksheets["sheet1"];
                sheet.Range["L21"].Value2="";
                if(CurrentPass()!=false)throw new Exception("monitor accepted blank judgment");
                sheet=book.Worksheets["sheet1"];
                sheet.Range["L21"].Value2="OK";
                if(CurrentPass()!=true)throw new Exception("monitor did not recover from blank to OK");
                Console.WriteLine("PASS transfer monitor follows OK -> NG -> blank -> OK without retransfer");
            }
        }
        finally { (session as IDisposable)?.Dispose(); if (File.Exists(output)) File.Delete(output); }
    }
}
