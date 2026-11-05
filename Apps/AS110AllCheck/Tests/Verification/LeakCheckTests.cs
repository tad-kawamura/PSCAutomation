using PSCAutomation.Apps.AS110AllCheck;
using System.Diagnostics;

internal static class LeakCheckTests
{
    public static async Task RunAsync()
    {
        var trace=new List<string>();
        var timer=Stopwatch.StartNew();
        await LeakCheckSequence.RunAsync(
            ()=>{trace.Add("startLogger");return 0;},
            ()=>trace.Add("outputOff"),()=>{trace.Add("stopLogger");return Task.CompletedTask;},CancellationToken.None);
        Check(timer.Elapsed>=TimeSpan.FromSeconds(9.9) && trace.SequenceEqual(new[]{"startLogger","outputOff","stopLogger"}),
            "leak check starts LOGGER without changing output and stops both after ten seconds");
        foreach(string failure in new[]{"start","cancel","alreadyCanceled","stopOutput","stopLogger"})
        {
            trace.Clear();
            using var cancel=new CancellationTokenSource();
            if(failure=="alreadyCanceled")cancel.Cancel();
            try
            {
                await LeakCheckSequence.RunAsync(
                    ()=>{trace.Add("startLogger");return failure=="start"?-7:0;},
                    ()=>{trace.Add("outputOff");if(failure=="stopOutput")throw new IOException("output stop failed");},
                    ()=>{trace.Add("stopLogger");if(failure=="stopLogger")throw new IOException("LOGGER stop failed");return Task.CompletedTask;},
                    cancel.Token,(duration,token)=>{
                        Check(duration>TimeSpan.Zero && duration<=TimeSpan.FromSeconds(10),"leak delay is bounded by ten seconds from LOGGER start");
                        if(failure=="cancel")cancel.Cancel();
                        token.ThrowIfCancellationRequested();return Task.CompletedTask;
                    });
                throw new Exception("Failure was not propagated: "+failure);
            }
            catch(Exception ex) when(ex is IOException or OperationCanceledException) { }
            Check(trace.Contains("outputOff") && trace.Contains("stopLogger"),"leak check stops both on "+failure);
            if(failure=="alreadyCanceled")Check(!trace.Contains("startLogger"),"canceled request stops existing output without starting LOGGER");
        }
    }
    private static void Check(bool condition,string message)
    {
        if(!condition)throw new Exception(message);
        Console.WriteLine("PASS "+message);
    }
}
