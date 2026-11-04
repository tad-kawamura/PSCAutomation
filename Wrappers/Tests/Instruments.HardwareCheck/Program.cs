using System.Diagnostics;
using PSCAutomation.Wrappers.LoggerFiles;
using PSCAutomation.Wrappers.WaveLogger.Automation;

namespace PSCAutomation.Wrappers.Instruments.HardwareCheck;

// WF1973 のバースト(Wf1973BurstSession)と連続(Wf1973ContinuousSession)を実機で動かす確認ツール。
// NR-500(WAVE LOGGER)で 40 秒記録しながら発振器を動かし、保存した xdt の指令電圧チャンネルから
// 出た時間・山の高さ・出し直し・出力 OFF を確かめる。
//   Instruments.HardwareCheck burst        旧 AS110AllCheck の動き検査(0.2Hz・10.2Vpp・0.5 周期・開始位相 0°)を 2 回、
//                                          データ取り(0.04Hz)を 1 回、リーク用 DC 5V を 4 秒、の順に出す
//   Instruments.HardwareCheck continuous   三角波 5.1Vpp・オフセット 2.55V(0〜5.1V)・0.1Hz を位相 15° で出し、
//                                          約 12.5 秒後に位相 165° で出し直す(旧 AS100201SwitchController の 2 つの位相)。最後に出力 OFF。
//                                          出し直した瞬間の位相を記録から測り、「165° からやり直した」「150° ずれただけ」「変わらない」のどれかを出す
//   Instruments.HardwareCheck analyze burst|continuous <xdt> [--dump 14.3 15]   保存済みの xdt を判定だけする(機器なし。時刻は記録から推定)。
//                                          --dump はその時刻 [s] の範囲の値を並べる
// オプション: --out <フォルダ>(既定 %TEMP%\Instruments_HardwareCheck)  --template <xdt>(既定 Apps/AS110NoiseTest/雛形/AS110ノイズ測定用.xdt)
//             --logger auto|x|legacy(既定 legacy)  --usb-id 0  --channel 4(指令電圧を記録しているチャンネル)
// 弁をつないだまま使う前提で、出す電圧は 0〜+5.1V(AS110 では 1V = 6mA なので 30.6mA まで)に収める。
// 連続は AS100-201 の本来の設定(10.2Vpp・オフセット 0 = ±5.1V)だと負の電圧が出るので、振幅を半分にしてオフセットを足している。
// Wf1973ContinuousSession は波形の形を送らない(本体の設定のまま)ので、先に Wf1973BurstSession で三角波にしてから使う。
internal static class Program
{
    private const double Threshold = 0.25;        // これを超えたら「出ている」[V]
    private const double BaselineLimit = 0.1;     // 出していないときの許容 [V]
    private const double PeakTolerance = 0.15;    // 山の高さの許容 [V]
    private const double TimeTolerance = 0.2;     // 時間の許容 [s](20ms 記録 10 点分)

    // 旧 AS110AllCheck As110MotionTest.Settings / DataSettings、リーク(MainForm.Production の SetDcOutput(5))と同じ値
    private static readonly GeneratorBurstSettings Motion = new(GeneratorWaveform.Triangle, 0.2, 10.2, 0, 0.5, 0);
    private static readonly GeneratorBurstSettings Data = Motion with { FrequencyHz = 0.04 };
    private const double LeakVolts = 5;
    private const double LeakHoldSeconds = 4;

    private static readonly GeneratorContinuousSettings Continuous = new(5.1, 0.1, 2.55);
    // 旧 AS100201SwitchController の As100201WaveformSettings.PcPhaseDegrees / CrPhaseDegrees
    private const int StartPhase = 15;
    private const int RestartPhase = 165;

    private sealed class Options
    {
        public string Mode = "";
        public string? AnalyzeKind, AnalyzePath, Out, Template;
        public WaveLoggerEdition Edition = WaveLoggerEdition.Legacy;
        public int UsbId, Channel = 4;
        public double? DumpFrom, DumpTo;   // analyze で、この時刻 [s] の範囲の値を並べる
    }

    private sealed record Event(string Name, double Seconds);

