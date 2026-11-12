using PSCAutomation.Apps.AS110AllCheck;
using PSCAutomation.Wrappers.WaveLogger.Automation;
using System.Diagnostics;

internal static class LeakCheckTests
{
    public static async Task RunAsync()
    {
        await SequenceAsync();
        Judge();
        Monitor();
        Input();
        Workflow();
        RecordFile();
    }

    // 試験の流れ: LOGGER開始 → 秒数いっぱい(または打ち切り)→ 出力OFF → LOGGER停止。途中の失敗でも両方止める。
    private static async Task SequenceAsync()
    {
        var trace=new List<string>();
        var timer=Stopwatch.StartNew();
        int polls=0;
        await LeakCheckSequence.RunAsync(
            ()=>{trace.Add("startLogger");return 0;},
            ()=>trace.Add("outputOff"),()=>{trace.Add("stopLogger");return Task.CompletedTask;},
            TimeSpan.FromSeconds(2),TimeSpan.FromMilliseconds(100),()=>{polls++;return false;},CancellationToken.None);
        Check(timer.Elapsed>=TimeSpan.FromSeconds(1.9) && trace.SequenceEqual(new[]{"startLogger","outputOff","stopLogger"}) && polls>=5,
            $"leak check runs the set seconds in real time, polling while running ({polls} polls), then stops output and LOGGER");

        // 模擬の時計: 待つたびに進める。秒数いっぱいで終わり、最後に 1 回読む。
        var now=TimeSpan.Zero;
        var waits=new List<TimeSpan>();
        Task FakeDelay(TimeSpan d,CancellationToken token){waits.Add(d);now+=d;token.ThrowIfCancellationRequested();return Task.CompletedTask;}
        trace.Clear();polls=0;
        await LeakCheckSequence.RunAsync(
            ()=>{trace.Add("startLogger");return 0;},()=>trace.Add("outputOff"),()=>{trace.Add("stopLogger");return Task.CompletedTask;},
            TimeSpan.FromSeconds(9),TimeSpan.FromMilliseconds(10),()=>{polls++;return false;},CancellationToken.None,FakeDelay,()=>now);
        Check(polls==900 && waits.All(w=>w==TimeSpan.FromMilliseconds(10)) && trace.SequenceEqual(new[]{"startLogger","outputOff","stopLogger"}),
            $"9-second test polls every 10 ms until the end ({polls} polls)");

        // 打ち切り: poll が true を返したら、その時点で出力OFF・LOGGER停止。
        now=TimeSpan.Zero;waits.Clear();trace.Clear();polls=0;
        await LeakCheckSequence.RunAsync(
            ()=>{trace.Add("startLogger");return 0;},()=>trace.Add("outputOff"),()=>{trace.Add("stopLogger");return Task.CompletedTask;},
            TimeSpan.FromSeconds(10),TimeSpan.FromMilliseconds(10),()=>++polls==30,CancellationToken.None,FakeDelay,()=>now);
        Check(polls==30 && now==TimeSpan.FromMilliseconds(300) && trace.SequenceEqual(new[]{"startLogger","outputOff","stopLogger"}),
            "decided failure stops output and LOGGER immediately (0.3 s)");

        // 自動判定なし(NR-X): 1 回待って終わる。
        now=TimeSpan.Zero;waits.Clear();polls=0;
        await LeakCheckSequence.RunAsync(()=>0,()=>{},()=>Task.CompletedTask,
            TimeSpan.FromSeconds(10),TimeSpan.FromSeconds(10),()=>{polls++;return false;},CancellationToken.None,FakeDelay,()=>now);
        Check(waits.SequenceEqual(new[]{TimeSpan.FromSeconds(10)}) && polls==1,"without judgment the sequence waits the full seconds once");

        foreach(string failure in new[]{"start","cancel","alreadyCanceled","stopOutput","stopLogger","poll"})
        {
            trace.Clear();now=TimeSpan.Zero;
            using var cancel=new CancellationTokenSource();
            if(failure=="alreadyCanceled")cancel.Cancel();
            try
            {
                await LeakCheckSequence.RunAsync(
                    ()=>{trace.Add("startLogger");return failure=="start"?-7:0;},
                    ()=>{trace.Add("outputOff");if(failure=="stopOutput")throw new IOException("output stop failed");},
                    ()=>{trace.Add("stopLogger");if(failure=="stopLogger")throw new IOException("LOGGER stop failed");return Task.CompletedTask;},
                    TimeSpan.FromSeconds(10),TimeSpan.FromMilliseconds(10),
                    ()=>failure=="poll"?throw new IOException("poll failed"):false,
                    cancel.Token,(duration,token)=>{
                        if(duration<=TimeSpan.Zero || duration>TimeSpan.FromMilliseconds(10))throw new Exception("leak delay is not bounded by the poll interval: "+duration);
                        now+=duration;
                        if(failure=="cancel")cancel.Cancel();
                        token.ThrowIfCancellationRequested();return Task.CompletedTask;
                    },()=>now);
                throw new Exception("Failure was not propagated: "+failure);
            }
            catch(Exception ex) when(ex is IOException or OperationCanceledException) { }
            Check(trace.Contains("outputOff") && trace.Contains("stopLogger"),"leak check stops both on "+failure);
            if(failure=="alreadyCanceled")Check(!trace.Contains("startLogger"),"canceled request stops existing output without starting LOGGER");
        }
    }

