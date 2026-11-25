using PSCAutomation.Apps.AS110AllCheck;

// 返ってこない機器の呼び出しを見切る部品(BlockingCallProbe。2026-11-25、WF1973 の接続で画面が固まった件)の確かめ。
internal static class BlockingCallProbeTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    public static async Task RunAsync()
    {
        var probe = new BlockingCallProbe();
        ApartmentState? apartment = null;
        var outcome = await probe.RunAsync(() => apartment = Thread.CurrentThread.GetApartmentState(), TimeSpan.FromSeconds(5));
        Check(outcome == BlockingCallProbe.Outcome.Completed && apartment == ApartmentState.STA && !probe.IsRunning,
            "probe: a quick call completes on an STA thread");

        outcome = await probe.RunAsync(() => throw new InvalidOperationException("見つかりません"), TimeSpan.FromSeconds(5));
        Check(outcome == BlockingCallProbe.Outcome.Completed, "probe: a failing call counts as returned (the caller reports the failure itself)");

        // 返ってこない(VISA の Open が止まった形): 見切って TimedOut。返るまでは次の試しを重ねず StillRunning。
        using var release = new ManualResetEventSlim();
        int started = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        outcome = await probe.RunAsync(() => { Interlocked.Increment(ref started); release.Wait(); }, TimeSpan.FromMilliseconds(300));
        Check(outcome == BlockingCallProbe.Outcome.TimedOut && watch.Elapsed < TimeSpan.FromSeconds(3) && probe.IsRunning,
            "probe: a call that does not return is given up after the timeout");
        outcome = await probe.RunAsync(() => Interlocked.Increment(ref started), TimeSpan.FromSeconds(5));
        Check(outcome == BlockingCallProbe.Outcome.StillRunning && Volatile.Read(ref started) == 1,
            "probe: while the stuck call has not returned, a new try is not started");

        // 機器の電源を入れ直して返った形: そのあとはまた試せる。
        release.Set();
        for (int i = 0; i < 100 && probe.IsRunning; i++) await Task.Delay(20);
        outcome = await probe.RunAsync(() => Interlocked.Increment(ref started), TimeSpan.FromSeconds(5));
        Check(outcome == BlockingCallProbe.Outcome.Completed && Volatile.Read(ref started) == 2,
            "probe: after the stuck call returns, the next try runs");
    }
}