    [STAThread]
    private static int Main(string[] args)
    {
        Options opts;
        try { opts = Parse(args); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("エラー: " + ex.Message);
            Usage();
            return 2;
        }

        if (opts.Mode == "analyze")
        {
            try
            {
                if (opts.DumpFrom is double from && opts.DumpTo is double to)
                {
                    var file = XdtFile.Load(opts.AnalyzePath!);
                    double[] values = file.GetChannel(opts.Channel).ReadScaledValues();
                    double step = (file.SampleInterval ?? TimeSpan.FromMilliseconds(20)).TotalSeconds;
                    for (int i = 0; i < values.Length; i++)
                        if (i * step >= from && i * step <= to) Console.WriteLine($"  {i * step,7:F2} 秒  {values[i],7:F3}");
                }
                return Analyze(opts.AnalyzeKind!, opts.AnalyzePath!, opts.Channel, null) ? 0 : 1;
            }
            catch (Exception ex) { Console.Error.WriteLine("エラー: " + ex.Message); return 1; }
        }

        // WAVE LOGGER の wrapper は「メッセージループのある STA スレッド」で使う(ShutdownAsync が await をはさむ)。
        int code = 1;
        var context = new ApplicationContext();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        SynchronizationContext.Current!.Post(async _ =>
        {
            try { code = await RunAsync(opts); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("エラー: " + ex.Message);
                code = 1;
            }
            finally { context.ExitThread(); }
        }, null);
        Application.Run(context);
        return code;
    }

    private static async Task<int> RunAsync(Options opts)
    {
        string template = Path.GetFullPath(opts.Template ?? Path.Combine(FindRepositoryRoot(), "Apps", "AS110NoiseTest", "雛形", "AS110ノイズ測定用.xdt"));
        if (!File.Exists(template)) throw new FileNotFoundException("雛形 xdt がありません: " + template);
        string outFolder = Path.GetFullPath(opts.Out ?? Path.Combine(Path.GetTempPath(), "Instruments_HardwareCheck"));
        Directory.CreateDirectory(outFolder);
        string xdt = Path.Combine(outFolder, $"WF1973_{opts.Mode}_{DateTime.Now:yyyyMMdd_HHmmss}.xdt");
        Console.WriteLine($"モード: {opts.Mode} / 雛形 {template} / 保存 {xdt} / 指令電圧 CH{opts.Channel}");

        var logger = new WaveLoggerSession();
        bool loggerReady = false;
        var events = new List<Event>();
        try
        {
            logger.Initialize(opts.Edition, Console.WriteLine);
            loggerReady = true;
            var hw = logger.DetectNr500Usb(opts.UsbId);
            Console.WriteLine("LOGGER 本体確認: " + hw.Description);
            if (!hw.IsDetected) throw new InvalidOperationException($"NR-500 を確認できません(USB 機器 ID {opts.UsbId})。");
            Expect("OpenFile", logger.OpenFile(template));

            if (opts.Mode == "burst") RunBurst(logger, events);
            else await RunContinuousAsync(logger, events);

            Expect("SaveXdt", logger.SaveXdt(xdt));
            Expect("CloseActiveFile", logger.CloseActiveFile());
        }
        finally
        {
            // 途中で止まっても発振器の出力は切る(それぞれの手順の中でも切っている)。
            try { using var off = FunctionGeneratorConnection.Connect(); } catch (Exception ex) { Console.WriteLine("発振器の出力 OFF を確認できません: " + ex.Message); }
            if (loggerReady)
            {
                try { logger.Stop(); } catch { }
                await logger.ShutdownAsync(Console.WriteLine);
            }
        }

        Console.WriteLine();
        Console.WriteLine("操作の時刻(LOGGER が測定中になってから):");
        foreach (var e in events) Console.WriteLine($"  {e.Seconds,6:F2} 秒  {e.Name}");
        return Analyze(opts.Mode, xdt, opts.Channel, events) ? 0 : 1;
    }