    // 判定: 収集開始からの最大値 − その点の値 がしきい値を一度でも超えたら不合格。ちょうどは合格。
    private static void Judge()
    {
        // 添付画像の例(最大 540.314、現在 539.700、差 0.614)は合格。
        var j=new LeakJudge(1.0);
        foreach(double v in new[]{539.900,540.314,540.100,539.672,539.700})Check(!j.Add(v),"values within 1.0 kPa keep passing");
        Check(j.Finish()==LeakOutcome.Pass && Math.Abs(j.Difference-0.614)<1e-9 && Math.Abs(j.MaxDifference-0.642)<1e-9 && j.Points==5,
            "example from the LOGGER screen passes (difference 0.614 kPa, largest 0.642 kPa)");

        j=new LeakJudge(1.0);
        j.Add(540.0);
        Check(!j.Add(539.0),"difference exactly at the threshold passes");
        Check(j.Add(538.99) && j.Decided==LeakOutcome.Fail && j.Finish()==LeakOutcome.Fail && j.Problem!.Contains("1.010"),
            "difference over the threshold fails at that point");
        Check(j.Add(540.0) && j.Points==3,"after failure further values are ignored");

        // float に丸めた値(LOGGER の換算と同じ)でも、画面の 0.001 kPa で差がちょうどしきい値なら合格。
        j=new LeakJudge(1.0);
        j.Add(540.314f);
        Console.WriteLine($"INFO float difference 540.314f - 539.314f = {(double)540.314f-(double)539.314f:R}");
        Check(!j.Add(539.314f) && j.Finish()==LeakOutcome.Pass && j.Latest==539.314 && j.Maximum==540.314,"float-rounded difference at the threshold passes (compared at 0.001 kPa like the LOGGER screen)");
        j=new LeakJudge(1.0);
        j.Add(540.314f);
        Check(j.Add(539.313f) && j.Decided==LeakOutcome.Fail,"0.001 kPa over the threshold fails");

        // 一時的に下がって戻っても、一度超えたら不合格(リアルタイムの比較)。
        j=new LeakJudge(1.0);
        j.Add(540.0);j.Add(538.5);
        Check(j.Finish()==LeakOutcome.Fail,"momentary drop over the threshold fails even if it recovers");

        // 最大値が後から上がった分も比べる。
        j=new LeakJudge(0.5);
        j.Add(539.0);j.Add(540.0);
        Check(j.Add(539.4) && j.Decided==LeakOutcome.Fail,"rising maximum is used for later comparisons");

        // 1 点も読めなければ判定できない。
        Check(new LeakJudge(1.0).Finish()==LeakOutcome.Unmeasurable,"no values means unmeasurable");
        Check(new LeakJudge(1.0,530.0).Finish()==LeakOutcome.Unmeasurable,"no values means unmeasurable even with a lower limit");

        // 最大値の下限(AS110-035 は 530 kPa、2026-11-12 続き): 試験の始めから点ごとに比べ、届いていなければその時点で不合格。ちょうどは合格。
        j=new LeakJudge(1.0,530.0);
        Check(j.Add(529.9) && j.Decided==LeakOutcome.Fail && j.Points==1 && j.Problem!.Contains("529.900") && j.Problem.Contains("530.0") && j.Problem.Contains("1 点目"),
            "maximum below the lower limit fails at once (real-time)");
        Check(j.Add(540.0) && j.Finish()==LeakOutcome.Fail,"after a lower limit failure further values are ignored");
        j=new LeakJudge(1.0,530.0);
        Check(!j.Add(530.0) && !j.Add(529.6) && !j.Add(529.2) && j.Finish()==LeakOutcome.Pass,"maximum exactly at the lower limit passes (later values only need the difference)");
        j=new LeakJudge(1.0,530.0);
        Check(!j.Add(530.0f) && !j.Add(529.9995f) && j.Finish()==LeakOutcome.Pass && j.Maximum==530.0,"float-rounded maximum at the lower limit passes");
        j=new LeakJudge(1.0,530.0);
        Check(j.Add(529.999f) && j.Decided==LeakOutcome.Fail,"0.001 kPa below the lower limit fails");
        j=new LeakJudge(1.0,530.0);
        j.Add(531.0);
        Check(j.Add(529.5) && j.Problem!.Contains("しきい値"),"after reaching the lower limit, a drop is judged by the difference");
        j=new LeakJudge(1.0,530.0);
        foreach(double v in new[]{539.900,540.314,540.100,539.672,539.700})j.Add(v);
        Check(j.Finish()==LeakOutcome.Pass,"example from the LOGGER screen passes with the AS110-035 lower limit");

        // 換算前の値の換算と無効値。
        var channel=new LeakPressureChannel(2,100,-1);
        Check(channel.ApiChannel==1 && channel.Scale(5.4f)==(double)(float)(100*5.4f-1),"pressure channel scales like XdtChannel.ReadScaledValues");
        Check(channel.Scale(3.4e38f) is null && channel.Scale(float.NaN) is null && channel.Scale(float.PositiveInfinity) is null,"invalid raw values are not scaled");

        // 雛形の圧力 CH2 の係数と移動平均を読める(作業用 xdt と同じ形式)。
        string template=Path.Combine(AppContext.BaseDirectory,"Fixtures","AS110データ調整・データ取り用.xdt");
        var fromXdt=LeakPressureChannel.FromXdt(template,2);
        Console.WriteLine($"INFO template CH2 gain={fromXdt.Gain} offset={fromXdt.Offset} average={fromXdt.AverageWindow}");
        Check(fromXdt.Number==2 && fromXdt.ApiChannel==1 && fromXdt.Gain!=0 && fromXdt.AverageWindow==7,"pressure channel coefficients and the 7-point moving average are read from the template xdt");
    }

