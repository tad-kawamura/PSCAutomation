using System.Reflection;

internal static class SessionContractTests
{
    public static async Task RunAsync(Assembly assembly, Type type)
    {
        var app = type.GetField("_application", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var edition = assembly.GetType("PSCAutomation.Wrappers.WaveLogger.Automation.WaveLoggerEdition")!;
        object Create(SessionLogger fake, string version = "WaveLoggerX")
        {
            var session = Activator.CreateInstance(type)!;
            app.SetValue(session, fake);
            type.GetProperty("ActiveEdition")!.SetValue(session, Enum.Parse(edition, version));
            return session;
        }
        object? Call(object session, string method, params object[] args) =>
            type.GetMethod(method)!.Invoke(session, args);
        async Task<long> Start(object session, Action? before = null) =>
            await (Task<long>)Call(session, "StartAsync", (Action<string>)(_ => { }), before ?? (() => { }))!;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

        var first = new SessionLogger();
        var second = new SessionLogger();
        var a = Create(first);
        var b = Create(second);
        await Start(a);
        await Start(a);
        await Start(b);
        Check(first.Starts == 1 && first.Triggers == 2 && second.Starts == 1 && second.Triggers == 1,
            "X start preparation or session isolation changed");
        Call(a, "OpenFile", "template.ydt");
        await Start(a);
        Call(a, "CloseActiveFile");
        await Start(a);
        Check(first.Starts == 3 && second.Starts == 1, "file operations did not reset only the owning session");
        Call(a, "Release");
        Check((bool)type.GetProperty("IsInitialized")!.GetValue(b)!, "releasing one session cleared another");
        Console.WriteLine("PASS isolated sessions; X prepares once and file changes reset preparation");

        second.TriggerResult = -7;
        Check(await Start(b) == -7 && second.Triggers == 2, "failed trigger was retried or hidden");
        try { await Start(b, () => throw new InvalidOperationException("interlock")); throw new Exception("interlock ignored"); }
        catch (InvalidOperationException) { }
        Check(second.Triggers == 2, "trigger sent after interlock failed");
        var legacy = new SessionLogger();
        await Start(Create(legacy, "Legacy"));
        Check(legacy.Starts == 1 && legacy.Triggers == 0, "legacy incorrectly used X manual trigger");
        Console.WriteLine("PASS legacy start, trigger failure without retry, and pre-trigger interlock");

        var single = new SessionLogger();
        var singleSession = Create(single);
        Call(singleSession, "StartSingleAcquisition");
        Call(singleSession, "StartSingleAcquisition");
        Check(single.Starts == 2 && single.Triggers == 0,
            "single acquisition must Start every time without manual Trigger");
        Console.WriteLine("PASS single acquisition starts every time without manual trigger");

        var stateType = assembly.GetType("PSCAutomation.Wrappers.WaveLogger.Automation.LoggerMeasurementState")!;
        var stateTracker = Activator.CreateInstance(stateType)!;
        stateType.GetProperty("MeasurementStarted")!.SetValue(stateTracker, true);
        bool Refresh(long state) => (bool)stateType.GetMethod("Refresh")!.Invoke(stateTracker,
            new object[] { (Func<long>)(() => state), (Action<string>)(_ => { }), false })!;
        Check(!Refresh(3) && !Refresh(10) && !Refresh(12) && Refresh(1),
            "single acquisition completed before fully stopped");
        Console.WriteLine("PASS single acquisition waits through trigger/readout/pending states until stopped");

        // Exercise existing low-level save entry points without writing any measurement files.
        Check((long)Call(b, "GetDataCount")! == 42, "wrong document data count");
        Check((long)Call(b, "SaveXdt", "result.ydt")! == 0, "XDT result changed");
        Check((long)Call(b, "SaveCsv", "result.csv")! == -5, "CSV failure result hidden");
        second.Document.Fail = true;
        try { Call(b, "SaveCsv", "retry.csv"); throw new Exception("save exception hidden"); }
        catch (TargetInvocationException ex) when (ex.InnerException is IOException) { }
        Check((bool)type.GetProperty("IsInitialized")!.GetValue(b)!, "save exception released application");
        Check(second.Document.Paths.SequenceEqual(new[] { "result.ydt", "result.csv", "retry.csv" }), "wrong save target");
        Console.WriteLine("PASS document operations preserve return codes, paths, and connection after failure");

        var reference = new SessionLogger { State = 1 };
        var c = Create(reference);
        bool stopped = false;
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xyb");
        async Task Load(long state) => await (Task)Call(c, "LoadReferenceAsync", missing, (long?)state,
            (Action<string>)(_ => { }), (Action)(() => stopped = true))!;
        try { await Load(4); throw new Exception("reference load accepted active acquisition"); }
        catch (InvalidOperationException) { }
        Check(reference.Stops == 0, "active acquisition stopped unexpectedly");
        try { await Load(3); throw new Exception("missing reference accepted"); }
        catch (FileNotFoundException) { }
        Check(reference.Stops == 1 && stopped, "stop notification lost before file load failure");
        Console.WriteLine("PASS reference load guards acquisition and reports stop before subsequent failure");

        Check(!(bool)Call(c, "IsSessionClosed")!, "unselected startup misidentified as manual close");
        type.GetField("_connectedIdentifier", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(c, 4);
        Check((bool)Call(c, "IsSessionClosed")!, "manual close missed with Visible=true and ID=255");
        reference.Identifier = 4;
        Check(!(bool)Call(c, "IsSessionClosed")!, "connected visible session misidentified as closed");
        reference.Visible = false;
        Check((bool)Call(c, "IsSessionClosed")!, "hidden session not detected");
        Console.WriteLine("PASS close detection distinguishes startup, identifier reset, and visibility");
    }
}

public sealed class SessionLogger
{
    public int Starts, Triggers, Stops;
    public long State = 3, TriggerResult;
    public bool Visible { get; set; } = true;
    public int Identifier = 255;
    public long GetIdentifier() => Identifier;
    public SessionDocument Document { get; } = new();
    public long Start() { Starts++; return 0; }
    public long Trigger() { Triggers++; return TriggerResult; }
    public long GetState() => State;
    public void Stop() { Stops++; }
    public long OpenFile(string path) => 0;
    public long CloseActiveFile() => 0;
    public SessionDocument GetActiveFile() => Document;
}

public sealed class SessionDocument
{
    public bool Fail;
    public List<string> Paths { get; } = new();
    public long GetDataCount() => 42;
    public long SaveFile(string path) { Paths.Add(path); return 0; }
    public long SaveCsvFile(string path)
    {
        Paths.Add(path);
        if (Fail) throw new IOException("simulated write failure");
        return -5;
    }
}
