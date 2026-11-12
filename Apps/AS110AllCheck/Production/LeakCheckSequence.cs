using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace PSCAutomation.Apps.AS110AllCheck;

// LOGGER開始を基準に試験秒数(基本10秒)。途中失敗・終了要求でも両機器の停止を試みる。
// 2026-11-12 から試験中に poll(値の読み出しと判定)を繰り返し、不合格・判定できないが決まったらその時点で打ち切る
// (specs/AS110AllCheck/SPEC.md「漏れ試験の自動判定」)。自動判定しないとき(NR-X)は poll が常に false を返す。
internal static class LeakCheckSequence
{
    /// <param name="poll">読み出しと判定。打ち切るとき true。例外は試験の失敗として扱う(両方の停止は試す)。</param>
    /// <param name="clock">時刻の取り方(検証用)。LOGGER開始時の値を基準にする。既定は実時間。</param>
    public static async Task RunAsync(Func<long> startLogger,
        Action stopOutput, Func<Task> stopLogger, TimeSpan duration, TimeSpan pollInterval, Func<bool> poll,
        CancellationToken cancellationToken,
        Func<TimeSpan,CancellationToken,Task>? delay = null, Func<TimeSpan>? clock = null)
    {
        if(duration<=TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(duration));
        if(pollInterval<=TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(pollInterval));
        var errors=new List<Exception>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            long result=startLogger();
            var watch=Stopwatch.StartNew();
            TimeSpan started=clock?.Invoke() ?? TimeSpan.Zero;
            TimeSpan Now()=>clock is null ? watch.Elapsed : clock()-started;
            if(result!=0)throw new IOException($"漏れチェックのLOGGER収集開始に失敗（戻り値{result}）。");
            while(true)
            {
                TimeSpan remaining=duration-Now();
                if(remaining>TimeSpan.Zero)await (delay ?? Task.Delay)(remaining<pollInterval?remaining:pollInterval,cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if(poll())break;
                if(Now()>=duration)break;
            }
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