    // 読み出し: 前回の続きを取り出して判定する。値が取れないときは判定できない。
    private static void Monitor()
    {
        var channel=new LeakPressureChannel(2,1,0);
        var data=new List<float>();
        var requests=new List<(int Unit,int Channel,long Start)>();
        LoggerRawDataBlock Read(int unit,IReadOnlyList<int> channels,long start)
        {
            requests.Add((unit,channels.Single(),start));
            return new LoggerRawDataBlock(data.Count,start,[data.Skip((int)Math.Min(start,data.Count)).ToArray()]);
        }
        var m=new LeakMonitor(channel,1.0,Read);
        Check(!m.Poll(TimeSpan.FromMilliseconds(10)) && m.Judge.Points==0,"no points yet keeps waiting");
        data.AddRange([540f,540.2f,539.9f]);
        Check(!m.Poll(TimeSpan.FromMilliseconds(100)) && m.Judge.Points==3 && requests[^1]==(1,1,0),"reads from the first point on unit 1, API channel 1");
        data.AddRange([539.5f]);
        Check(!m.Poll(TimeSpan.FromMilliseconds(200)) && m.Judge.Points==4 && requests[^1].Start==3,"next read continues after the last point");
        Check(!m.Poll(TimeSpan.FromMilliseconds(2100)),"no new points for under 2 s keeps waiting");
        Check(m.Poll(TimeSpan.FromMilliseconds(2201)) && m.Judge.Decided==LeakOutcome.Unmeasurable && m.Judge.Problem!.Contains("2 秒"),
            "no new points for 2 s is unmeasurable");

        data.Clear();
        m=new LeakMonitor(channel,1.0,Read);
        data.AddRange([540f,540f]);m.Poll(TimeSpan.FromMilliseconds(50));
        data.AddRange([539.1f,538.9f,540f]);
        Check(m.Poll(TimeSpan.FromMilliseconds(60)) && m.Judge.Decided==LeakOutcome.Fail && m.Judge.Points==4,"failure inside a block stops at that point");

        // 最大値の下限は読み出しの途中で比べ、届いていなければその場で打ち切る(移動平均 7 点なら 4 点読んだところで 1 点目を判定)。
        data.Clear();
        m=new LeakMonitor(new LeakPressureChannel(2,1,0,7),1.0,Read,530.0);
        data.AddRange([529.6f,529.7f,529.8f]);
        Check(!m.Poll(TimeSpan.FromMilliseconds(30)) && m.Judge.Points==0,"lower limit waits for the moving-average window like other checks");
        data.AddRange([529.9f]);
        Check(m.Poll(TimeSpan.FromMilliseconds(40)) && m.Judge.Decided==LeakOutcome.Fail && m.Judge.Points==1 && m.Judge.Problem!.Contains("下限"),
            "start below the lower limit stops the test during reading");
        data.Clear();
        m=new LeakMonitor(new LeakPressureChannel(2,1,0,7),1.0,Read,530.0);
        data.AddRange([539.9f,540.0f,539.8f,539.9f,539.7f]);
        Check(!m.Poll(TimeSpan.FromMilliseconds(50)) && m.Judge.Decided is null && m.Finish()==LeakOutcome.Pass,"AS110-035 pressure above the lower limit keeps passing");

        m=new LeakMonitor(channel,1.0,(_,_,_)=>throw new InvalidOperationException("COM error"));
        Check(m.Poll(TimeSpan.Zero) && m.Judge.Decided==LeakOutcome.Unmeasurable && m.Judge.Problem!.Contains("COM error"),"read exception is unmeasurable");

        data.Clear();data.AddRange([540f,540f,540f]);
        m=new LeakMonitor(channel,1.0,Read);m.Poll(TimeSpan.Zero);
        data.RemoveAt(0);
        Check(m.Poll(TimeSpan.FromMilliseconds(10)) && m.Judge.Decided==LeakOutcome.Unmeasurable && m.Judge.Problem!.Contains("減りました"),"decreased count is unmeasurable");

        m=new LeakMonitor(channel,1.0,(_,_,start)=>new LoggerRawDataBlock(start+2,start,[new float[1]]));
        Check(m.Poll(TimeSpan.Zero) && m.Judge.Decided==LeakOutcome.Unmeasurable,"short block is unmeasurable");

        // 換算前の値は係数で換算し、無効値(振り切れ)は判定できない。
        data.Clear();data.AddRange([5.40f,5.405f]);
        m=new LeakMonitor(new LeakPressureChannel(2,100,-1),1.0,Read);
        Check(!m.Poll(TimeSpan.Zero) && m.Judge.Points==2 && m.Judge.Maximum==Math.Round((double)(float)(100*5.405f-1),3),"raw values are scaled and judged");
        data.AddRange([5.40f,3.4e38f,5.40f]);
        Check(m.Poll(TimeSpan.FromMilliseconds(10)) && m.Judge.Decided==LeakOutcome.Unmeasurable && m.Judge.Problem!.Contains("4 点目"),
            "invalid raw value (over range) makes the test unmeasurable");

        // 移動平均(7 点、中心の窓): 判定する値は XdtChannel.ReadProcessedValues と同じ。少しずつ読んでも、最後に残りを判定して同じになる。
        var noisy=new List<float>();
        var random=new Random(12);
        for(int i=0;i<1000;i++)noisy.Add((float)(540+0.3*Math.Sin(i/40.0)+(random.NextDouble()-0.5)*1.6));
        double[] expected=Processed(noisy,7);
        data.Clear();
        var averaged=new LeakMonitor(new LeakPressureChannel(2,1,0,7),10.0,Read);
        var raw=new LeakMonitor(new LeakPressureChannel(2,1,0,1),10.0,Read);
        for(int i=0;i<noisy.Count;i+=13)
        {
            data.AddRange(noisy.Skip(i).Take(13));
            averaged.Poll(TimeSpan.FromMilliseconds(i*10));raw.Poll(TimeSpan.FromMilliseconds(i*10));
            if(averaged.Judge.Points!=Math.Max(0,data.Count-3))throw new Exception($"moving average judged {averaged.Judge.Points} of {data.Count} points");
        }
        Check(true,"moving average waits only for the right half of the window (3 points) while reading in blocks");
        Check(averaged.Finish()==LeakOutcome.Pass && raw.Finish()==LeakOutcome.Pass && averaged.Judge.Points==1000,"remaining points are judged at the end");
        double Round(double v)=>Math.Round(v,3,MidpointRounding.AwayFromZero);
        double expectedMax=expected.Select(Round).Max(),expectedLast=Round(expected[^1]);
        double expectedDiff=0,running=double.MinValue;
        foreach(double v in expected.Select(Round)){running=Math.Max(running,v);expectedDiff=Math.Max(expectedDiff,running-v);}
        Check(averaged.Judge.Maximum==expectedMax && averaged.Judge.Latest==expectedLast && Math.Abs(averaged.Judge.MaxDifference-expectedDiff)<1e-9,
            $"moving-average judgment equals ReadProcessedValues (max {expectedMax}, last {expectedLast}, largest difference {expectedDiff:F3})");
        Console.WriteLine($"INFO noise ±0.8 kPa: largest difference raw {raw.Judge.MaxDifference:F3} kPa, 7-point average {averaged.Judge.MaxDifference:F3} kPa");
        Check(raw.Judge.MaxDifference>averaged.Judge.MaxDifference,"moving average reduces noise in the difference");
        // 移動平均の値で不合格になる。
        data.Clear();
        var failing=new LeakMonitor(new LeakPressureChannel(2,1,0,7),1.0,Read);
        data.AddRange(Enumerable.Repeat(540f,50));failing.Poll(TimeSpan.Zero);
        data.AddRange(Enumerable.Repeat(538.5f,10));
        Check(failing.Poll(TimeSpan.FromMilliseconds(10)) && failing.Judge.Decided==LeakOutcome.Fail,"averaged drop over the threshold fails");
    }

