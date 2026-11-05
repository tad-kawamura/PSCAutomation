using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PSCAutomation.Apps.AS110AllCheck;
using PSCAutomation.Wrappers.Arduino;
using PSCAutomation.Wrappers.Instruments;

internal static class Program
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private static object? Field(object target,string name)=>target.GetType().GetField(name,Private)!.GetValue(target);
    private static void Set(object target,string name,object? value)=>target.GetType().GetField(name,Private)!.SetValue(target,value);
    private static object? Call(object target,string name,params object[] args)=>target.GetType().GetMethod(name,Private)!.Invoke(target,args);
    private static void Check(bool ok,string message) { if(!ok)throw new Exception(message); }
    private static void PumpUntil(Func<bool> completed)
    {
        var timer=Stopwatch.StartNew();
        while(!completed())
        {
            Application.DoEvents();
            if(timer.Elapsed>TimeSpan.FromSeconds(8))throw new TimeoutException("Form did not close");
            Thread.Sleep(5);
        }
    }

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        foreach(var scenario in new[]{"normal","generator-stop","generator-dispose","logger","all","pending-operation","pending-success","sync-operation","leak-cancel","leak-stop-ok","leak-stop-fail"})
        {
            using var form=new MainForm();
            // Showは呼ばない。OnShownの実機接続を避け、実際のCloseイベントだけを検証。
            ((System.Windows.Forms.Timer)Field(form,"_statusHeartbeat")!).Stop();
            var trace=new List<string>();
            var generator=new FakeGenerator(trace)
            {
                FailStop=scenario is "generator-stop" or "all" or "leak-stop-fail",
                FailDispose=scenario is "generator-dispose" or "all"
            };
            Set(form,"_generator",generator);
            // 漏れ試験のDC +5V出力中に終了するケース。
            if(scenario is "leak-stop-ok" or "leak-stop-fail")Set(form,"_leakOutputReady",true);
            object logger=Field(form,"_logger")!;
            var fakeLogger=new ShutdownLogger(trace) { FailStop=scenario is "logger" or "all" };
            Set(logger,"_application",fakeLogger);
            _=form.Handle;
            int dialogsSeen=0;
            using var watchdog=new System.Windows.Forms.Timer { Interval=50 };
            watchdog.Tick+=(_,_)=>
            {
                // モーダルのメッセージループ中も検出し、回帰時にテストをハングさせない。
                foreach(var dialog in Application.OpenForms.Cast<Form>().Where(f=>f.Modal).ToArray())
                { dialogsSeen++;dialog.Close(); }
            };
            watchdog.Start();
            int closed=0;
            form.FormClosed+=(_,_)=>closed++;
            TaskCompletionSource? pending=null;
            Task? operation=null;
            if(scenario is "pending-operation" or "pending-success")
            {
                pending=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                operation=(Task)Call(form,"RunOperationAsync","mock pending operation",(Func<Task<string>>)(async()=>
                {
                    await pending.Task;
                    if(scenario=="pending-operation")throw new COMException("VISA mock pending error",unchecked((int)0x80040000));
                    return "pending operation completed";
                }))!;
            }
            if(scenario=="sync-operation")
                Call(form,"RunOperation","mock sync operation",(Func<string>)(()=>
                {
                    form.Close();
                    throw new COMException("VISA mock sync error",unchecked((int)0x80040000));
                }));
            if(scenario=="leak-cancel")
                operation=(Task)Call(form,"RunOperationAsync","mock leak",(Func<Task<string>>)(async()=>
                {
                    using var cancel=new CancellationTokenSource();
                    Set(form,"_leakRunning",true);Set(form,"_leakCancel",cancel);
                    try { await Task.Delay(Timeout.Infinite,cancel.Token); }
                    catch(OperationCanceledException) when((bool)Field(form,"_closeAfterLeak")!) { }
                    finally { Set(form,"_leakRunning",false);Set(form,"_leakCancel",null); }
                    return "leak cancelled";
                }))!;
            form.Close();
            form.Close(); // 連打・終了要求の重複で停止・解放を二重実行しない。
            if(pending is not null)
            {
                Check(!form.IsDisposed && trace.Count==0,"Shutdown raced an active operation");
                pending.SetResult();
            }
            PumpUntil(()=>form.IsDisposed);
            Check(dialogsSeen==0,"Shutdown displayed a blocking error dialog");
            Check(closed==1,"Close event did not complete exactly once");
            Check(trace.Count(t=>t=="generator-stop")==1,"Generator stop was skipped or repeated");
            Check(trace.Count(t=>t=="generator-dispose")==1,"Generator dispose was skipped or repeated");
            Check(trace.IndexOf("generator-stop")<trace.IndexOf("logger-stop"),"Logger step did not follow output stop attempt");
            Check(trace.IndexOf("logger-stop")<trace.IndexOf("generator-dispose"),"Later cleanup was skipped");
            Check(Field(logger,"_application") is null,"Logger reference was not released after a failure");
            Check((bool)Field(Field(form,"_arduino")!,"_disposed")!,"Arduino cleanup was skipped");
            Check((bool)Field(form,"_shutdownComplete")!,"Shutdown completion was not recorded");
            int errors=(int)Field(form,"_shutdownErrors")!;
            bool expectedFailure=scenario is not ("normal" or "pending-success" or "leak-cancel" or "leak-stop-ok");
            Check(expectedFailure ? errors>0 : errors==0,"Failure was falsely reported as successful shutdown");
            Check(operation is null || operation.IsCompletedSuccessfully,"Pending operation did not return without a modal");
            if(errors>0)
            {
                string log=File.ReadAllText(ArduinoDiagnosticLog.PathName);
                Check(log.Contains("SHUTDOWN_ERROR") && log.Contains("失敗・未確認"),"Shutdown error was not persisted");
            }
            if(scenario=="leak-stop-ok")
                Check(!(bool)Field(form,"_leakOutputReady")! && !(bool)Field(form,"_generatorOutputUnknown")!,"Output state was not cleared after a successful stop");
            if(scenario=="leak-stop-fail")
                Check((bool)Field(form,"_leakOutputReady")! && (bool)Field(form,"_generatorOutputUnknown")!,"Failed output stop was shown as OFF");
            Console.WriteLine($"PASS real MainForm.Close {scenario}: closed={closed}, errors={errors}, trace={string.Join(',',trace)}");
        }
    }
}

