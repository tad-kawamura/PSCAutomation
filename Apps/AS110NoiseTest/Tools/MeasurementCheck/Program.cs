using System.Globalization;
using PSCAutomation.Apps.AS110NoiseTest.Analysis;
using PSCAutomation.Apps.AS110NoiseTest.Control;
using PSCAutomation.Wrappers.Instruments;
using PSCAutomation.Apps.AS110NoiseTest.Excel;
using PSCAutomation.Apps.AS110NoiseTest.Measurement;
using PSCAutomation.Wrappers.WaveLogger.Automation;
using PSCAutomation.Wrappers.LoggerFiles;

namespace PSCAutomation.Apps.AS110NoiseTest.Tools.MeasurementCheck;

// 測定シーケンス(Measurement/MeasurementSequence)の確認ツール。
//   MeasurementCheck --sim          機器なし。WF1973・7351E・LOGGER を全部模擬し、Excel だけ本物に書く(--no-excel で Excel も使わない)
//   MeasurementCheck --logger-only  LOGGER(NR-500)だけ実機。電流は模擬(WF1973・7351E なしで LOGGER の流れを確かめる)
//   MeasurementCheck --real         全部実機(7351E は DCI・200mA 固定・BUS トリガに設定してから使う。--no-meter-config で設定しない)
// 共通オプション:
//   --sn SN60000 --memo 備忘録 --start 5 --step 5 --end 20   測定条件(既定 SN=SIM0001、備忘録=模擬、5〜20mA 5mA 刻み)
//   --out <フォルダ>     xdt の保存先(既定 %TEMP%\AS110NoiseTest_MeasurementCheck)
//   --excel <xlsx>       結果 Excel(既定 保存先\AS110ノイズ測定_確認.xlsx。無ければ雛形から作る)
//   --settle 1           電流を合わせてから記録開始までの待ち [s]
//   --final 10           終了時に合わせる電流 [mA](省略で 0V・出力 OFF)
//   --visible / --leave-open   Excel を表示 / 終了後も開いたままにする
// 模擬のオプション(--sim、--logger-only の電流):
//   --duration 2         模擬 LOGGER の記録時間 [s](1 秒以上。実機の雛形は 40 秒)
//   --yellow 10,20       この mA の制御圧力を黄になる波形にする(既定 10)
//   --red 20             この mA を赤になる波形にする(既定 20)
//   --cancel-after 3     開始 3 秒後に中断して、後始末(LOGGER 停止・出力 OFF)を確かめる
// LOGGER のオプション(--logger-only、--real):
//   --logger auto|x|legacy   WAVE LOGGER X / 旧 WAVE LOGGER(既定 auto = X を優先)
//   --usb-id 0               NR-500 の USB 機器 ID(0〜3)
internal static class Program
{
    private enum Mode { None, Sim, LoggerOnly, Real, FixDisplay }

    private sealed class Options
    {
        public Mode Mode;
        public string Sn = "SIM0001", Memo = "模擬";
        public int Start = 5, Step = 5, End = 20;
        public string? Out, Excel;
        public double Settle = 1.0, Duration = 2.0;
        public double? Final, CancelAfter;
        public bool Visible, LeaveOpen, NoExcel, NoMeterConfig;
        public HashSet<int> Yellow = [10], Red = [20];
        public WaveLoggerEdition Edition = WaveLoggerEdition.Auto;
        public int UsbId;
        public string? FixDisplayPath;   // --fix-display: 既存 xdt の制御圧力の表示レンジを平均値中心に直す(ファイルか フォルダ)
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Options opts;
        try { opts = Parse(args); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("エラー: " + ex.Message);
            return 2;
        }
        if (opts.Mode == Mode.None) { Usage(); return 2; }
        if (opts.Mode == Mode.FixDisplay)
        {
            try { return FixDisplay(opts.FixDisplayPath!); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("エラー: " + ex.Message);
                return 1;
            }
        }

