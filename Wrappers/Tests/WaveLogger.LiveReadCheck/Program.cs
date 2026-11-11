using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using PSCAutomation.Wrappers.LoggerFiles;
using PSCAutomation.Wrappers.WaveLogger.Automation;

namespace PSCAutomation.Wrappers.WaveLogger.LiveReadCheck;

// 測定中の LOGGER から、xdt を保存せずに値を取り出せるかを実機で確かめる確認ツール(2026-11-10)。
// 目的: 測定中も波形から助言を刻々と出す(AS100201SwitchController の将来の形)ための下調べ。
//
// やること:
//   1. 雛形 xdt を開いて 1 回収集を始める(StartSingleAcquisition。Instruments.HardwareCheck と同じ)
//   2. 測定中、一定間隔(既定 200ms)で LOGGER のメーカー API を直接呼ぶ
//        GetDataCount  … PC に読み出された点数
//        GetArrayData  … 増えた分の値(換算前)をまとめて取る(うまくいかなければ GetData で 1 点ずつ)
//        GetCurrentData … 本体の最新値
//      それぞれにかかった時間と、点数の増え方(PC に届くまでの遅れ)を記録する
//   3. LOGGER が止まったら xdt を保存し、測定中に取り出した値と 1 点ずつ突き合わせる
//      雛形の換算(スケーリング)で換算した値と、保存した xdt の換算値も比べる
//
// 2026-11-10 の作成時は部品に値の取り出しが無かったので、部品が持っている LOGGER の参照をリフレクションで借りてメーカー API を呼んだ。
// 2026-11-11 に部品へ ReadRawData / GetCurrentRawValue を足したので、既定(まとめて取る)はその関数を使う。
// GetData の 1 点ずつ(--per-point と、まとめて取れないときの切り替え)と、GetArrayData と GetData の照合は、部品に無いので今も参照を借りる。
//
//   WaveLogger.LiveReadCheck --template <xdt> [--interval 200] [--stop-after 秒] [--max-seconds 120]
//                            [--unit 1] [--per-point] [--out <フォルダ>] [--logger legacy|x|auto] [--usb-id 0]
//   WaveLogger.LiveReadCheck --template <xdt> --dry-run   雛形の中身(チャンネル・周期・換算)だけ表示する(機器なし)
internal static class Program
{
    // GetData / GetCurrentData は異常時に 3.4×10^38 を返す(説明書 7.4.2.3 / 7.4.2.4)
    private const float InvalidThreshold = WaveLoggerSession.InvalidRawValueThreshold;

    private sealed class Options
    {
        public string? Template, Out;
        public WaveLoggerEdition Edition = WaveLoggerEdition.Legacy;
        public int UsbId, Unit = 1, IntervalMs = 200;
        public double? StopAfter;
        public double MaxSeconds = 120;
        public bool DryRun, PerPoint;
    }

    private sealed class Channel(ChannelInfo info)
    {
        public ChannelInfo Info { get; } = info;
        public int ComChannel => Info.Number - 1;   // メーカー API のチャンネルは 0 始まり
        public List<float> Values { get; } = new();
        public float? Current;
        public double Scale(double raw) => Info.Gain * raw + Info.Offset;
        public string Label => $"CH{Info.Number}{(string.IsNullOrWhiteSpace(Info.Name) ? "" : " " + Info.Name)}";
    }

    private sealed class Timing(string name)
    {
        private readonly List<double> samples = new();
        public string Name { get; } = name;
        public void Add(double ms) => samples.Add(ms);
        public double Last => samples.Count == 0 ? 0 : samples[^1];
        public string Summary => samples.Count == 0 ? $"{Name}: 呼んでいない"
            : $"{Name}: {samples.Count} 回 平均 {samples.Average():F1}ms 最大 {samples.Max():F1}ms";
    }

    private sealed record Tick(double Seconds, long State, long Count, long Added, double LagSeconds,
        double TickMs, double CountMs, double ReadMs, double CurrentMs, float?[] Current);

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

