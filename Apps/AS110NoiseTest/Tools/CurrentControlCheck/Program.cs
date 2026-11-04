using System.Globalization;
using PSCAutomation.Apps.AS110NoiseTest.Control;
using PSCAutomation.Wrappers.Instruments;

namespace PSCAutomation.Apps.AS110NoiseTest.Tools.CurrentControlCheck;

// 電流フィードバック制御の確認ツール。
//   CurrentControlCheck --sim            機器なしで制御ロジックを確認(模擬の電流特性)
//   CurrentControlCheck 5 10 20          実機で各 mA に合わせ、終了時に 0V・出力 OFF
//   CurrentControlCheck --hold 3 5 10    合わせた後に 3 秒間 7351E を読み続け、安定を見る
//   CurrentControlCheck --gain 5.5 10    初期ゲインをわざとずらし、補正が効くかを見る
// 7351E の設定(ADC コマンド。2026-11-03 実機確認済み):
//   CurrentControlCheck --meter-info            7351E の現在の設定とエラー・ログを表示(設定は変えない)
//   CurrentControlCheck --meter-config 5 10     DCI・200mA 固定・演算 OFF・BUS トリガに設定してから電流制御
//     --rate fast|med|slow1|slow2               サンプリング・レート(指定すると --meter-config 扱い)
//     --smooth 10                               スムージング回数(2〜100、指定すると --meter-config 扱い)
//     --trigger imm|bus                         トリガ・ソース(既定 bus、指定すると --meter-config 扱い)
//   CurrentControlCheck --rate-scan 10          10mA に合わせ、レートごとの読み取り間隔とばらつきを測る
//   CurrentControlCheck --step-test             電流を 10⇔20mA に変えた直後の読み取りを、トリガ・ソースごとに比べる
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool simulate = false, meterInfo = false, meterConfig = false, rateScan = false, stepTest = false;
        double holdSeconds = 0;
        double? gain = null;
        var meterSettings = new Adcmt7351ESettings();
        var targets = new List<double>();
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--sim": simulate = true; continue;
                    case "--meter-info": meterInfo = true; continue;
                    case "--meter-config": meterConfig = true; continue;
                    case "--rate-scan": rateScan = true; continue;
                    case "--step-test": stepTest = true; continue;
                }
                if (i + 1 < args.Length)
                {
                    switch (args[i])
                    {
                        case "--hold": holdSeconds = Parse(args[++i]); continue;
                        case "--gain": gain = Parse(args[++i]); continue;
                        case "--rate":
                            meterSettings = meterSettings with { SamplingRate = ParseRate(args[++i]) };
                            meterConfig = true;
                            continue;
                        case "--smooth":
                            meterSettings = meterSettings with { SmoothingCount = int.Parse(args[++i], CultureInfo.InvariantCulture) };
                            meterConfig = true;
                            continue;
                        case "--trigger":
                            meterSettings = meterSettings with { TriggerSource = ParseTrigger(args[++i]) };
                            meterConfig = true;
                            continue;
                    }
                }
                if (args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("不明なオプションです:" + args[i]);
                targets.Add(Parse(args[i]));
            }
            if (targets.Count == 0) targets.AddRange(rateScan ? [10] : [5, 10, 20]);
            meterSettings.Validate();

            var settings = new CurrentControlSettings();
            if (gain is double g) settings = settings with { MilliampsPerVolt = g };

            if (simulate) return RunSimulation(settings, targets);
            if (meterInfo) return RunMeterInfo();
            if (stepTest) return RunStepTest(meterSettings);
            if (rateScan) return RunRateScan(settings, meterSettings, targets[0]);
            return RunHardware(settings, meterConfig ? meterSettings : null, targets, holdSeconds);
        }
        catch (Exception ex)
        {
            Console.WriteLine("エラー: " + ex.Message);
            return 1;
        }
    }

    private static int RunHardware(
        CurrentControlSettings settings, Adcmt7351ESettings? meterSettings, List<double> targets, double holdSeconds)
    {
        using var meter = Adcmt7351ESession.Connect();
        Console.WriteLine($"7351E : {meter.Identity} ({meter.Address})");
        if (meterSettings is not null) Configure(meter, meterSettings);
        using var source = Wf1973DcSource.Connect();
        Console.WriteLine($"WF1973: {source.Identity} ({source.Address})");
        Console.WriteLine($"開始前の実測: {meter.ReadMilliamps():F3} mA ({meter.LastRawReading})");

        var controller = new CurrentController(new Adcmt7351ECurrentMeter(meter), new Wf1973OffsetVoltageSource(source), settings) { Log = Console.WriteLine };
        try
        {
            foreach (double target in targets)
            {
                CurrentControlResult result = controller.Regulate(target);
                Report(result);
                if (holdSeconds > 0) Hold(meter, target, settings, holdSeconds);
            }
            Console.WriteLine($"推定ゲイン: {controller.EstimatedMilliampsPerVolt:F4} mA/V");
        }
        finally
        {
            controller.TurnOff();
            meter.DiscardStaleReadings();
            Console.WriteLine($"終了後の実測: {meter.ReadMilliamps():F3} mA");
        }
        return 0;
    }

    // 7351E の設定を読むだけ。WF1973 には触らない。
    private static int RunMeterInfo()
    {
        using var meter = Adcmt7351ESession.Connect();
        Console.WriteLine($"7351E : {meter.Identity} ({meter.Address})");
        // 設定によっては電流として解釈できないので、生の応答をそのまま表示する。
        meter.DiscardReading();
        Console.WriteLine($"読み取り: {meter.LastRawReading}");
        foreach (var (name, query, response) in meter.ReadConfiguration())
            Console.WriteLine($"  {name,-12} {query,-6} → {response}");
        IReadOnlyList<string> errors = meter.DrainErrors();
        Console.WriteLine(errors.Count == 0 ? "エラー・ログ: なし" : "エラー・ログ: " + string.Join(" / ", errors));
        // 問い合わせの後も測定値を読めるか(読み取りの流れが乱れていないか)。
        meter.DiscardReading();
        Console.WriteLine($"読み取り: {meter.LastRawReading}");
        return 0;
    }

    // 電流読み取り用の設定を送り、反映を表示する。
    private static void Configure(Adcmt7351ESession meter, Adcmt7351ESettings meterSettings)
    {
        IReadOnlyList<string> oldErrors = meter.ApplySettings(meterSettings);
        if (oldErrors.Count > 0) Console.WriteLine("設定前のエラー・ログ: " + string.Join(" / ", oldErrors));
        Console.WriteLine($"7351E 設定: {string.Join(" ", meterSettings.ToCommands())}");
    }

    // 合わせた電流のまま、サンプリング・レートごとの読み取り間隔とばらつきを測る。
    // 最後に meterSettings(既定は Slow2)へ戻す。
    private static int RunRateScan(CurrentControlSettings settings, Adcmt7351ESettings meterSettings, double target)
    {
        const int Samples = 10;
        using var meter = Adcmt7351ESession.Connect();
        Console.WriteLine($"7351E : {meter.Identity} ({meter.Address})");
        Configure(meter, meterSettings);
        using var source = Wf1973DcSource.Connect();
        Console.WriteLine($"WF1973: {source.Identity} ({source.Address})");

        var controller = new CurrentController(new Adcmt7351ECurrentMeter(meter), new Wf1973OffsetVoltageSource(source), settings) { Log = Console.WriteLine };
        try
        {
            Report(controller.Regulate(target));
            foreach (Adcmt7351ESamplingRate rate in Enum.GetValues<Adcmt7351ESamplingRate>())
            {
                meter.ApplySettings(meterSettings with { SamplingRate = rate });
                var values = new List<double>();
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < Samples; i++) values.Add(meter.ReadMilliamps());
                double interval = stopwatch.Elapsed.TotalMilliseconds / Samples;
                double mean = values.Average();
                double sd = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1));
                Console.WriteLine($"  {rate,-6}: 間隔 {interval,6:F0} ms / 平均 {mean:F4} mA / " +
                                  $"min {values.Min():F3} max {values.Max():F3} / 標準偏差 {sd:F4} mA");
            }
        }
        finally
        {
            controller.TurnOff();
            try
            {
                meter.ApplySettings(meterSettings);
                Console.WriteLine($"7351E をレート {meterSettings.SamplingRate} に戻しました");
            }
            catch (Exception ex) { Console.WriteLine("7351E の設定を戻せません:" + ex.Message); }
        }
        return 0;
    }

    // 電圧を変えた直後に何が読めるかを、読み取り方ごとに比べる(10mA ⇔ 20mA 付近、公称ゲインで指令)。
    //   immediate-idle : TRS0。変更前にしばらく読まずに置く(7351E 側に古い値が溜まる状況)
    //   immediate-read : TRS0。変更の直前に 1 回読んでおく
    //   bus            : TRS3。読むたびに *TRG(ReadMilliamps が送る)
    private static int RunStepTest(Adcmt7351ESettings meterSettings)
    {
        const double LowVolts = 10 / 6.0, HighVolts = 20 / 6.0;
        using var meter = Adcmt7351ESession.Connect();
        Console.WriteLine($"7351E : {meter.Identity} ({meter.Address})");
        Configure(meter, meterSettings);
        using var source = Wf1973DcSource.Connect();
        Console.WriteLine($"WF1973: {source.Identity} ({source.Address})");
        try
        {
            source.SetOffsetVolts(LowVolts);
            source.SetOutput(true);
            foreach (string mode in new[] { "immediate-idle", "immediate-read", "bus" })
            {
                meter.SetTriggerSource(mode == "bus" ? Adcmt7351ETriggerSource.Bus : Adcmt7351ETriggerSource.Immediate);
                Console.WriteLine($"[{mode}]");
                foreach (double volts in new[] { HighVolts, LowVolts, HighVolts, LowVolts })
                {
                    Thread.Sleep(1500);
                    if (mode == "immediate-read") meter.ReadMilliamps();
                    source.SetOffsetVolts(volts);
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    var text = new List<string>();
                    for (int i = 0; i < 4; i++)
                    {
                        double value = meter.ReadMilliamps();
                        text.Add($"{value,7:F3}@{stopwatch.ElapsedMilliseconds,4}ms");
                    }
                    Console.WriteLine($"  → {volts * 6:F0}mA 指令: {string.Join("  ", text)}");
                }
            }
            // 出力 OFF → ON の直後(BUS)。ON の後の待ち時間ごとに比べる。
            Console.WriteLine("[bus 出力 OFF→ON]");
            foreach (int waitMs in new[] { 0, 0, 20, 50, 100, 200 })
            {
                source.SetOutput(false);
                Thread.Sleep(1500);
                source.SetOffsetVolts(HighVolts);
                source.SetOutput(true);
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                if (waitMs > 0) Thread.Sleep(waitMs);
                var text = new List<string>();
                for (int i = 0; i < 4; i++)
                    text.Add($"{meter.ReadMilliamps(),7:F3}@{stopwatch.ElapsedMilliseconds,4}ms");
                Console.WriteLine($"  待ち {waitMs,3}ms: {string.Join("  ", text)}");
            }
        }
        finally
        {
            source.SetOffsetVolts(0);
            source.SetOutput(false);
            try
            {
                meter.SetTriggerSource(Adcmt7351ETriggerSource.Immediate);
                Console.WriteLine("出力 OFF・トリガ・ソース IMMEDIATE に戻しました");
            }
            catch (Exception ex) { Console.WriteLine("トリガ・ソースを戻せません(前面パネルで確認):" + ex.Message); }
        }
        return 0;
    }

    // 合わせた後も許容幅に入り続けるかを見る(記録中の電流の安定性の目安)。
    private static void Hold(Adcmt7351ESession meter, double target, CurrentControlSettings settings, double seconds)
    {
        var values = new List<double>();
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until) values.Add(meter.ReadMilliamps());
        int outside = values.Count(v => Math.Abs(v - target) > settings.ToleranceMilliamps);
        Console.WriteLine($"  保持 {seconds:F1}s: {values.Count} 回 min {values.Min():F3} / max {values.Max():F3} mA、許容外 {outside} 回");
    }

    private static int RunSimulation(CurrentControlSettings settings, List<double> targets)
    {
        // 公称 6mA/V からずらした特性で、補正とゲイン学習が効くかを見る。
        var plant = new SimulatedPlant(milliampsPerVolt: 5.85, offsetMilliamps: 0.04, noiseMilliamps: 0.004);
        var controller = new CurrentController(plant, plant, settings) { Log = Console.WriteLine };
        int failures = 0;
        foreach (double target in targets)
        {
            CurrentControlResult result = controller.Regulate(target);
            Report(result);
            if (Math.Abs(plant.TrueMilliamps - target) > settings.ToleranceMilliamps) failures++;
        }
        controller.TurnOff();

        // ゲイン未学習のままいきなり 35mA でも、上限保護で止まらずに合わせられるか(実機は公称より約 0.6% 大きい)。
        var cold = new SimulatedPlant(milliampsPerVolt: 6.04, offsetMilliamps: 0, noiseMilliamps: 0.002);
        try
        {
            CurrentControlResult r = new CurrentController(cold, cold, settings).Regulate(35);
            bool ok = Math.Abs(cold.TrueMilliamps - 35) <= settings.ToleranceMilliamps;
            Console.WriteLine($"{(ok ? "OK" : "NG")}: 未学習で 35mA → 実測 {r.MeasuredMilliamps:F3} mA / 補正 {r.Adjustments} 回");
            if (!ok) failures++;
        }
        catch (CurrentControlException ex)
        {
            Console.WriteLine("NG: 未学習で 35mA → " + ex.Message);
            failures++;
        }

        // 上限超過で止まるか(電流が想定の 2 倍流れる故障を模擬)。
        var broken = new SimulatedPlant(milliampsPerVolt: 12.0, offsetMilliamps: 0, noiseMilliamps: 0);
        var guarded = new CurrentController(broken, broken, settings);
        try
        {
            guarded.Regulate(34);
            Console.WriteLine("NG: 上限超過で止まりませんでした");
            failures++;
        }
        catch (CurrentControlException ex)
        {
            bool off = !broken.IsOutputOn && broken.Volts == 0;
            Console.WriteLine($"{(off ? "OK" : "NG")}: 上限超過 → {ex.Message}");
            if (!off) failures++;
        }

        foreach (var (raw, expected) in new[] { ("DCI_ +006.027E-03", 6.027), ("DCI_ -000.008E-03", -0.008) })
        {
            double parsed = Adcmt7351ESession.ParseMilliamps(raw);
            bool ok = Math.Abs(parsed - expected) < 1e-9;
            Console.WriteLine($"{(ok ? "OK" : "NG")}: {raw} → {parsed} mA");
            if (!ok) failures++;
        }
        try
        {
            Adcmt7351ESession.ParseMilliamps("DCV_ +001.000E+00");
            Console.WriteLine("NG: DCV を電流として受け付けました");
            failures++;
        }
        catch (InvalidOperationException) { Console.WriteLine("OK: DCV は拒否"); }

        failures += CheckMeterCommands();
        Console.WriteLine(failures == 0 ? "模擬確認: すべて OK" : $"模擬確認: NG {failures} 件");
        return failures == 0 ? 0 : 1;
    }

    private static void Report(CurrentControlResult r) =>
        Console.WriteLine($"  → 目標 {r.TargetMilliamps:F3} mA / 実測 {r.MeasuredMilliamps:F3} mA / " +
                          $"{r.OffsetVolts:F4} V / 補正 {r.Adjustments} 回 / 読み取り {r.Readings.Count} 回 / " +
                          $"{r.Elapsed.TotalSeconds:F1} s");

    // 7351E のコマンド組み立てと応答解釈(機器なしで確かめられる部分)。NG の件数を返す。
    private static int CheckMeterCommands()
    {
        int failures = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine($"{(ok ? "OK" : "NG")}: {label}");
            if (!ok) failures++;
        }
        void Rejects(string raw)
        {
            try
            {
                Adcmt7351ESession.ParseMilliamps(raw);
                Check(false, raw + " を電流として受け付けました");
            }
            catch (InvalidOperationException) { Check(true, raw + " は拒否"); }
        }

        string defaults = string.Join(" ", new Adcmt7351ESettings().ToCommands());
        Check(defaults == "F5 R6 PR4 AZ1 SM0 NL0 SC0 DB0 MN0 CO0 H1 TRS3 INIC1", "既定の設定コマンド " + defaults);
        var custom = new Adcmt7351ESettings
        {
            SamplingRate = Adcmt7351ESamplingRate.Medium,
            AutoZero = Adcmt7351EAutoZero.Once,
            SmoothingCount = 10,
            LineFrequency = Adcmt7351ELineFrequency.Hz50,
            TriggerSource = Adcmt7351ETriggerSource.Immediate,
        };
        string customText = string.Join(" ", custom.ToCommands());
        Check(customText == "F5 R6 PR2 AZ2 TI10 SM1 NL0 SC0 DB0 MN0 CO0 H1 LF0 TRS0 INIC1", "変更した設定コマンド " + customText);
        var expected = custom.ExpectedResponses().ToDictionary(x => x.Query, x => x.Expected);
        Check(expected["AZ?"] == "AZ0" && expected["TI?"] == "TI10" && expected["LF?"] == "LF0" && expected["TRS?"] == "TRS0",
              "設定の確認内容(Once は AZ0)");
        try
        {
            new Adcmt7351ESettings { SmoothingCount = 1 }.Validate();
            Check(false, "スムージング 1 回を受け付けました");
        }
        catch (ArgumentException) { Check(true, "スムージング 1 回は拒否"); }
        try
        {
            new Adcmt7351ESettings { TriggerSource = Adcmt7351ETriggerSource.Manual }.Validate();
            Check(false, "トリガ・ソース MANUAL を受け付けました");
        }
        catch (ArgumentException) { Check(true, "トリガ・ソース MANUAL は拒否"); }

        Check(Adcmt7351ESession.SameSetting("F5", "F05"), "F5 と F05 は同じ");
        Check(Adcmt7351ESession.SameSetting("TI10", "TI010"), "TI10 と TI010 は同じ");
        Check(!Adcmt7351ESession.SameSetting("R6", "R0"), "R6 と R0 は違う");
        Check(!Adcmt7351ESession.SameSetting("PR4", "AZ4"), "PR4 と AZ4 は違う");
        Check(Adcmt7351ESession.ParseErrorCode("+000,\"No error\"") == 0, "ERR? +000 → 0");
        Check(Adcmt7351ESession.ParseErrorCode("-100,\"Command error\"") == -100, "ERR? -100 → -100");
        Check(Adcmt7351ESession.IsMeasurement("DCI_ +006.027E-03"), "測定値を見分ける");
        Check(!Adcmt7351ESession.IsMeasurement("TRS0") && !Adcmt7351ESession.IsMeasurement("+000,\"No error\"") &&
              !Adcmt7351ESession.IsMeasurement("TRD+0.0000E+00"), "応答を測定値と取り違えない");
        Rejects("DCIO +9.99999E+37");
        Rejects("DCIN +001.000E-03");
        Rejects("DCI_ +9.99999E+37");
        return failures;
    }

    private static Adcmt7351ESamplingRate ParseRate(string text) => text.ToLowerInvariant() switch
    {
        "fast" => Adcmt7351ESamplingRate.Fast,
        "med" or "medium" => Adcmt7351ESamplingRate.Medium,
        "slow1" => Adcmt7351ESamplingRate.Slow1,
        "slow2" => Adcmt7351ESamplingRate.Slow2,
        _ => throw new ArgumentException("--rate は fast / med / slow1 / slow2 のどれかにしてください:" + text),
    };

    private static Adcmt7351ETriggerSource ParseTrigger(string text) => text.ToLowerInvariant() switch
    {
        "imm" or "immediate" => Adcmt7351ETriggerSource.Immediate,
        "bus" => Adcmt7351ETriggerSource.Bus,
        _ => throw new ArgumentException("--trigger は imm / bus のどちらかにしてください:" + text),
    };

    private static double Parse(string text) => double.Parse(text, CultureInfo.InvariantCulture);
}

// 7351E と WF1973 の代わり。電圧変更直後の 1 回目は変化前の値を返す(実機と同じ)。
internal sealed class SimulatedPlant(double milliampsPerVolt, double offsetMilliamps, double noiseMilliamps)
    : ICurrentMeter, IOffsetVoltageSource
{
    private readonly Random _random = new(1);
    private double _staleMilliamps;
    private bool _changed;

    public double Volts { get; private set; }
    public bool IsOutputOn { get; private set; }
    public int StaleReadingsAfterChange => 1;
    public double TrueMilliamps => IsOutputOn ? Volts * milliampsPerVolt + offsetMilliamps : 0;

    public void SetOffsetVolts(double volts)
    {
        _staleMilliamps = TrueMilliamps;
        Volts = volts;
        _changed = true;
    }

    public void SetOutput(bool on)
    {
        _staleMilliamps = TrueMilliamps;
        IsOutputOn = on;
        _changed = true;
    }

    public double ReadMilliamps()
    {
        double value = _changed ? _staleMilliamps : TrueMilliamps;
        _changed = false;
        return Math.Round(value + (_random.NextDouble() * 2 - 1) * noiseMilliamps, 3);
    }
}
