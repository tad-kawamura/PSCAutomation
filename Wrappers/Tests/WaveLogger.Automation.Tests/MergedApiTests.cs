using System.Reflection;
using PSCAutomation.Wrappers.WaveLogger.Automation;

// モノレポへの統合で AS100201SwitchController 版から足した API の検査(2026-11-04 追加)。
internal static class MergedApiTests
{
    public static async Task RunAsync(Type type)
    {
        var app = type.GetField("_application", BindingFlags.Instance | BindingFlags.NonPublic)!;
        WaveLoggerSession Create(SessionLogger fake, WaveLoggerEdition edition = WaveLoggerEdition.WaveLoggerX,
            WaveLoggerLifecycle? lifecycle = null)
        {
            var session = lifecycle is { } l ? new WaveLoggerSession { Lifecycle = l } : new WaveLoggerSession();
            app.SetValue(session, fake);
            type.GetProperty("ActiveEdition")!.SetValue(session, edition);
            return session;
        }
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

        Check(new WaveLoggerSession().Lifecycle == WaveLoggerLifecycle.ManagedShutdown, "default lifecycle changed");
        Check(Create(new SessionLogger(), lifecycle: WaveLoggerLifecycle.ReferenceOnly).Lifecycle == WaveLoggerLifecycle.ReferenceOnly,
            "lifecycle init not kept");
        Console.WriteLine("PASS lifecycle defaults to ManagedShutdown and ReferenceOnly can be selected");

        foreach (var edition in new[] { WaveLoggerEdition.WaveLoggerX, WaveLoggerEdition.Legacy })
        {
            var plain = new SessionLogger();
            var session = Create(plain, edition);
            Check(session.Start() == 0 && session.Start() == 0, "Start return value changed");
            Check(plain.Starts == 2 && plain.Triggers == 0, $"{edition}: Start must call Start once per call without Trigger");
        }
        Console.WriteLine("PASS Start issues exactly one Start per call without Trigger (X and legacy)");

        // Start() は X 版の開始準備(初回 Start 済み)をリセットしない。StartSingleAcquisition() はリセットする。
        var keep = new SessionLogger();
        var keepSession = Create(keep);
        await keepSession.StartAsync(_ => { }, () => { });
        keepSession.Start();
        await keepSession.StartAsync(_ => { }, () => { });
        Check(keep.Starts == 2 && keep.Triggers == 2, "Start reset X start preparation");
        var reset = new SessionLogger();
        var resetSession = Create(reset);
        await resetSession.StartAsync(_ => { }, () => { });
        resetSession.StartSingleAcquisition();
        await resetSession.StartAsync(_ => { }, () => { });
        Check(reset.Starts == 3 && reset.Triggers == 2, "StartSingleAcquisition did not reset X start preparation");
        Console.WriteLine("PASS Start keeps X preparation; StartSingleAcquisition resets it");

        var state = new LoggerMeasurementState { State = 10 };
        Check(state.DisplayText == "収集データ読み出し中（本体停止）", "DisplayText for state 10 changed");
        Check(state.GetDisplayText() == state.DisplayText, "GetDisplayText default differs from DisplayText");
        Check(state.GetDisplayText(false) == "収集データ読み出し中", "GetDisplayText(false) for state 10");
        state.State = 4;
        Check(state.GetDisplayText(false) == state.DisplayText, "describeHardwareStop affected another state");
        Console.WriteLine("PASS state 10 text can omit hardware stop; other states unchanged");
    }
}