        // WAVE LOGGER の部品は「メッセージループのある STA スレッド」で使う(Instruments.HardwareCheck と同じ作り)。
        // 測定中の読み取りも同じスレッドから await Task.Delay をはさんで呼ぶ(アプリでタイマーから呼ぶのと同じ条件)。
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
        string template = Path.GetFullPath(opts.Template!);
        if (!File.Exists(template)) throw new FileNotFoundException("雛形 xdt がありません: " + template);
        var templateFile = XdtFile.Load(template);
        TimeSpan interval = templateFile.SampleInterval
            ?? throw new InvalidDataException("雛形からサンプリング周期を読めません。");
        var channels = templateFile.Channels.Select(c => new Channel(c.Info)).ToList();
        int[] apiChannels = channels.Select(c => c.ComChannel).ToArray();

        Console.WriteLine($"雛形: {template}");
        Console.WriteLine($"  形式 {templateFile.Info.Format} / 周期 {interval.TotalMilliseconds:0.###}ms / 雛形の点数 {templateFile.SampleCount}");
        foreach (var c in channels)
        {
            string scale = c.Info.ScalingEnabled ? $"換算 {c.Info.Gain:G6} × 値 + {c.Info.Offset:G6}" : "換算なし";
            Console.WriteLine($"  {c.Label} [{c.Info.Unit}] → API のユニット {opts.Unit} / チャンネル {c.ComChannel}  {scale}");
        }
        if (opts.DryRun) return 0;

        string outFolder = Path.GetFullPath(opts.Out ?? Path.Combine(Path.GetTempPath(), "WaveLogger_LiveReadCheck"));
        Directory.CreateDirectory(outFolder);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string savedXdt = Path.Combine(outFolder, $"LiveRead_{stamp}.xdt");
        string tickCsv = Path.Combine(outFolder, $"LiveRead_{stamp}_ticks.csv");
        Console.WriteLine($"読み取り間隔 {opts.IntervalMs}ms / 保存先 {outFolder}");
        Console.WriteLine();

        var ticks = new List<Tick>();
        var tState = new Timing("GetState");
        var tCount = new Timing("GetDataCount");
        var tRead = new Timing("値の取り出し(全チャンネル)");
        var tCurrent = new Timing("GetCurrentData(全チャンネル)");
        var tTick = new Timing("1 回の読み取り全体");
        var increments = new List<(double Seconds, long Added)>();
        var notes = new List<string>();
        bool useArray = !opts.PerPoint;
        long readPos = 0;
        bool saved = false;