    // 記録を始め、測定中になった時点からの経過時間を返す時計を作る。
    private static Stopwatch StartRecording(WaveLoggerSession logger)
    {
        if (logger.GetState() != 1) throw new InvalidOperationException($"LOGGER が停止状態ではありません(GetState={logger.GetState()})。");
        Expect("StartSingleAcquisition", logger.StartSingleAcquisition());
        var wait = Stopwatch.StartNew();
        while (logger.GetState() == 1)
        {
            if (wait.Elapsed.TotalSeconds > 10) throw new TimeoutException("10 秒たっても LOGGER の記録が始まりません。");
            Thread.Sleep(50);
        }
        Console.WriteLine($"記録開始(測定中まで {wait.Elapsed.TotalSeconds:F2} 秒)");
        return Stopwatch.StartNew();
    }

    private static void WaitUntil(Stopwatch clock, double seconds)
    {
        double rest = seconds - clock.Elapsed.TotalSeconds;
        if (rest > 0) Thread.Sleep(TimeSpan.FromSeconds(rest));
    }

    private static void WaitRecordingEnd(WaveLoggerSession logger, Stopwatch clock)
    {
        while (true)
        {
            long state = logger.GetState();
            if (state == 1) break;
            if (state == 11) throw new InvalidOperationException("LOGGER がエラー停止しました(GetState=11)。");
            if (clock.Elapsed.TotalSeconds > 90) throw new TimeoutException("90 秒たっても記録が終わりません。");
            Thread.Sleep(200);
        }
        Console.WriteLine($"記録終了({clock.Elapsed.TotalSeconds:F1} 秒)");
    }

    private static void RunBurst(WaveLoggerSession logger, List<Event> events)
    {
        using var generator = FunctionGeneratorConnection.Connect();
        Console.WriteLine("発振器: " + generator.Identity);
        generator.PrepareBurst(Motion);
        Console.WriteLine("準備: " + generator.PreparationSummary);

        var clock = StartRecording(logger);
        void Mark(string name)
        {
            events.Add(new Event(name, clock.Elapsed.TotalSeconds));
            Console.WriteLine($"  {clock.Elapsed.TotalSeconds,6:F2} 秒  {name}");
        }

        WaitUntil(clock, 2);
        generator.TriggerBurst();
        Mark("動き検査 1 回目(TriggerBurst。初回なので出力 ON + *TRG)");
        WaitUntil(clock, 7);
        generator.TriggerBurst();
        Mark("動き検査 2 回目(TriggerBurst。*TRG だけ)");
        WaitUntil(clock, 10.5);
        generator.PrepareBurst(Data);
        Console.WriteLine("  準備: " + generator.PreparationSummary);
        WaitUntil(clock, 11);
        generator.TriggerBurst();
        Mark("データ取り(0.04Hz)");
        WaitUntil(clock, 26);
        generator.SetDcOutput(LeakVolts);
        Mark($"リーク DC {LeakVolts}V(SetDcOutput)");
        WaitUntil(clock, 26 + LeakHoldSeconds);
        generator.StopOutput();
        Mark("出力停止(StopOutput)");
        WaitRecordingEnd(logger, clock);
    }

    private static async Task RunContinuousAsync(WaveLoggerSession logger, List<Event> events)
    {
        // 連続のクラスは波形の形を送らないので、バーストのクラスで三角波(対称 50%)にしておく。
        using (var shape = FunctionGeneratorConnection.Connect())
        {
            shape.PrepareBurst(new GeneratorBurstSettings(GeneratorWaveform.Triangle, Continuous.FrequencyHz, Continuous.AmplitudeVpp, Continuous.OffsetVolts, 1, 0));
            Console.WriteLine("三角波にしておく: " + shape.PreparationSummary);
        }

        var generator = Wf1973ContinuousSession.Connect();
        Console.WriteLine("発振器(連続): " + generator.Identity);
        var clock = StartRecording(logger);
        void Mark(string name)
        {
            events.Add(new Event(name, clock.Elapsed.TotalSeconds));
            Console.WriteLine($"  {clock.Elapsed.TotalSeconds,6:F2} 秒  {name}");
        }
        try
        {
            WaitUntil(clock, 2);
            bool done = await generator.StartWithPhaseAsync(Continuous, StartPhase, () => false);
            Mark($"連続開始(位相 {StartPhase}°、完了={done})");
            WaitUntil(clock, 14.5);
            done = await generator.StartWithPhaseAsync(Continuous, RestartPhase, () => false);
            Mark($"出し直し(位相 {RestartPhase}°、完了={done})");
            WaitUntil(clock, 29.5);
        }
        finally
        {
            // 連続のクラスは Dispose で出力 OFF を送らない(元のとおり)。接続し直して切る。
            generator.Dispose();
            using var off = FunctionGeneratorConnection.Connect();
        }
        Mark("出力 OFF(接続し直して :OUTPut:STATe OFF)");
        WaitRecordingEnd(logger, clock);
    }

