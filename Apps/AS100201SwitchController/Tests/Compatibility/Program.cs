// 元: AS100201SwitchController Tests/Compatibility/Program.cs(5eb5b63)。
// 移植で直したのは using・模擬の IInstrumentSession の追加メンバー・IsIncident の呼び方(wrapper で public になった)だけ。
using PSCAutomation.Apps.AS100201SwitchController;
using PSCAutomation.Wrappers.Arduino;
using PSCAutomation.Wrappers.Instruments;
using PSCAutomation.Wrappers.WaveLogger.Automation;
using System.Globalization;
using System.Reflection;

internal static class Program
{
    private static int _passed;
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }

    private static void Pass(string message) { ++_passed; Console.WriteLine("PASS " + message); }

    [STAThread]
    private static void Main()
    {
        GeneratorChecks().GetAwaiter().GetResult();
        LoggerChecks();
        ProtocolAndLogChecks();
        Console.WriteLine($"All {_passed} compatibility groups passed. No device or LOGGER application was opened.");
    }

    private static async Task GeneratorChecks()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        var fake = new FakeInstrument();
        // 移植時に、接続時に波形の形(三角波)も送るようにした(元は OFF・オフセット 0・バースト OFF の 3 つだけ)。
        Wf1973ContinuousSession.InitializeContinuousOutput(fake, As100201WaveformSettings.Waveform);
        Check(fake.Commands.SequenceEqual(new[] { ":OUTPut:STATe OFF", ":SOURce:FUNCtion:SHAPe RAMP", ":SOURce:FUNCtion:RAMP:SYMMetry 50PCT",
            ":SOURce:VOLTage:OFFSet 0", ":SOURce:BURSt:STATe OFF", "*OPC?" }), "connection initialization changed");
        Pass("continuous initialization: OFF, triangle (RAMP 50%), zero offset, burst OFF, *OPC?");
        fake.Commands.Clear();
        using var wf = new Wf1973ContinuousSession(fake);
        Check(await wf.StartWithPhaseAsync(As100201WaveformSettings.Continuous, As100201WaveformSettings.PcPhaseDegrees, () => false), "PC restart failed");
        Check(await wf.StartWithPhaseAsync(As100201WaveformSettings.Continuous, As100201WaveformSettings.CrPhaseDegrees, () => false), "CR restart failed");
        Check(fake.Commands.SequenceEqual(new[] {
            ":SOURce:VOLTage 10.2;PHASe 15;FREQuency:FIXed 0.02",
            ":SOURce:VOLTage:OFFSet 0", ":SOURce:PHASe:INITiate", ":OUTPut:STATe ON",
            ":SOURce:VOLTage 10.2;PHASe 165;FREQuency:FIXed 0.02",
            ":SOURce:VOLTage:OFFSet 0", ":SOURce:PHASe:INITiate" }), "waveform sequence or output toggling changed");
        Pass("PC/CR command order, invariant numbers, repeated restart without OFF/ON");

        for (int boundary = 1; boundary <= 3; boundary++)
        {
            var interrupted = new FakeInstrument();
            using var session = new Wf1973ContinuousSession(interrupted);
            int checks = 0;
            Check(!await session.StartWithPhaseAsync(As100201WaveformSettings.Continuous, 15, () => ++checks == boundary), "superseded restart completed");
            Check(interrupted.Commands.Count == boundary && !interrupted.Commands.Contains(":OUTPut:STATe ON"), "commands sent after cancellation");
            Check(await session.StartWithPhaseAsync(As100201WaveformSettings.Continuous, 165, () => false), "latest request failed");
            Check(interrupted.Commands[^1] == ":OUTPut:STATe ON" && interrupted.Commands[^4].Contains("PHASe 165;"), "latest phase or first output ON lost");
        }
        Pass("supersession at all three yield boundaries followed by latest phase");

        var failed = new FakeInstrument { FailAt = 2 };
        using var failureSession = new Wf1973ContinuousSession(failed);
        try { await failureSession.StartWithPhaseAsync(As100201WaveformSettings.Continuous, 15, () => false); throw new Exception("write error hidden"); }
        catch (IOException) { }
        Check(failed.Commands.Count == 2, "write failure retried");
        failed.FailAt = 0;
        await failureSession.StartWithPhaseAsync(As100201WaveformSettings.Continuous, 165, () => false);
        Check(failed.Commands[^1] == ":OUTPut:STATe ON", "failed start marked output enabled");
        wf.Dispose();
        Check(!wf.IsConnected && fake.Disposals == 1, "transport not disposed once");
        try { wf.QueryIdentity(); throw new Exception("disposed session accepted query"); }
        catch (ObjectDisposedException) { }
        Pass("VISA failures propagate without retry; dispose closes transport");
    }

    private static WaveLoggerSession Session(FakeLogger fake, WaveLoggerEdition edition)
    {
        var session = new WaveLoggerSession { Lifecycle = WaveLoggerLifecycle.ReferenceOnly };
        typeof(WaveLoggerSession).GetField("_application", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, fake);
        typeof(WaveLoggerSession).GetProperty("ActiveEdition")!.SetValue(session, edition);
        return session;
    }

    private static void LoggerChecks()
    {
        foreach (var edition in new[] { WaveLoggerEdition.Legacy, WaveLoggerEdition.WaveLoggerX })
        {
            var fake = new FakeLogger();
            var session = Session(fake, edition);
            Check(session.Start() == 0 && session.Start() == 0, "direct start failed");
            Check(fake.Calls.SequenceEqual(new[] { "Start", "Start" }), "Start performed trigger, polling or preparation");
            fake.StartResult = -7;
            Check(session.Start() == -7, "start error code hidden");
            fake.ThrowStart = true;
            try { session.Start(); throw new Exception("start exception hidden"); }
            catch (IOException) { }
            Check(fake.Calls.Count == 4, "Start automatically retried");
            fake.Calls.Clear();
            session.Stop();
            Check(fake.Calls.SequenceEqual(new[] { "Stop" }), "stop not unconditional");
            fake.Calls.Clear();
            session.Release();
            Check(fake.Calls.Count == 0 && !session.IsInitialized, "Release stopped, quit or retained application");
            Pass($"{edition}: direct Start, return/exception propagation, Stop, release without Quit");

            fake = new FakeLogger();
            session = Session(fake, edition);
            Check(session.OpenFile("template.ydt") == -3, "OpenFile result hidden");
            Check(session.HasActiveFile(), "active file missed");
            Check(session.CloseActiveFile() == -4, "CloseActiveFile result hidden");
            fake.Calls.Clear();
            Check(session.DetectNr500Usb(0).IsDetected, "NR-500 detection failed");
            Check(fake.Calls.SequenceEqual(new[] { "ID:0", edition == WaveLoggerEdition.Legacy ? "Unit:0" : "Unit:0:0" }), "connection added diagnostic state queries");
            session.Release();
            Pass($"{edition}: file return codes and unchanged NR-500 check calls");
        }

        var state = new LoggerMeasurementState { MeasurementStarted = true };
        Check(!state.Refresh(() => 3, _ => { }) && !state.ReadyToStart && state.MeasurementStarted, "manual-trigger wait enabled");
        Check(!state.Refresh(() => 10, _ => { }) && !state.ReadyToStart && state.GetDisplayText(false) == "収集データ読み出し中", "data readout gate/display changed");
        Check(!state.Refresh(() => 12, _ => { }) && !state.ReadyToStart, "residual processing allowed restart");
        Check(state.Refresh(() => 1, _ => { }) && state.ReadyToStart, "completion not detected");
        state.ReadAfter = Environment.TickCount64 + 10000;
        state.Refresh(() => throw new Exception("state read during suppression"), _ => { });
        state.ReadAfter = 0;
        try { state.Refresh(() => throw new IOException(), _ => { }); throw new Exception("state failure hidden"); }
        catch (IOException) { }
        Check(state.State is null, "failed read retained state");
        var logs = new List<string>();
        state.Refresh(() => 11, logs.Add);
        state.Refresh(() => 11, logs.Add);
        Check(state.ReadyToStart && logs.Count == 1, "error-stop gate or repeated log changed");
        Pass("AS100 state gates, completion, read suppression, error handling and display");
    }

    private static void ProtocolAndLogChecks()
    {
        As100201Application.ConfigureDiagnostics();
        foreach (var kind in new[] { "HEARTBEAT_STALE", "FRAME_GAP", "PROTOCOL_RECOVERY", "READER_ERROR", "WRITE_ERROR" })
            Check(ArduinoDiagnosticLog.IsIncident(kind), "incident classification lost: " + kind);
        Check(!ArduinoDiagnosticLog.IsIncident("NF_STALE"), "AS110 NF incident imported");
        Check(ArduinoDiagnosticLog.ApplicationName == "AS100201SwitchController", "log fallback directory changed");
        Check(ArduinoDiagnosticLog.FallbackDirectory.EndsWith(Path.Combine("AS100201SwitchController", "Logs")), "log fallback directory changed");
        Pass("product log directory and incident kinds preserved without NF policy");
    }
}