        // WAVE LOGGER の wrapper は「メッセージループのある STA スレッド・同じ同期コンテキスト」で使う前提
        // (ShutdownAsync が await Task.Delay をはさむ)。コンソールでも WinForms のメッセージループの上で動かす。
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
        string templateFolder = FindTemplateFolder();
        string outFolder = Path.GetFullPath(opts.Out ?? Path.Combine(Path.GetTempPath(), "AS110NoiseTest_MeasurementCheck"));
        string excel = Path.GetFullPath(opts.Excel ?? Path.Combine(outFolder, "AS110ノイズ測定_確認.xlsx"));
        var settings = new MeasurementSettings
        {
            TemplateXdtPath = Path.Combine(templateFolder, "AS110ノイズ測定用.xdt"),
            TemplateExcelPath = Path.Combine(templateFolder, "AS110ノイズ測定_雛形.xlsx"),
            SettleSeconds = opts.Settle,
            FinalMilliamps = opts.Final,
        };
        if (opts.Mode == Mode.Sim)
        {
            // Start 直後は StateReadDelayMilliseconds の間 GetState を読まないので、それより短い記録は「始まらない」扱いになる。
            if (opts.Duration * 1000 < settings.StateReadDelayMilliseconds * 2)
                throw new ArgumentException($"--duration は {settings.StateReadDelayMilliseconds * 2 / 1000.0:0.#} 秒以上にしてください(状態を読み始める前に終わってしまう)。");
            settings = settings with { AcquisitionTimeoutSeconds = Math.Max(10, opts.Duration * 2 + 5) };
        }
        var plan = new MeasurementPlan(opts.Sn, opts.Memo, opts.Start, opts.Step, opts.End, outFolder, excel);
        var controlSettings = new CurrentControlSettings();
        Console.WriteLine($"モード: {opts.Mode} / 保存先 {outFolder} / Excel {(opts.NoExcel ? "使わない" : excel)}");