    // ---- 判定 ----

    private sealed record Run(int Start, int End)   // End は含まない
    {
        public int Length => End - Start;
    }

    private static bool Analyze(string kind, string path, int channelNumber, IReadOnlyList<Event>? events)
    {
        var file = XdtFile.Load(path);
        var channel = file.GetChannel(channelNumber);
        double[] v = channel.ReadScaledValues();
        double dt = (file.SampleInterval ?? TimeSpan.FromMilliseconds(20)).TotalSeconds;
        Console.WriteLine();
        Console.WriteLine($"xdt: {Path.GetFileName(path)} / {v.Length} 点 / {dt * 1000:0.#}ms / CH{channelNumber} {channel.Info.Name} [{channel.Info.Unit}] 最小 {v.Min():F3} 最大 {v.Max():F3}");
        var checks = new List<(bool Ok, string Text)>();
        if (kind == "burst") AnalyzeBurst(v, dt, events, checks);
        else AnalyzeContinuous(v, dt, events, checks);
        foreach (var (ok, text) in checks) Console.WriteLine($"{(ok ? "OK" : "NG")}: {text}");
        bool all = checks.All(c => c.Ok);
        Console.WriteLine(all ? "すべて OK" : "NG あり");
        return all;
    }

    private static List<Run> RunsAbove(double[] v, double threshold)
    {
        var runs = new List<Run>();
        int start = -1;
        for (int i = 0; i <= v.Length; i++)
        {
            bool above = i < v.Length && v[i] > threshold;
            if (above && start < 0) start = i;
            else if (!above && start >= 0) { runs.Add(new Run(start, i)); start = -1; }
        }
        return runs;
    }

    private static double Max(double[] v, Run r) => v.Skip(r.Start).Take(r.Length).Max();

    // 出していない区間の最大の振れと、その時刻 [s]。
    // 山の前後は除く(margins[k] 点 + 2 点。三角の上り始め・下り終わりは Threshold より下でも出ている)。
    private static (double Value, double Seconds) BaselineDeviation(double[] v, IReadOnlyList<Run> runs, double dt, IReadOnlyList<int>? margins = null)
    {
        double worst = 0;
        int at = 0;
        for (int i = 0; i < v.Length; i++)
        {
            if (runs.Select((r, k) => (r, m: (margins is not null && k < margins.Count ? margins[k] : 0) + 2))
                    .Any(x => i >= x.r.Start - x.m && i < x.r.End + x.m)) continue;
            if (Math.Abs(v[i]) > worst) { worst = Math.Abs(v[i]); at = i; }
        }
        return (worst, at * dt);
    }