        var logger = new WaveLoggerSession();
        bool loggerReady = false;
        try
        {
            logger.Initialize(opts.Edition, Console.WriteLine);
            loggerReady = true;
            var hw = logger.DetectNr500Usb(opts.UsbId);
            Console.WriteLine("LOGGER 本体確認: " + hw.Description);
            if (!hw.IsDetected) throw new InvalidOperationException($"NR-500 を確認できません(USB 機器 ID {opts.UsbId})。");
            Expect("OpenFile", logger.OpenFile(template));
            object app = BorrowApplication(logger);

            long before = WithDocument<long>(app, doc => Convert.ToInt64(doc.GetDataCount()));
            Console.WriteLine($"開始前の点数(雛形のデータ): {before}");

            if (logger.GetState() != 1) throw new InvalidOperationException($"LOGGER が停止状態ではありません(GetState={logger.GetState()})。");
            Expect("StartSingleAcquisition", logger.StartSingleAcquisition());
            var wait = Stopwatch.StartNew();
            while (logger.GetState() == 1)
            {
                if (wait.Elapsed.TotalSeconds > 10) throw new TimeoutException("10 秒たっても LOGGER の記録が始まりません。");
                await Task.Delay(20);
            }
            var clock = Stopwatch.StartNew();
            Console.WriteLine($"記録開始(測定中まで {wait.Elapsed.TotalSeconds:F2} 秒)。以下の値は換算後(雛形の換算を使用)");

            bool stopSent = false;
            double nextPrint = 0;
            int stableTicks = 0;
            long lastCount = -1;
            while (true)
            {
                await Task.Delay(opts.IntervalMs);
                var tick = Stopwatch.StartNew();
                double now = clock.Elapsed.TotalSeconds;

                var sw = Stopwatch.StartNew();
                long state = logger.GetState();
                tState.Add(sw.Elapsed.TotalMilliseconds);
                if (state == 11) throw new InvalidOperationException("LOGGER がエラー停止しました(GetState=11)。");

                long count = 0, added = 0;
                double countMs = 0, readMs = 0, currentMs = 0;
                bool done = false;
                if (useArray)
                {
                    // 部品の ReadRawData(GetDataCount と全チャンネルの GetArrayData を 1 回で)。時間は「値の取り出し」に入れる。
                    sw.Restart();
                    LoggerRawDataBlock? block = null;
                    try { block = logger.ReadRawData(opts.Unit, apiChannels, readPos); }
                    catch (Exception ex)
                    {
                        notes.Add($"{now:F2} 秒: ReadRawData で例外({ex.GetType().Name}: {ex.Message})。以後 1 点ずつ取る");
                        useArray = false;
                    }
                    if (block is not null)
                    {
                        readMs = sw.Elapsed.TotalMilliseconds;
                        tRead.Add(readMs);
                        count = block.TotalCount;
                        if (count < readPos)
                        {
                            notes.Add($"{now:F2} 秒: 点数が減った({readPos} → {count})。ここから読み直す");
                            foreach (var c in channels) c.Values.Clear();
                            readPos = 0;
                            done = true;
                        }
                        else if (block.Count > 0 && !MatchesPerPoint(app, opts.Unit, channels, block, notes, now))
                        {
                            useArray = false;   // 照合が合わなければ、この回の分から 1 点ずつ取り直す
                        }
                        else
                        {
                            added = block.Count;
                            for (int i = 0; i < channels.Count; i++) channels[i].Values.AddRange(block.Values[i]);
                            readPos = count;
                            if (added > 0) increments.Add((now, added));
                            done = true;
                        }
                    }
                }
                if (!done)
                {
                    WithDocument<int>(app, doc =>
                    {
                        sw.Restart();
                        count = Convert.ToInt64(doc.GetDataCount());
                        countMs = sw.Elapsed.TotalMilliseconds;
                        tCount.Add(countMs);

                        if (count < readPos)
                        {
                            notes.Add($"{now:F2} 秒: 点数が減った({readPos} → {count})。ここから読み直す");
                            foreach (var c in channels) c.Values.Clear();
                            readPos = 0;
                        }
                        if (count > readPos)
                        {
                            sw.Restart();
                            added = count - readPos;
                            foreach (var c in channels)
                                c.Values.AddRange(ReadPerPoint((object)doc, opts.Unit, c, readPos, (int)added));
                            readMs = sw.Elapsed.TotalMilliseconds;
                            tRead.Add(readMs);
                            readPos = count;
                            increments.Add((now, added));
                        }
                        return 0;
                    });
                }

                // 部品の GetCurrentRawValue(無効値は null)。
                sw.Restart();
                foreach (var c in channels) c.Current = logger.GetCurrentRawValue(opts.Unit, c.ComChannel);
                currentMs = sw.Elapsed.TotalMilliseconds;
                tCurrent.Add(currentMs);
                tTick.Add(tick.Elapsed.TotalMilliseconds);

                // PC に届くまでの遅れ: 記録開始からの時間で本来あるはずの点数と、届いている点数の差
                double lag = now - count * interval.TotalSeconds;
                ticks.Add(new Tick(now, state, count, added, lag, tTick.Last, countMs, readMs, currentMs,
                    channels.Select(c => c.Current).ToArray()));

                if (now >= nextPrint || state == 1)
                {
                    nextPrint = Math.Floor(now) + 1;
                    var latest = channels.Where(c => c.Values.Count > 0)
                        .Select(c => $"{c.Label} {c.Scale(c.Values[^1]):G5}");
                    Console.WriteLine($"  {now,6:F2} 秒 状態 {state} 点数 {count,6}(+{added}) 遅れ {lag:F2} 秒 読み取り {tTick.Last:F1}ms | {string.Join(" / ", latest)}");
                }

                if (state == 1)
                {
                    // 止まった直後は PC への読み出しが残っていることがあるので、点数が 3 回続けて変わらないまで読む
                    stableTicks = count == lastCount ? stableTicks + 1 : 0;
                    lastCount = count;
                    if (stableTicks >= 3) break;
                    if (clock.Elapsed.TotalSeconds > opts.MaxSeconds + 15) { notes.Add("停止後 15 秒たっても点数が落ち着かない"); break; }
                    continue;
                }
                if (!stopSent && opts.StopAfter is double stopAt && now >= stopAt)
                {
                    logger.Stop();
                    stopSent = true;
                    Console.WriteLine($"  {now,6:F2} 秒 Stop を送った(--stop-after)");
                }
                else if (!stopSent && now >= opts.MaxSeconds)
                {
                    logger.Stop();
                    stopSent = true;
                    notes.Add($"{opts.MaxSeconds} 秒たっても止まらないので Stop を送った");
                }
            }
            Console.WriteLine($"記録終了({clock.Elapsed.TotalSeconds:F1} 秒、取り出した点数 {readPos})");

            Expect("SaveXdt", logger.SaveXdt(savedXdt));
            saved = true;
            Expect("CloseActiveFile", logger.CloseActiveFile());
        }
        finally
        {
            if (loggerReady)
            {
                try { if (logger.GetState() != 1) logger.Stop(); } catch { }
                await logger.ShutdownAsync(Console.WriteLine);
            }
            if (ticks.Count > 0) WriteTicks(tickCsv, channels, ticks);
        }