        SimulatedPlant? plant = null;
        SimulatedLogger? simLogger = null;
        Adcmt7351ESession? meter = null;
        Wf1973DcSource? source = null;
        WaveLoggerDevice? realLogger = null;
        INoiseResultWriter? writer = null;
        try
        {
            ICurrentMeter currentMeter;
            IOffsetVoltageSource voltageSource;
            if (opts.Mode == Mode.Real)
            {
                meter = Adcmt7351ESession.Connect();
                Console.WriteLine($"7351E : {meter.Identity} ({meter.Address})");
                if (!opts.NoMeterConfig)
                {
                    var meterSettings = new Adcmt7351ESettings();
                    IReadOnlyList<string> oldErrors = meter.ApplySettings(meterSettings);
                    if (oldErrors.Count > 0) Console.WriteLine("7351E 設定前のエラー・ログ: " + string.Join(" / ", oldErrors));
                    Console.WriteLine($"7351E 設定: {string.Join(" ", meterSettings.ToCommands())}");
                }
                source = Wf1973DcSource.Connect();
                Console.WriteLine($"WF1973: {source.Identity} ({source.Address})");
                currentMeter = new Adcmt7351ECurrentMeter(meter);
                voltageSource = new Wf1973OffsetVoltageSource(source);
            }
            else
            {
                // 公称 6mA/V からずらした特性(CurrentControlCheck --sim と同じ)
                plant = new SimulatedPlant(milliampsPerVolt: 5.85, offsetMilliamps: 0.04, noiseMilliamps: 0.004);
                currentMeter = plant;
                voltageSource = plant;
                Console.WriteLine("WF1973・7351E: 模擬(5.85 mA/V + 0.04 mA)");
            }

            ILoggerDevice logger;
            if (opts.Mode == Mode.Sim)
            {
                WaveProfile ProfileOf(int ma) => opts.Red.Contains(ma) ? WaveProfile.Red : opts.Yellow.Contains(ma) ? WaveProfile.Yellow : WaveProfile.Normal;
                simLogger = new SimulatedLogger(TimeSpan.FromSeconds(opts.Duration), () => plant!.TrueMilliamps, ProfileOf, Console.WriteLine);
                logger = simLogger;
            }
            else
            {
                realLogger = WaveLoggerDevice.Connect(opts.Edition, opts.UsbId, Console.WriteLine);
                Console.WriteLine($"LOGGER: {realLogger.Description}");
                logger = realLogger;
            }

            writer = opts.NoExcel ? new ConsoleResultWriter() : NoiseResultWorkbook.Open(excel, settings.TemplateExcelPath, opts.Visible);
            if (writer is NoiseResultWorkbook wb0) Console.WriteLine($"Excel: {wb0.Path}");

            var sequence = new MeasurementSequence(logger, currentMeter, voltageSource, controlSettings, writer, settings)
            {
                Log = Console.WriteLine,
            };

            using var cancel = new CancellationTokenSource();
            if (opts.CancelAfter is double after) cancel.CancelAfter(TimeSpan.FromSeconds(after));

            int failures = 0;
            IReadOnlyList<MeasurementStepResult> results;
            try
            {
                results = sequence.Run(plan, cancel.Token);
            }
            catch (OperationCanceledException) when (opts.CancelAfter is not null)
            {
                bool off = plant is null || (!plant.IsOutputOn && plant.Volts == 0);
                bool stopped = logger.GetState() == LoggerState.Stopped;
                Console.WriteLine($"{(off && stopped ? "OK" : "NG")}: 中断 → 出力 OFF={off} / LOGGER 停止={stopped}");
                return off && stopped ? 0 : 1;
            }

            Console.WriteLine();
            Console.WriteLine("指令mA  実測mA   電圧V  補正  制御圧平均  p-p   判定   所要秒  xdt");
            foreach (var r in results)
            {
                var m = r.Analysis.Metrics;
                Console.WriteLine($"{r.CommandMilliamps,5}  {r.Current.MeasuredMilliamps,7:F3}  {r.Current.OffsetVolts,6:F4}  {r.Current.Adjustments,3}  " +
                                  $"{r.Analysis.ControlPressure.Mean,9:F2}  {m.PeakToPeakKpa,4:F1}  {m.Grade,-6}  {r.Elapsed.TotalSeconds,6:F1}  {Path.GetFileName(r.XdtPath)}");
            }
            if (results.Count > 0)
                Console.WriteLine($"1 電流あたり 平均 {results.Average(r => r.Elapsed.TotalSeconds):F1} 秒 / 最長 {results.Max(r => r.Elapsed.TotalSeconds):F1} 秒(OpenFile から CloseActiveFile まで)");

            if (opts.Mode == Mode.Sim)
            {
                // 模擬の波形どおりに判定されたか、電流が許容幅に入っていたか。
                foreach (var r in results)
                {
                    NoiseGrade expected = opts.Red.Contains(r.CommandMilliamps) ? NoiseGrade.Red
                        : opts.Yellow.Contains(r.CommandMilliamps) ? NoiseGrade.Yellow : NoiseGrade.Normal;
                    if (r.Analysis.Metrics.Grade != expected)
                    {
                        Console.WriteLine($"NG: {r.CommandMilliamps}mA の判定が {r.Analysis.Metrics.Grade}(期待 {expected})");
                        failures++;
                    }
                    if (Math.Abs(r.Current.MeasuredMilliamps - r.CommandMilliamps) > controlSettings.ToleranceMilliamps)
                    {
                        Console.WriteLine($"NG: {r.CommandMilliamps}mA の実測が {r.Current.MeasuredMilliamps:F3}mA");
                        failures++;
                    }
                    if (Math.Abs(r.Analysis.Current.Mean - r.CommandMilliamps) > 0.1)
                    {
                        Console.WriteLine($"NG: {r.CommandMilliamps}mA の xdt の電流 CH が {r.Analysis.Current.Mean:F3}mA");
                        failures++;
                    }
                }
                bool finalOk = opts.Final is double f
                    ? plant!.IsOutputOn && Math.Abs(plant.TrueMilliamps - f) <= controlSettings.ToleranceMilliamps
                    : !plant!.IsOutputOn && plant.Volts == 0;
                Console.WriteLine($"{(finalOk ? "OK" : "NG")}: 終了時の電流 {(opts.Final is double ff ? $"{ff}mA" : "出力 OFF")} → 実際 {(plant.IsOutputOn ? $"{plant.TrueMilliamps:F3}mA" : "OFF")}");
                if (!finalOk) failures++;
                if (results.Count != plan.Currents().Count) { Console.WriteLine("NG: 点数が合いません"); failures++; }
            }

            if (writer is NoiseResultWorkbook wb)
            {
                Console.WriteLine("Excel で再計算した行の集計:");
                foreach (int row in results.Select(r => r.ExcelRow).Where(r => r > 0).Distinct())
                {
                    var s = wb.ReadRowSummary(row);
                    Console.WriteLine($"  行{row}: 最大p-p={s.MaxPp?.ToString("F1") ?? "-"}  黄={s.Yellow?.ToString() ?? "-"}  赤={s.Red?.ToString() ?? "-"}  判定={s.Judge}");
                }
                if (opts.LeaveOpen) wb.LeaveOpenForUser();
            }
            if (opts.Mode == Mode.Sim) Console.WriteLine(failures == 0 ? "模擬確認: すべて OK" : $"模擬確認: NG {failures} 件");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            if (realLogger is not null)
            {
                try { await realLogger.ShutdownAsync(Console.WriteLine); }
                catch (Exception ex) { Console.WriteLine("LOGGER の終了に失敗: " + ex.Message + "(LOGGER 画面を確認してください)"); }
                realLogger.Dispose();
            }
            (writer as IDisposable)?.Dispose();
            source?.Dispose();
            meter?.Dispose();
        }
    }