    private static void AnalyzeBurst(double[] v, double dt, IReadOnlyList<Event>? events, List<(bool, string)> checks)
    {
        var runs = RunsAbove(v, Threshold);
        double motionPeak = Motion.AmplitudeVpp / 2 + Motion.OffsetVolts;
        double motionSec = Motion.BurstCycles / Motion.FrequencyHz;
        double dataSec = Data.BurstCycles / Data.FrequencyHz;
        // 0 → 山 → 0 の三角が Threshold を超えている時間
        double TriangleWidth(double seconds, double peak) => seconds * (1 - Threshold / peak);
        double leakSec = events is { Count: >= 5 } ? events[4].Seconds - events[3].Seconds : LeakHoldSeconds;
        var expected = new (string Name, double Width, double Peak)[]
        {
            ("動き検査 1 回目", TriangleWidth(motionSec, motionPeak), motionPeak),
            ("動き検査 2 回目", TriangleWidth(motionSec, motionPeak), motionPeak),
            ("データ取り", TriangleWidth(dataSec, motionPeak), motionPeak),
            ("リーク DC", leakSec, LeakVolts),
        };

        checks.Add((runs.Count == expected.Length, $"出ていた区間の数 {runs.Count}(期待 {expected.Length})"));
        for (int k = 0; k < Math.Min(runs.Count, expected.Length); k++)
        {
            var r = runs[k];
            var (name, width, peak) = expected[k];
            double w = r.Length * dt, p = Max(v, r);
            // 保存済み xdt だけの判定では操作の時刻が分からないので、DC の長さは操作の遅れ(実測 0.1〜0.2 秒)の分だけ緩める。
            double tolerance = name == "リーク DC" && events is null ? 0.35 : TimeTolerance;
            checks.Add((Math.Abs(w - width) <= tolerance,$"{name}: {r.Start * dt:F2}〜{r.End * dt:F2} 秒、{Threshold}V を超えた時間 {w:F2} 秒(期待 {width:F2})"));
            checks.Add((Math.Abs(p - peak) <= PeakTolerance, $"{name}: 最大 {p:F3}V(期待 {peak:F2})"));
            if (name == "リーク DC")
            {
                // 平らに保てているか(立ち上がり・立ち下がりの 2 点を除く)
                var flat = v.Skip(r.Start + 2).Take(Math.Max(1, r.Length - 4)).ToArray();
                checks.Add((flat.Max() - flat.Min() <= 0.1, $"{name}: 平らな部分の振れ {flat.Max() - flat.Min():F3}V(0.1V 以内)"));
            }
        }
        if (events is { Count: >= 4 } && runs.Count == expected.Length)
        {
            // 記録の時刻と操作の時刻のずれ(最初の山で合わせ、残りが同じずれで並ぶか)。
            // 三角は 0V から上り始めて Threshold を超えるまでに時間がかかる(遅い三角ほど長い)ので、その分を引いて出始めの時刻にする。
            double[] rise = { motionSec / 2, motionSec / 2, dataSec / 2, 0 };
            double Onset(int k) => runs[k].Start * dt - (rise[k] == 0 ? 0 : rise[k] * Threshold / expected[k].Peak);
            double lag = Onset(0) - events[0].Seconds;
            for (int k = 1; k < 4; k++)
            {
                double diff = Onset(k) - (events[k].Seconds + lag);
                checks.Add((Math.Abs(diff) <= TimeTolerance, $"{expected[k].Name}: 操作から出始めるまでのずれ {diff:+0.00;-0.00} 秒(1 回目基準)"));
            }
        }
        // 三角が Threshold に届くまでの点数(データ取りは 0.31 秒 = 16 点)
        var margins = expected.Select(e => e.Name == "リーク DC" ? 0
            : (int)Math.Ceiling((e.Name == "データ取り" ? dataSec : motionSec) / 2 * Threshold / e.Peak / dt)).ToList();
        var (worst, worstAt) = BaselineDeviation(v, runs, dt, margins);
        checks.Add((worst <= BaselineLimit, $"出していないときの振れ 最大 {worst:F3}V({worstAt:F2} 秒。{BaselineLimit}V 以内。2 回目の前・データ取りの準備中・出力停止後を含む)"));
    }

