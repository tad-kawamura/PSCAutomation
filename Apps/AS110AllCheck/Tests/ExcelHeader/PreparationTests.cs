using System.Diagnostics;
using System.Reflection;

// 専用Excelと一時ファイルのみ使用。事前準備したブックへの初回転送が、準備なしの転送と同じ結果になるか確認する。
internal static class PreparationTests
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    public static async Task RunAsync(Assembly assembly,Type type,Type modelType,string template,string captured)
    {
        string folder=Path.Combine(Path.GetTempPath(),"as110-prepare-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        object settings=Activator.CreateInstance(assembly.GetType("PSCAutomation.Apps.AS110AllCheck.TransferSettings")!)!;
        object model=Activator.CreateInstance(modelType,"AS110-035",539)!;
        string serial="123";
        var date=DateTime.Today;
        var logs=new List<string>();
        Action<string> log=line=>{logs.Add(line);Console.WriteLine(line);};
        object? session=null;
        object? Book()=>SessionAccess.Workbook(session);
        dynamic App()=>SessionAccess.Application(session)!; // ExcelApplication の Visible を見る
        void Check(bool value,string message)
        {
            if(!value)throw new Exception(message);
            Console.WriteLine("PASS "+message);
        }
        async Task<object> Prepare(object? reusable)
        {
            var task=(Task)type.GetMethod("PrepareAsync")!.Invoke(null,[template,Path.Combine(folder,"work.xlsx"),settings,model,serial,reusable,log])!;
            await task;
            return task.GetType().GetProperty("Result")!.GetValue(task)!;
        }
        async Task<(double Ms,bool Pass,string Judgment,string Cells,string Header)> Transfer()
        {
            logs.Clear();
            var timer=Stopwatch.StartNew();
            session=type.GetMethod("Open")!.Invoke(null,[template,Path.Combine(folder,"work.xlsx"),captured,settings,model,serial,date,session,log])!;
            bool pass=await (Task<bool>)type.GetMethod("IsPassAsync")!.Invoke(session,null)!;
            double ms=timer.Elapsed.TotalMilliseconds;
            dynamic book=SessionAccess.RawBook(session)!;
            dynamic sheet=book.Worksheets["sheet1"];
            var values=(Array)sheet.Range["A1:AC1628"].Value2;
            var text=new System.Text.StringBuilder();
            foreach(object? value in values)text.Append(Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture)).Append('|');
            string header=(string)book.Worksheets["圧縮5"].Shapes.Item("テキスト ボックス 3").TextFrame2.TextRange.Text;
            return (ms,pass,(string)type.GetProperty("JudgmentDescription")!.GetValue(session)!,text.ToString(),header);
        }
        try
        {
            var plain=await Transfer();
            Console.WriteLine($"TRANSFER without preparation={plain.Ms:F0}ms pass={plain.Pass}");
            ((IDisposable)session!).Dispose();session=null;

            var timer=Stopwatch.StartNew();
            session=await Prepare(null);
            double prepared=timer.Elapsed.TotalMilliseconds;
            Check(Book() is not null && !(bool)App().Visible,"preparation opens work book without showing Excel");
            object book=Book()!;

            var after=await Transfer();
            Console.WriteLine($"TRANSFER after preparation={after.Ms:F0}ms pass={after.Pass}");
            Check(ReferenceEquals(book,Book()) && logs.Any(x=>x.Contains("ブック再利用")),"first transfer reuses prepared work book");
            Check((bool)App().Visible,"transfer shows Excel");
            Check(after.Pass==plain.Pass && after.Judgment==plain.Judgment,"judgment matches transfer without preparation");
            Check(after.Cells==plain.Cells,"all sheet1 values A1:AC1628 match transfer without preparation");
            Check(after.Header==plain.Header,"header matches transfer without preparation");

            logs.Clear();
            session=await Prepare(session);
            Check(ReferenceEquals(book,Book()) && logs.Any(x=>x.Contains("準備済み")),"repeated preparation keeps existing work book");
            Console.WriteLine($"TIMING withoutPreparation={plain.Ms:F0}ms preparation={prepared:F0}ms afterPreparation={after.Ms:F0}ms");
        }
        finally
        {
            (session as IDisposable)?.Dispose();
            Directory.Delete(folder,true);
        }
    }
}
