using System.Reflection;
using System.Windows.Forms;

// 実MainFormと専用Excelで、事前準備を開始する条件を確認する。機器には接続しない。
internal static class ExcelPreparationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(Assembly assembly)
    {
        var type = assembly.GetType("PSCAutomation.Apps.AS110AllCheck.MainForm")!;
        var excelType = assembly.GetType("PSCAutomation.Apps.AS110AllCheck.ExcelWorkSession")!;
        using var form = (Form)Activator.CreateInstance(type)!;
        object Field(string name) => type.GetField(name, Private)!.GetValue(form)!;
        object? Excel() => type.GetField("_excel", Private)!.GetValue(form);
        object? Book() => Excel() is { } session ? excelType.GetField("_book", Private)!.GetValue(session) : null;
        void Prepare()
        {
            // 継続処理はUIスレッドへ戻るため、待つ間もメッセージを処理する。
            var task = (Task)type.GetMethod("PrepareExcelWhenIdleAsync", Private)!.Invoke(form, null)!;
            while (!task.IsCompleted) { Application.DoEvents(); Thread.Sleep(10); }
            task.GetAwaiter().GetResult();
        }
        var flow = Field("_flow");
        var flowType = flow.GetType();
        flowType.GetProperty("Mode")!.SetValue(flow, Enum.Parse(assembly.GetType("PSCAutomation.Apps.AS110AllCheck.TestMode")!, "Production"));
        object product = ((Array)assembly.GetType("PSCAutomation.Apps.AS110AllCheck.ProductModel")!.GetField("All")!.GetValue(null)!).GetValue(0)!;
        flowType.GetMethod("Configure")!.Invoke(flow, ["123", product]);
        try
        {
            type.GetField("_operationInProgress", Private)!.SetValue(form, true);
            Prepare();
            if (Excel() is not null) throw new Exception("Excel preparation started during another operation");
            type.GetField("_operationInProgress", Private)!.SetValue(form, false);
            Prepare();
            if (Book() is not { } book) throw new Exception("Idle MainForm did not prepare a work book");
            dynamic app = excelType.GetField("_app", Private)!.GetValue(Excel())!; // Wrappers/Excel の ExcelApplication(Visible を持つ)
            if ((bool)app.Visible || (bool)Field("_operationInProgress") || (bool)Field("_excelPreparing"))
                throw new Exception("Preparation showed Excel or left MainForm busy");
            Console.WriteLine("PASS actual MainForm waits for other operations, then prepares hidden Excel and returns to idle");
            flowType.GetMethod("BeginMeasurement")!.Invoke(flow, [DateTime.Now]);
            flowType.GetMethod("CaptureSucceeded")!.Invoke(flow, null);
            Prepare();
            if (!ReferenceEquals(book, Book())) throw new Exception("Preparation replaced the work book of a captured measurement");
            Console.WriteLine("PASS preparation does not touch Excel while a measurement result exists");
        }
        finally { (Excel() as IDisposable)?.Dispose(); }
    }
}