    // XdtChannel.ReadProcessedValues と同じ計算(検査用に写したもの)。
    private static double[] Processed(IReadOnlyList<float> source,int window)
    {
        var output=new double[source.Count];
        int left=0,right=0;double sum=0;
        for(int i=0;i<source.Count;i++)
        {
            int wantLeft=(int)Math.Max(0L,(long)i-window/2);
            int wantRight=(int)Math.Min(source.Count,(long)i-window/2+window);
            while(right<wantRight)sum+=(double)(float)source[right++];
            while(left<wantLeft)sum-=(double)(float)source[left++];
            output[i]=(float)(sum/(right-left));
        }
        return output;
    }

    private static void Input()
    {
        Check(LeakTestInput.Validate("10","1.0",out int s,out double t) is null && s==10 && t==1.0,"default inputs are valid");
        Check(LeakTestInput.Validate(" 9 ","0.5",out s,out t) is null && s==9 && t==0.5,"9 seconds and 0.5 kPa are valid");
        Check(LeakTestInput.Validate("1","10",out _,out t) is null && t==10.0 && LeakTestInput.Validate("60","0.1",out _,out _) is null,"range limits are valid");
        foreach(var (sec,th) in new[]{("0","1.0"),("61","1.0"),("9.5","1.0"),("","1.0"),("abc","1.0"),("-1","1.0"),("10","0"),("10","0.05"),("10","10.1"),("10","1.05"),("10","x"),("10","-1"),("10","")})
            Check(LeakTestInput.Validate(sec,th,out _,out _) is not null,$"invalid input rejected: seconds '{sec}', threshold '{th}'");
        Check(LeakTestInput.ValidateLowerLimit("530","AS110-035",out double limit) is null && limit==530.0
            && LeakTestInput.ValidateLowerLimit(" 529.9 ","AS110-035",out limit) is null && limit==529.9
            && LeakTestInput.ValidateLowerLimit("0.1","m",out _) is null && LeakTestInput.ValidateLowerLimit("1000.0","m",out _) is null,"lower limit inputs are valid");
        Check(LeakTestInput.ValidateLowerLimit("","AS110-135B",out _)!.Contains("AS110-135B の最大値の下限が入っていません"),"blank lower limit names the model");
        foreach(string text in new[]{"0","1000.1","530.05","x","-1"})
            Check(LeakTestInput.ValidateLowerLimit(text,"m",out _) is not null,$"invalid lower limit rejected: '{text}'");
        Check(LeakTestInput.ValidateFailDisplay("2.0",out double display) is null && display==2.0
            && LeakTestInput.ValidateFailDisplay("0.5",out _) is null && LeakTestInput.ValidateFailDisplay("10",out _) is null,"fail display inputs are valid");
        foreach(string text in new[]{"0.4","10.1","2.05","","x"})
            Check(LeakTestInput.ValidateFailDisplay(text,out _) is not null,$"invalid fail display rejected: '{text}'");
        // 設定の既定値: AS110-035 の下限 530 kPa、不合格の表示 2 秒。古い設定ファイル(項目なし)を読んでも同じ。
        var defaults=System.Text.Json.JsonSerializer.Deserialize<TransferSettings>("{\"LeakTestSeconds\":12}")!;
        Check(defaults.LeakTestSeconds==12 && defaults.LeakFailDisplaySeconds==2.0 && defaults.LeakMaximumLowerLimitKpa.Count==1
            && defaults.LeakMaximumLowerLimitKpa["AS110-035"]==530.0,"old settings files get the AS110-035 lower limit and 2 s display");
        var saved=new TransferSettings();saved.LeakMaximumLowerLimitKpa["AS110-135B"]=480.5;saved.LeakFailDisplaySeconds=3.5;
        var reloaded=System.Text.Json.JsonSerializer.Deserialize<TransferSettings>(System.Text.Json.JsonSerializer.Serialize(saved))!;
        Check(reloaded.LeakMaximumLowerLimitKpa["AS110-135B"]==480.5 && reloaded.LeakMaximumLowerLimitKpa["AS110-035"]==530.0 && reloaded.LeakFailDisplaySeconds==3.5,
            "lower limits per model and display seconds are remembered");
    }

