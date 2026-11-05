using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace PSCAutomation.Apps.AS110AllCheck;

// LOGGER開始を基準に10秒。途中失敗・終了要求でも両機器の停止を試みる。
internal static class LeakCheckSequence
{
    public static async Task RunAsync(Func<long> startLogger,
        Action stopOutput, Func<Task> stopLogger, CancellationToken cancellationToken,
        Func<TimeSpan,CancellationToken,Task>? delay = null)
    {
        var errors=new List<Exception>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            long result=startLogger();
            var elapsed=Stopwatch.StartNew();
            if(result!=0)throw new IOException($"漏れチェックのLOGGER収集開始に失敗（戻り値{result}）。");
            TimeSpan remaining=TimeSpan.FromSeconds(10)-elapsed.Elapsed;
            if(remaining>TimeSpan.Zero)await (delay ?? Task.Delay)(remaining,cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch(Exception ex){errors.Add(ex);}
        finally
        {
            try { stopOutput(); } catch(Exception ex){errors.Add(ex);}
            try { await stopLogger(); } catch(Exception ex){errors.Add(ex);}
        }
        if(errors.Count==1)ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if(errors.Count>1)throw new AggregateException("漏れチェック中または停止処理で異常が発生しました。",errors);
    }
}