        Console.WriteLine();
        Console.WriteLine("■ 取り出し方: " + (useArray ? "GetArrayData(まとめて)" : "GetData(1 点ずつ)"));
        foreach (var t in new[] { tState, tCount, tRead, tCurrent, tTick }) Console.WriteLine("  " + t.Summary);
        PrintArrival(ticks, increments, interval);
        foreach (var n in notes) Console.WriteLine("  注意: " + n);
        Console.WriteLine($"  1 回ごとの記録: {tickCsv}");

        bool ok = saved && Compare(savedXdt, channels, templateFile);
        Console.WriteLine(ok ? "すべて OK" : "NG あり");
        return ok ? 0 : 1;
    }

    // ---- メーカー API(部品に無いので、確認用に直接呼ぶ) ----

    // WaveLoggerSession が持っている LOGGER の参照を借りる。解放は部品に任せる(ここでは解放しない)。
    private static object BorrowApplication(WaveLoggerSession logger) =>
        typeof(WaveLoggerSession).GetField("_application", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(logger)
        ?? throw new InvalidOperationException("WaveLoggerSession の中の LOGGER 参照を取れません(部品の作りが変わった可能性)。");

    // 部品の SaveXdt などと同じく、アクティブなドキュメントを毎回取り、使い終わったら解放する。
    private static T WithDocument<T>(object app, Func<dynamic, T> action)
    {
        dynamic application = app;
        object? doc = application.GetActiveFile();
        if (doc is null) throw new InvalidOperationException("LOGGER にアクティブなファイルがありません。");
        try { return action(doc); }
        finally { if (Marshal.IsComObject(doc)) Marshal.ReleaseComObject(doc); }
    }

    // 部品の ReadRawData で取った値の先頭と末尾を、GetData の 1 点と比べて正しく受け取れたか確かめる(部品に GetData は無い)。
    private static bool MatchesPerPoint(object app, int unit, List<Channel> channels, LoggerRawDataBlock block, List<string> notes, double now) =>
        WithDocument(app, doc =>
        {
            for (int i = 0; i < channels.Count; i++)
            {
                var c = channels[i];
                float[] values = block.Values[i];
                float first = Convert.ToSingle(doc.GetData(unit, c.ComChannel, (int)block.Start));
                float last = Convert.ToSingle(doc.GetData(unit, c.ComChannel, (int)(block.Start + values.Length - 1)));
                if (first == values[0] && last == values[^1]) continue;
                notes.Add($"{now:F2} 秒 {c.Label}: ReadRawData(GetArrayData)の値が GetData と合わない(先頭 {values[0]} / {first}、末尾 {values[^1]} / {last})。以後 1 点ずつ取る");
                return false;
            }
            return true;
        });

    // GetData で 1 点ずつ取る(--per-point と、まとめて取れないときの切り替え)。
    private static float[] ReadPerPoint(object document, int unit, Channel c, long start, int count)
    {
        dynamic doc = document;
        var values = new float[count];
        for (int i = 0; i < count; i++)
        {
            float v = Convert.ToSingle(doc.GetData(unit, c.ComChannel, (int)(start + i)));
            if (v >= InvalidThreshold) throw new InvalidOperationException($"{c.Label} の {start + i} 点目が無効値です(GetData)。");
            values[i] = v;
        }
        return values;
    }

    // ---- 結果 ----

    private static void PrintArrival(List<Tick> ticks, List<(double Seconds, long Added)> increments, TimeSpan interval)
    {
        var running = ticks.Where(t => t.State != 1 && t.Count > 0).ToList();
        if (running.Count > 0)
        {
            // 記録開始の時計は「状態が測定中に変わったのを見た時刻」なので、遅れの絶対値には数十 ms の誤差がある。ばらつきを見る。
            Console.WriteLine($"  PC に届くまでの遅れ(測定中): 最小 {running.Min(t => t.LagSeconds):F2} 秒 平均 {running.Average(t => t.LagSeconds):F2} 秒 最大 {running.Max(t => t.LagSeconds):F2} 秒");
        }
        if (increments.Count > 1)
        {
            var gaps = increments.Zip(increments.Skip(1), (a, b) => b.Seconds - a.Seconds).ToList();
            Console.WriteLine($"  点数が増えた回数 {increments.Count} / 1 回に増えた点数 平均 {increments.Average(i => i.Added):F1} 最大 {increments.Max(i => i.Added)}"
                + $"(= {increments.Average(i => i.Added) * interval.TotalSeconds:F2} 秒分) / 増える間隔 平均 {gaps.Average():F2} 秒 最大 {gaps.Max():F2} 秒");
        }
        int currentInvalid = ticks.Count(t => t.State != 1 && t.Current.Any(v => v is null));
        Console.WriteLine($"  GetCurrentData: 測定中に無効値が返った回数 {currentInvalid} / {ticks.Count(t => t.State != 1)}");
    }

    private static bool Compare(string savedXdt, List<Channel> channels, XdtFile templateFile)
    {
        var saved = XdtFile.Load(savedXdt);
        Console.WriteLine();
        Console.WriteLine($"■ 保存した xdt との突き合わせ: {savedXdt}({saved.SampleCount} 点)");
        bool ok = true;
        foreach (var c in channels)
        {
            var target = saved.Channels.FirstOrDefault(x => x.Info.Number == c.Info.Number);
            if (target is null) { Console.WriteLine($"NG: {c.Label} が保存した xdt にありません"); ok = false; continue; }
            double[] raw = target.ReadRawValues();
            int n = Math.Min(raw.Length, c.Values.Count);
            int mismatch = 0, firstMismatch = -1;
            double maxDiff = 0;
            for (int i = 0; i < n; i++)
            {
                double diff = Math.Abs(raw[i] - c.Values[i]);
                if (diff == 0) continue;
                mismatch++;
                if (firstMismatch < 0) firstMismatch = i;
                maxDiff = Math.Max(maxDiff, diff);
            }
            bool lengthOk = raw.Length == c.Values.Count;
            bool same = lengthOk && mismatch == 0;
            ok &= same;
            Console.WriteLine($"{(same ? "OK" : "NG")}: {c.Label} 点数 取り出し {c.Values.Count} / xdt {raw.Length}、値の不一致 {mismatch} 点"
                + (mismatch > 0 ? $"(最初 {firstMismatch} 点目、最大差 {maxDiff:G4})" : ""));

            // 換算: 雛形の換算で換算した値と、保存した xdt の換算値が同じか(アプリが雛形から係数を読めば済むかの確認)
            if (n > 0)
            {
                double[] scaled = target.ReadScaledValues();
                double worst = 0;
                for (int i = 0; i < n; i++) worst = Math.Max(worst, Math.Abs((float)c.Scale(c.Values[i]) - scaled[i]));
                bool sameScale = c.Info.Gain == target.Info.Gain && c.Info.Offset == target.Info.Offset;
                Console.WriteLine($"    換算: 雛形の係数 {(sameScale ? "= 保存した xdt の係数" : $"≠ 保存した xdt の係数({target.Info.Gain:G6} × 値 + {target.Info.Offset:G6})")}、"
                    + $"雛形の係数で換算した値と xdt の換算値の差 最大 {worst:G4} {c.Info.Unit}");
                ok &= sameScale;
            }
        }
        return ok;
    }

    private static void WriteTicks(string path, List<Channel> channels, List<Tick> ticks)
    {
        var sb = new StringBuilder();
        sb.Append("経過秒,状態,点数,増えた点数,遅れ秒,1回の読み取りms,GetDataCount_ms,取り出しms,GetCurrentData_ms");
        foreach (var c in channels) sb.Append(",現在値_CH").Append(c.Info.Number).Append("(換算前)");
        sb.AppendLine();
        foreach (var t in ticks)
        {
            sb.Append(string.Join(",", new[] { t.Seconds, t.State, t.Count, t.Added, t.LagSeconds, t.TickMs, t.CountMs, t.ReadMs, t.CurrentMs }
                .Select(v => Convert.ToString(v, CultureInfo.InvariantCulture))));
            foreach (var v in t.Current) sb.Append(',').Append(v?.ToString(CultureInfo.InvariantCulture) ?? "無効");
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    // ---- 補助 ----

    private static void Expect(string name, long result)
    {
        if (result != 0) throw new InvalidOperationException($"LOGGER の {name} が失敗しました(戻り値 {result})。");
    }

    private static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException(args[i] + " の値がありません。");
            switch (args[i])
            {
                case "--template": o.Template = Next(); break;
                case "--out": o.Out = Next(); break;
                case "--usb-id": o.UsbId = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--unit": o.Unit = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--interval": o.IntervalMs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--stop-after": o.StopAfter = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--max-seconds": o.MaxSeconds = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--per-point": o.PerPoint = true; break;
                case "--dry-run": o.DryRun = true; break;
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
        if (o.Template is null) throw new ArgumentException("--template で雛形 xdt を指定してください。");
        if (o.IntervalMs is < 20 or > 5000) throw new ArgumentException("--interval は 20〜5000ms です。");
        if (o.Unit is < 1 or > 8) throw new ArgumentException("--unit は 1〜8 です。");
        return o;
    }

    private static void Usage() => Console.Error.WriteLine(
        "使い方: WaveLogger.LiveReadCheck --template <xdt> [--interval 200] [--stop-after 秒] [--max-seconds 120] [--unit 1] [--per-point]"
        + " [--out <フォルダ>] [--logger legacy|x|auto] [--usb-id 0] [--dry-run]");
}