    // 工程: 最後の漏れ試験が合格・判定なし(NR-X)なら合格にできる。不合格・判定できないは回避が要る。試験を始めたら前の結果は使わない。
    private static void Workflow()
    {
        var f=new ProductionWorkflow{Mode=TestMode.Production};
        f.Configure("100",ProductModel.All[0]);f.BeginMeasurement(DateTime.Now);f.CaptureSucceeded();f.TransferSucceeded();f.SaveSucceeded();
        f.LeakStarted();f.LeakFinished(LeakOutcome.Fail);
        Check(!f.CanComplete && f.LeakNeedsOverride && f.LeakChecks==1,"failed leak test blocks completion and needs override");
        f.LeakStarted();
        Check(!f.CanComplete && !f.LeakNeedsOverride && f.LastLeakOutcome is null,"starting a retest clears the previous result");
        f.LeakFinished(LeakOutcome.Pass);
        Check(f.CanComplete && !f.LeakNeedsOverride && f.LeakChecks==2,"passing retest allows completion");
        f.LeakStarted();
        Check(!f.CanComplete,"a pass is not kept while the next test is running or after it failed with an error");
        f.LeakFinished(LeakOutcome.Unmeasurable);
        Check(!f.CanComplete && f.LeakNeedsOverride,"unmeasurable result needs override");
        f.LeakStarted();f.LeakFinished(LeakOutcome.NotJudged);
        Check(f.CanComplete,"NR-X without judgment allows completion as before");
        f.BeginMeasurement(DateTime.Now);
        Check(f.LastLeakOutcome is null && !f.LeakNeedsOverride,"remeasure clears the leak result");
    }