    private static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} に値がありません。");
            switch (a)
            {
                case "--sim": o.Mode = Mode.Sim; break;
                case "--logger-only": o.Mode = Mode.LoggerOnly; break;
                case "--real": o.Mode = Mode.Real; break;
                case "--fix-display": o.Mode = Mode.FixDisplay; o.FixDisplayPath = Next(); break;
                case "--sn": o.Sn = Next(); break;
                case "--memo": o.Memo = Next(); break;
                case "--start": o.Start = ParseInt(Next()); break;
                case "--step": o.Step = ParseInt(Next()); break;
                case "--end": o.End = ParseInt(Next()); break;
                case "--out": o.Out = Next(); break;
                case "--excel": o.Excel = Next(); break;
                case "--settle": o.Settle = ParseDouble(Next()); break;
                case "--final": o.Final = ParseDouble(Next()); break;
                case "--duration": o.Duration = ParseDouble(Next()); break;
                case "--cancel-after": o.CancelAfter = ParseDouble(Next()); break;
                case "--yellow": o.Yellow = ParseList(Next()); break;
                case "--red": o.Red = ParseList(Next()); break;
                case "--visible": o.Visible = true; break;
                case "--leave-open": o.LeaveOpen = o.Visible = true; break;
                case "--no-excel": o.NoExcel = true; break;
                case "--no-meter-config": o.NoMeterConfig = true; break;
                case "--usb-id": o.UsbId = ParseInt(Next()); break;
                case "--logger":
                    o.Edition = Next().ToLowerInvariant() switch
                    {
                        "auto" => WaveLoggerEdition.Auto,
                        "x" => WaveLoggerEdition.WaveLoggerX,
                        "legacy" => WaveLoggerEdition.Legacy,
                        var v => throw new ArgumentException("--logger は auto / x / legacy です:" + v),
                    };
                    break;
                default: throw new ArgumentException("不明なオプションです:" + a);
            }
        }
        return o;
    }

    private static int ParseInt(string s) => int.Parse(s, CultureInfo.InvariantCulture);
    private static double ParseDouble(string s) => double.Parse(s, CultureInfo.InvariantCulture);
    private static HashSet<int> ParseList(string s) =>
        s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ParseInt).ToHashSet();

    // 既存の xdt(ファイル、またはフォルダ直下の *.xdt)の制御圧力の表示レンジを平均値中心に直す。
    // アプリが表示レンジを直す前に保存した xdt(2026-11-03 の実機確認など)の後始末用。機器は使わない。
    private static int FixDisplay(string target)
    {
        string[] files = Directory.Exists(target)
            ? Directory.GetFiles(target, "*.xdt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray()
            : File.Exists(target) ? [target]
            : throw new FileNotFoundException("xdt かフォルダが見つかりません。", target);
        var settings = new MeasurementSettings { TemplateXdtPath = files[0], TemplateExcelPath = "-" };   // 刻みの既定値を使うだけ
        var channels = new LoggerChannelMap();
        int changed = 0, same = 0, failed = 0;
        foreach (string f in files)
        {
            try
            {
                double mean = XdtReader.Read(f, [channels.ControlPressure]).Values[channels.ControlPressure].Average();
                XdtDisplayRange.Range before = XdtDisplayRange.Read(f, channels.ControlPressure);
                bool did = XdtDisplayRange.Center(f, channels.ControlPressure, mean, settings.DisplayCenterStepKpa);
                XdtDisplayRange.Range after = XdtDisplayRange.Read(f, channels.ControlPressure);
                Console.WriteLine($"{(did ? "直した" : "そのまま")}  平均 {mean,7:F2}  {before.Lower,6:0.#}〜{before.Upper,-6:0.#} → {after.Lower,6:0.#}〜{after.Upper,-6:0.#}  {Path.GetFileName(f)}");
                if (did) changed++; else same++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                Console.WriteLine($"失敗    {ex.Message}  {Path.GetFileName(f)}");
                failed++;
            }
        }
        Console.WriteLine($"直した {changed} 本 / そのまま {same} 本 / 失敗 {failed} 本");
        return failed == 0 ? 0 : 1;
    }

    private static void Usage()
    {
        Console.WriteLine("使い方: MeasurementCheck --sim | --logger-only | --real [--sn SN] [--memo 備忘録] [--start 5 --step 5 --end 20]");
        Console.WriteLine("        [--out フォルダ] [--excel xlsx] [--settle 1] [--final mA] [--visible] [--leave-open] [--no-excel]");
        Console.WriteLine("        模擬: [--duration 2] [--yellow 10,20] [--red 20] [--cancel-after 3]   LOGGER: [--logger auto|x|legacy] [--usb-id 0]");
        Console.WriteLine("        MeasurementCheck --fix-display <xdt かフォルダ>   既存 xdt の制御圧力の表示レンジを平均値中心に直す");
    }

    // 雛形(xdt・Excel)のフォルダ。exe の場所から上にたどって「雛形\AS110ノイズ測定用.xdt」がある所を使う。
    // 開発中は bin\Debug\... の上のリポジトリ直下の 雛形、発行した exe(発行.cmd)では exe の隣にコピーした 雛形 が見つかる。
    private static string FindTemplateFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "雛形");
            if (File.Exists(Path.Combine(candidate, "AS110ノイズ測定用.xdt")) && File.Exists(Path.Combine(candidate, "AS110ノイズ測定_雛形.xlsx")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"雛形フォルダ(AS110ノイズ測定用.xdt と AS110ノイズ測定_雛形.xlsx)が {AppContext.BaseDirectory} から上に見つかりません。");
    }
}