    private static void AnalyzeContinuous(double[] v, double dt, IReadOnlyList<Event>? events, List<(bool, string)> checks)
    {
        var runs = RunsAbove(v, Threshold);
        // 谷(0V 付近)で一瞬 Threshold を下回ることがあるので、最初に上がってから最後に下がるまでを「出ていた」とする。
        if (runs.Count == 0) { checks.Add((false, "出力が記録されていません")); return; }
        int start = runs[0].Start, end = runs[^1].End;
        double lag = events is { Count: >= 1 } ? start * dt - events[0].Seconds : 0;
        double restartSec = events is { Count: >= 2 } ? events[1].Seconds + lag : start * dt + 12.5;
        double offSec = events is { Count: >= 3 } ? events[2].Seconds + lag : end * dt;
        int restart = (int)Math.Round(restartSec / dt);
        // 発振器が出し直すのは操作から 0.1 秒ほど遅れる。操作の時刻の前後 0.5 秒でいちばん大きい段差を出し直しの瞬間とする。
        // 実機(2026-11-04)では段差が 2 回出る: 位相の設定(:PHASe)で設定の差だけずれ、約 80ms 後に :PHASe:INITiate で設定の位相からやり直す。
        int window = (int)Math.Round(0.5 / dt), jumpAt = -1;
        double biggest = 0.3;   // 三角の 1 点ぶんの変化(0.1Hz・5.1Vpp で約 0.02V)より十分大きい段差だけ
        for (int i = Math.Max(1, restart - window); i < Math.Min(v.Length, restart + window); i++)
        {
            double step = Math.Abs(v[i] - v[i - 1]);
            if (step <= 0.3) continue;
            Console.WriteLine($"出し直しの段差: {(i - 1) * dt:F2} 秒 {v[i - 1]:F3}V → {i * dt:F2} 秒 {v[i]:F3}V(操作から {(i * dt - restartSec):+0.00;-0.00} 秒)");
            if (step > biggest) { biggest = step; jumpAt = i; }
        }
        if (jumpAt > 0)
        {
            restart = jumpAt;
            restartSec = jumpAt * dt;
        }
        int margin = (int)Math.Round(0.3 / dt);
        double low = Continuous.OffsetVolts - Continuous.AmplitudeVpp / 2, high = Continuous.OffsetVolts + Continuous.AmplitudeVpp / 2;
        double halfPeriod = 0.5 / Continuous.FrequencyHz;

        checks.Add((Math.Abs(end * dt - offSec) <= 0.5, $"出ていた区間 {start * dt:F2}〜{end * dt:F2} 秒(出力 OFF の操作 {offSec:F2} 秒)"));
        foreach (var (name, from, to) in new[] { ("出し直し前", start + margin, restart - margin), ("出し直し後", restart + margin, end - margin) })
        {
            if (to <= from) { checks.Add((false, $"{name}: 区間が短すぎます")); continue; }
            var seg = v[from..to];
            checks.Add((Math.Abs(seg.Min() - low) <= PeakTolerance && Math.Abs(seg.Max() - high) <= PeakTolerance,
                $"{name}: 最小 {seg.Min():F3}V / 最大 {seg.Max():F3}V(期待 {low:F2} / {high:F2})"));
            var crossings = new List<double>();
            for (int i = from + 1; i < to; i++)
                if ((v[i - 1] - Continuous.OffsetVolts) * (v[i] - Continuous.OffsetVolts) < 0) crossings.Add(i * dt);
            var gaps = crossings.Zip(crossings.Skip(1), (a, b) => b - a).ToList();
            bool ok = gaps.Count > 0 && gaps.All(g => Math.Abs(g - halfPeriod) <= TimeTolerance);
            checks.Add((ok, $"{name}: 中心 {Continuous.OffsetVolts}V を横切る間隔 {string.Join(" / ", gaps.Select(g => g.ToString("F2")))} 秒(期待 {halfPeriod:F2})"));
        }

        // 位相は中心を横切る時刻から測る(上りで横切る = 0°、下りで横切る = 180°)。
        // 出力 ON・出し直しの瞬間の段差でも中心を横切ることがあるので、その前後 0.2 秒の横切りは使わない。
        double startSec = start * dt;
        var cross = new List<(double Seconds, bool Rising)>();
        for (int i = start + 1; i < end; i++)
        {
            double a = v[i - 1] - Continuous.OffsetVolts, b = v[i] - Continuous.OffsetVolts;
            if (a * b >= 0) continue;
            double t = (i - 1 + a / (a - b)) * dt;
            if (Math.Abs(t - startSec) < 0.2 || Math.Abs(t - restartSec) < 0.2) continue;
            cross.Add((t, b > a));
        }
        double PhaseAt(double t, (double Seconds, bool Rising) c) => Norm((c.Rising ? 0 : 180) + 360 * Continuous.FrequencyHz * (t - c.Seconds));
        var firstAfterStart = cross.FirstOrDefault(c => c.Seconds > startSec);
        var lastBeforeRestart = cross.LastOrDefault(c => c.Seconds < restartSec);
        var firstAfterRestart = cross.FirstOrDefault(c => c.Seconds > restartSec);
        if (firstAfterStart == default || lastBeforeRestart == default || firstAfterRestart == default)
        {
            checks.Add((false, "位相を測れる横切りが足りません"));
        }
        else
        {
            double started = PhaseAt(startSec, firstAfterStart);
            checks.Add((Math.Abs(Diff(started, StartPhase)) <= 20, $"開始の位相 {started:F0}°(設定 {StartPhase}°)"));
            double natural = PhaseAt(restartSec, lastBeforeRestart);    // 出し直さなければこの位相だった
            double observed = PhaseAt(restartSec, firstAfterRestart);  // 実際に出し直した後の位相
            double jump = Diff(observed, natural);
            string kind =
                Math.Abs(Diff(observed, RestartPhase)) <= 20 ? $"{RestartPhase}° からやり直した" :
                Math.Abs(Diff(jump, RestartPhase - StartPhase)) <= 20 ? $"やり直さず、位相の設定の差 {RestartPhase - StartPhase}° だけずれた" :
                Math.Abs(jump) <= 20 ? "変わらない(出し直しが効いていない)" : "どれにも当てはまらない";
            // 新しい位相が波形に反映されていれば OK(やり直したか、ずれただけかは結果として残す)
            bool reflected = !kind.StartsWith("変わらない") && !kind.StartsWith("どれにも");
            checks.Add((reflected, $"出し直し({restartSec:F2} 秒): 出し直さなければ {natural:F0}° → 実際 {observed:F0}°(ずれ {jump:+0;-0}°)= {kind}"));
        }
        double after = v.Skip(end + 2).Select(Math.Abs).DefaultIfEmpty(0).Max();
        checks.Add((end + 2 < v.Length && after <= BaselineLimit, $"出力 OFF 後の振れ 最大 {after:F3}V({BaselineLimit}V 以内)"));
        double beforeStart = v.Take(Math.Max(0, start - 2)).Select(Math.Abs).DefaultIfEmpty(0).Max();
        checks.Add((beforeStart <= BaselineLimit, $"開始前の振れ 最大 {beforeStart:F3}V({BaselineLimit}V 以内)"));
    }