internal sealed class FakeGenerator(List<string> trace) : IFunctionGeneratorSession
{
    public bool FailStop,FailDispose;
    public string Identity=>"fake";
    public string PreparationSummary=>"fake";
    public bool IsConnected=>true;
    public string QueryIdentity()=>Identity;
    public void PrepareBurst(GeneratorBurstSettings settings)=>throw new Exception("Unexpected hardware preparation");
    public void TriggerBurst()=>throw new Exception("Unexpected trigger");
    public void SetDcOutput(double volts)=>throw new Exception("Unexpected output ON");
    public void StopOutput()
    {
        trace.Add("generator-stop");
        if(FailStop)throw new COMException("VISA mock disconnected",unchecked((int)0x80040000));
    }
    public void Dispose()
    {
        trace.Add("generator-dispose");
        if(FailDispose)throw new IOException("VISA mock dispose error");
    }
}

public sealed class ShutdownLogger(List<string> trace)
{
    public bool FailStop;
    public bool Visible { get; private set; }=true;
    public long GetIdentifier()=>0;
    public long GetFileCount()=>Visible ? 1 : 0;
    public long GetState()=>1;
    public void Stop()
    {
        trace.Add("logger-stop");
        if(FailStop)throw new COMException("LOGGER mock disconnected",unchecked((int)0x80004005));
    }
    public void Quit() { trace.Add("logger-quit");Visible=false; }
}