internal sealed class FakeInstrument : IInstrumentSession
{
    public string Address => "USB0::FAKE::INSTR";
    public string Identity => "NF,WF1973,FAKE";
    public int TimeoutMilliseconds { get; set; } = 2000;
    public string Read() => Identity;
    public List<string> Commands { get; } = new();
    public int Disposals, FailAt;
    public void Write(string command)
    {
        Commands.Add(command);
        if (FailAt == Commands.Count) throw new IOException("fake VISA write failure");
    }
    public string Query(string command) { Write(command); return command == "*OPC?" ? "1" : Identity; }
    public void Dispose() => ++Disposals;
}

// Public for the dynamic COM-compatible calls made from the separate wrapper assembly.
public sealed class FakeLogger
{
    public List<string> Calls { get; } = new();
    public long StartResult;
    public bool ThrowStart;
    public long Start() { Calls.Add("Start"); if (ThrowStart) throw new IOException("fake Start failure"); return StartResult; }
    public void Stop() => Calls.Add("Stop");
    public long Trigger() { Calls.Add("Trigger"); return 0; }
    public long GetState() { Calls.Add("State"); return 3; }
    public void Quit() => Calls.Add("Quit");
    public long OpenFile(string path) { Calls.Add("Open:" + path); return -3; }
    public long CloseActiveFile() { Calls.Add("Close"); return -4; }
    public long GetActiveFileType() => 2;
    public long GetFileCount() => 1;
    public object GetActiveFile() => this;
    public long SetIdentifier(int id) { Calls.Add("ID:" + id); return 0; }
    public long GetUnitInfo(int id) { Calls.Add("Unit:" + id); return 193; }
    public long GetUnitInfo(int group, int unit) { Calls.Add($"Unit:{group}:{unit}"); return 193; }
}