    // ---- 補助 ----

    private static double Norm(double degrees) => (degrees % 360 + 360) % 360;
    private static double Diff(double a, double b) => (Norm(a - b) + 180) % 360 - 180;   // a - b を -180〜180° に

    private static void Expect(string name, long result)
    {
        if (result != 0) throw new InvalidOperationException($"LOGGER の {name} が失敗しました(戻り値 {result})。");
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PSCAutomation.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("PSCAutomation.sln が見つかりません。--template で雛形 xdt を指定してください。");
    }

    private static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException(args[i] + " の値がありません。");
            switch (args[i])
            {
                case "burst" or "continuous" when o.Mode == "": o.Mode = args[i]; break;
                case "analyze" when o.Mode == "":
                    o.Mode = "analyze";
                    o.AnalyzeKind = Next();
                    if (o.AnalyzeKind is not ("burst" or "continuous")) throw new ArgumentException("analyze の次は burst か continuous です。");
                    o.AnalyzePath = Next();
                    break;
                case "--out": o.Out = Next(); break;
                case "--template": o.Template = Next(); break;
                case "--usb-id": o.UsbId = int.Parse(Next()); break;
                case "--channel": o.Channel = int.Parse(Next()); break;
                case "--dump": o.DumpFrom = double.Parse(Next()); o.DumpTo = double.Parse(Next()); break;
                case "--logger":
                    o.Edition = Next().ToLowerInvariant() switch
                    {
                        "auto" => WaveLoggerEdition.Auto,
                        "x" => WaveLoggerEdition.WaveLoggerX,
                        "legacy" => WaveLoggerEdition.Legacy,
                        var x => throw new ArgumentException("--logger は auto / x / legacy です: " + x),
                    };
                    break;
                default: throw new ArgumentException("不明な引数: " + args[i]);
            }
        }
        if (o.Mode == "") throw new ArgumentException("burst / continuous / analyze のどれかを指定してください。");
        return o;
    }

    private static void Usage() => Console.Error.WriteLine(
        "使い方: Instruments.HardwareCheck burst | continuous | analyze burst|continuous <xdt> [--out <フォルダ>] [--template <xdt>] [--logger auto|x|legacy] [--usb-id 0] [--channel 4]");
}