    private static void RecordFile()
    {
        string root=Path.Combine(Path.GetTempPath(),"AS110AllCheck-leak-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var at=new DateTime(2026,11,12,10,0,0);
            var fail=new LeakTestResult(at,LeakOutcome.Fail,10,1.0,540.314,539.2,1.114,321,"差 1.114 kPa がしきい値 1.0 kPa を超えました",530.0);
            string path=LeakTestRecordFile.Append(root,"00100","AS110-035",fail);
            LeakTestRecordFile.Append(root,"00100","AS110-035",new LeakTestResult(at.AddMinutes(1),LeakOutcome.NotJudged,10,1.0,double.NaN,double.NaN,double.NaN,0,"NR-X は自動判定なし"));
            LeakTestRecordFile.AppendOverride(root,"00100","AS110-035",fail,at.AddMinutes(2));
            byte[] bytes=File.ReadAllBytes(path);
            string[] lines=File.ReadAllLines(path,System.Text.Encoding.UTF8);
            Check(path==Path.Combine(root,"漏れ試験記録.txt") && bytes[0]==0xEF && bytes[1]==0xBB && bytes[2]==0xBF,"record file is UTF-8 with BOM in the save folder");
            Check(lines.Length==4 && lines[0]==LeakTestRecordFile.Header && lines.All(l=>l.Split('\t').Length==12),"header and one tab-separated line per test (12 columns)");
            Check(lines[0].Split('\t')[10]=="備考" && lines[0].Split('\t')[11]=="最大値の下限(kPa)","lower limit column is added at the end (earlier columns keep their position)");
            Check(lines[1]=="2026-11-12 10:00:00\t00100\tAS110-035\t不合格\t10\t1.0\t540.314\t539.200\t1.114\t321\t差 1.114 kPa がしきい値 1.0 kPa を超えました\t530.0","failed test line has values");
            Check(lines[2].Split('\t')[3]=="判定なし（人が判断）" && lines[2].Split('\t')[6]=="-" && lines[2].Split('\t')[9]=="-" && lines[2].Split('\t')[11]=="-","NR-X line has no values");
            Check(lines[3].Split('\t')[11]=="530.0","override line keeps the lower limit of the previous test");
            Check(new LeakTestResult(at,LeakOutcome.Pass,10,1.0,540.314,539.7,0.642,1000,"",530.0).Summary.Contains("最大値 540.314 kPa ≧ 下限 530.0 kPa"),"pass summary shows the lower limit");
            Check(lines[3].Split('\t')[3]=="人の判断で合格" && lines[3].Split('\t')[10].StartsWith("直前の判定：不合格"),"override line records the previous judgment");
            Check(!File.Exists(Path.Combine(root,"完了記録.txt")),"completion journal is not touched");
        }
        finally { Directory.Delete(root,true); }
        bool failed=false;
        try { LeakTestRecordFile.Append(Path.Combine(Path.GetTempPath(),"AS110AllCheck-missing-"+Guid.NewGuid().ToString("N"),"x"),"1","m",new LeakTestResult(DateTime.Now,LeakOutcome.Pass,10,1,1,1,0,1,"")); }
        catch(DirectoryNotFoundException){failed=true;}
        Check(failed,"unwritable save folder throws (completion is blocked)");
    }

    private static void Check(bool condition,string message)
    {
        if(!condition)throw new Exception(message);
        Console.WriteLine("PASS "+message);
    }
}
