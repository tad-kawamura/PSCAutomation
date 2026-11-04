using System.Diagnostics;
using System.Reflection;

internal static class ProcessLifetimeTests
{
    public static async Task RunAsync(Assembly assembly, Type control, object session)
    {
        var type = assembly.GetType("PSCAutomation.Wrappers.WaveLogger.Automation.LoggerProcessLifetime")!;
        using var target = StartChild();
        using var unrelated = StartChild();
        object lifetime = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { target, Environment.ProcessPath! }, null)!;
        async Task Complete(bool allow) => await (Task)type.GetMethod("CompleteAsync")!
            .Invoke(lifetime, new object[] { allow, (Action<string>)Console.WriteLine })!;
        try
        {
            var pending = control.GetField("_pendingProcess", BindingFlags.Instance | BindingFlags.NonPublic)!;
            pending.SetValue(session, lifetime);
            try
            {
                control.GetMethod("Initialize", new[] { assembly.GetType("PSCAutomation.Wrappers.WaveLogger.Automation.WaveLoggerEdition")!, typeof(Action<string>) })!
                    .Invoke(session, new object?[] { Enum.Parse(assembly.GetType("PSCAutomation.Wrappers.WaveLogger.Automation.WaveLoggerEdition")!, "WaveLoggerX"), null });
                throw new Exception("Initialize accepted a pending old process");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
            { Console.WriteLine("PASS pending old process prevents new COM activation"); }
            finally { pending.SetValue(session, null); }

            try { await Complete(false); throw new Exception("Unverified shutdown succeeded"); }
            catch (InvalidOperationException) { }
            if (target.HasExited || unrelated.HasExited) throw new Exception("Unverified shutdown killed a process");
            Console.WriteLine("PASS unverified stop/quit retains target and unrelated process");

            await Complete(true);
            if (!target.HasExited || unrelated.HasExited) throw new Exception("Wrong termination target");
            Console.WriteLine("PASS only retained target handle terminates; unrelated same-name process survives");
            await Complete(true);
            Console.WriteLine("PASS already exited target completes without another process lookup");
        }
        finally
        {
            // 試験が自ら起動したプロセスのみ、保有ハンドルで片付ける。
            if (!target.HasExited) { target.Kill(); target.WaitForExit(); }
            if (!unrelated.HasExited) { unrelated.Kill(); unrelated.WaitForExit(); }
            ((IDisposable)lifetime).Dispose();
        }
    }

    private static Process StartChild()
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--child");
        var process = Process.Start(info)!;
        _ = process.Handle;
        return process;
    }
}
