using System.Reflection;
using System.Runtime.InteropServices;
// 元: AS110AllCheck 他 Tests/LoggerShutdown。旧版はアプリ出力先の DLL をパス指定で読んでいたが、ここでは ProjectReference した wrapper を直接読む。
// 実機・LOGGER 本体ソフトは使わない(COM オブジェクトの代わりに下の FakeLogger 等を差し込む)。
if (args.Length == 1 && args[0] == "--child") { Thread.Sleep(60000); return; }
var assembly = typeof(PSCAutomation.Wrappers.WaveLogger.Automation.WaveLoggerSession).Assembly;
var control = assembly.GetType("PSCAutomation.Wrappers.WaveLogger.Automation.WaveLoggerSession")!;
var appField = control.GetField("_application", BindingFlags.Instance | BindingFlags.NonPublic)!;
var idField = control.GetField("_connectedIdentifier", BindingFlags.Instance | BindingFlags.NonPublic)!;
var edition = assembly.GetType("PSCAutomation.Wrappers.WaveLogger.Automation.WaveLoggerEdition")!;
var session = Activator.CreateInstance(control)!;
async Task Run(string name, FakeLogger fake, bool success, bool disconnected = false)
{
    appField.SetValue(session, fake);
    idField.SetValue(session, 4);
    control.GetProperty("ActiveEdition")!.SetValue(session, Enum.Parse(edition, "WaveLoggerX"));
    var logs = new List<string>();
    Exception? error = null;
    try { await (Task)control.GetMethod("ShutdownAsync")!.Invoke(session, new object[] { (Action<string>)logs.Add })!; }
    catch (Exception ex) { error = ex; }
    if ((error is null) != success) throw new Exception(name + ": unexpected result", error);
    if (success != (appField.GetValue(session) is null)) throw new Exception(name + ": wrong reference retention");
    if (!disconnected && fake.Trace.IndexOf("ID4") > fake.Trace.IndexOf("Stop")) throw new Exception(name + ": stopped before restoring ID");
    if (success && !disconnected && fake.Trace.IndexOf("Stop") > fake.Trace.IndexOf("Quit")) throw new Exception(name + ": quit before stop command");
    Console.WriteLine("PASS " + name + " / " + string.Join(",", fake.Trace));
}
await Run("restore ID, verify hardware, stop, quit and release", new(), true);
await Run("asynchronous Quit waits for documents and visibility", new() { DeferredQuit = true }, true);
await Run("failed stop retains old reference and prevents quit", new() { InvalidState = true }, false);
await Run("failed Quit verification retains old reference", new() { QuitFails = true }, false);
await Run("already disconnected COM releases stale reference", new() { Disconnected = true }, true, true);
await ProcessLifetimeTests.RunAsync(assembly, control, session);
await SessionContractTests.RunAsync(assembly, control);
await MergedApiTests.RunAsync(control);
await PrestartTests.RunAsync(control);
Console.WriteLine("WaveLogger.Automation: all checks passed.");
public class FakeLogger
{
    public List<string> Trace = new();
    public bool InvalidState, QuitFails, Disconnected, DeferredQuit; private int quitPolls; private bool quitRequested;
    public bool Visible { get; set; } = true;
    int id = 255, count = 1, calls;
    bool stopping;
    long state = 3;
    public long GetIdentifier() => id; public long GetUnitInfo(int group, int unit) { Trace.Add("Unit198"); return 198; }
    public long SetIdentifier(int value) { Trace.Add("ID" + value); id = value; return 0; }
    public long GetActiveFileType() => count == 0 ? 0 : 2;
    public long GetFileCount() { if (quitRequested && DeferredQuit && ++quitPolls > 2) { count = 0; Visible = false; } return count; }
    public void Stop() { Trace.Add("Stop"); if (Disconnected) throw new COMException("server disconnected", unchecked((int)0x80010108)); stopping = true; }
    public long GetState()
    {
        if (stopping) state = InvalidState ? -2 : ++calls switch { 1 => 11, 2 => 12, _ => 1 };
        Trace.Add("State" + state); return state;
    }
    public void Quit()
    {
        if (!stopping) throw new Exception("Quit invoked before Stop");
        Trace.Add("Quit"); quitRequested = true; if (!QuitFails && !DeferredQuit) { count = 0; Visible = false; }
    }
}
