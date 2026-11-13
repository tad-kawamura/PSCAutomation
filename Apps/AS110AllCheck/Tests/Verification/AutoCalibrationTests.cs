using System.Text;
using PSCAutomation.Apps.AS110AllCheck;

// 校正の自動読み取り(段階 1)を、機器なしの模擬(GC16・WF1973・NR-500・人)で確かめる。
// 弁は実装(ManualBallValveSwitcher)のまま使い、人の「OK」で模擬の弁の向きが変わる。時計は待つたびに進む模擬。
internal static class AutoCalibrationTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    public static async Task RunAsync()
    {
        var model = ProductModel.All.Single(m => m.Name == "AS110-035"); // 539 kPa

        // 1. ふつうの流れ: ①〜④ で 8 項目ができ、後片付け(出力停止・GC16 を RM,0)をする。弁は最後に測定の向きのままなので戻す知らせは出さない。
        {
            var rig = new Rig();
            var result = await rig.Procedure().RunAsync(model, CancellationToken.None);
            var c = result.Calibration;
            Check(c.Current.Output1 == 0 && c.Current.Output2 == 29.9f && Near(c.Current.Input1, Rig.Ch1Volts(0)) && Near(c.Current.Input2, Rig.Ch1Volts(5)),
                "auto calibration: current points are CH1 at 0 V and at 5 V with the multimeter mA");
            Check(Near(c.Pressure.Output1, 0.03) && Near(c.Pressure.Input1, Rig.Ch2Volts(0)) && Near(c.Pressure.Output2, 539.43) && Near(c.Pressure.Input2, Rig.Ch2Volts(539.4)),
                "auto calibration: pressure points are the zero-checked GC16 value and the settled high value with CH2");
            Check(rig.Output.Commands.SequenceEqual(["0", "5", "stop"]), "auto calibration: WF1973 0 V, then 5 V kept from ② to ④, then stopped: " + string.Join(",", rig.Output.Commands));
            Check(rig.Locator.Finds == 1 && rig.Gauge.Disposals == 1 && rig.Gauge.ZeroAdjusts == 1, "auto calibration: GC16 found once, zero-adjusted once, released (RM,0) once");
            Check(rig.Operator.Instructions.SequenceEqual([ManualBallValveSwitcher.AtmosphereInstruction, ManualBallValveSwitcher.SupplyInstruction]),
                "auto calibration: ball valves asked atmosphere then supply, no restore notice after success");
            Check(result.Zero.WithinIdeal && result.Zero.Adjustments == 1 && result.Zero.Describe().Contains("±0.1 kPa 以内"), "auto calibration: zero check within ±0.1 kPa");
            Check(result.SinceGaugeConfirmed is { } since && since > TimeSpan.Zero && result.GaugePort == "COM7", "auto calibration: elapsed time since GC16 was confirmed and the port");
            Check(rig.Operator.AdjustedAt is { } adjusted && rig.HighSettledAt is { } settled && settled - adjusted >= TimeSpan.FromSeconds(5),
                "auto calibration: ④ waits at least 5 s of steady values after 合わせた");
            Check(rig.Operator.PressuresShown > 10, "auto calibration: GC16 values are shown while waiting");
        }

        // 2. 0 点が ±0.2 kPa を超えたら自動でゼロ調整し直す(最大 3 回)。それでも超えたら「もう一度」で続ける。±0.1〜0.2 は「許容範囲」。
        {
            var rig = new Rig();
            rig.Gauge.ZeroResiduals.Enqueue(0.3, 0.3, 0.25, 0.4, 0.15);
            rig.Operator.ZeroRetryAnswers.Enqueue(true);
            var result = await rig.Procedure().RunAsync(model, CancellationToken.None);
            Check(rig.Gauge.ZeroAdjusts == 5 && rig.Operator.ZeroRetryAsked == 1 && result.Zero.Adjustments == 5 && !result.Zero.WithinIdeal &&
                result.Zero.Describe().Contains("許容範囲") && Near(result.Calibration.Pressure.Output1, 0.15f),
                "zero check: 1 + 3 automatic re-adjustments, then もう一度, then 0.15 kPa recorded as 許容範囲");
        }
        {
            var rig = new Rig();
            rig.Gauge.ZeroResiduals.Enqueue(0.3, 0.3, 0.3, 0.3);
            rig.Operator.ZeroRetryAnswers.Enqueue(false);
            await ExpectCanceled(rig, model, "zero check: 中止 stops the automatic reading");
            Check(rig.Output.Commands.Last() == "stop" && rig.Gauge.Disposals == 1 && rig.Operator.Notices.SequenceEqual([ManualBallValveSwitcher.RestoreInstruction]),
                "zero check abort: output stopped, GC16 released, ball valves asked back to the measuring direction");
        }

        // 3. ③ で大気圧のはずが外れていたら(弁の向き違い)知らせ、そのあいだゼロ調整しない。「合わせ直す」で待ち直す。
        {
            var rig = new Rig { AtmosphereKpa = 20 };
            rig.Operator.DeviationAnswers.Enqueue(DeviationChoice.Readjust);
            rig.Operator.OnDeviation = () => rig.AtmosphereKpa = 0;
            var result = await rig.Procedure().RunAsync(model, CancellationToken.None);
            Check(rig.Operator.DeviationAsked == 1 && rig.Operator.DeviationMessages[0].Contains("③") && rig.Gauge.ZeroAdjusts == 1 &&
                rig.Gauge.ZeroAdjustedAtKpa == 0, "③ deviation over ±5 kPa: no zero adjustment until 合わせ直す and settled at atmosphere");
        }

        // 4. ④ で目標から ±10% を超えたら知らせる。「このまま使う」ならその値で記録する。
        {
            var rig = new Rig { RegulatorKpa = 450 };
            rig.Operator.DeviationAnswers.Enqueue(DeviationChoice.UseAsIs);
            var result = await rig.Procedure().RunAsync(model, CancellationToken.None);
            Check(rig.Operator.DeviationAsked == 1 && rig.Operator.DeviationMessages[0].Contains("④") && Near(result.Calibration.Pressure.Output2, 450.03),
                "④ deviation over ±10%: このまま使う records the settled value");
        }
        {
            var rig = new Rig { RegulatorKpa = 520 }; // 539 kPa の ±10%(±53.9)以内なので知らせない
            await rig.Procedure().RunAsync(model, CancellationToken.None);
            Check(rig.Operator.DeviationAsked == 0, "④ a few-kPa (here 19 kPa) offset from the target does not warn");
        }

        // 5. 落ち着かない: 15 秒を過ぎたら「弁の向きを確かめて」と知らせる。「中止」で止め、後片付けで弁を戻す知らせを出す。
        {
            var rig = new Rig { AtmosphereNoiseKpa = 1.0 };
            rig.Operator.NotSettledAnswers.Enqueue(WaitChoice.Abort);
            await ExpectCanceled(rig, model, "not settled: 中止 stops the automatic reading");
            Check(rig.Operator.NotSettledAsked == 1 && rig.Operator.NotSettledMessages[0].Contains("15 秒") &&
                rig.Operator.NotSettledMessages[0].Contains("ボールバルブ①②の向き") && rig.Gauge.ZeroAdjusts == 0 &&
                rig.Operator.Notices.SequenceEqual([ManualBallValveSwitcher.RestoreInstruction]) && rig.Output.Commands.Last() == "stop",
                "not settled: notice after 15 s, no zero adjustment, valves back and output stopped");
        }
        {
            // 「待ち続ける」で見始めからやり直す。「この段をやり直す」で弁の切り替えからやり直す。
            var rig = new Rig { AtmosphereNoiseKpa = 1.0 };
            rig.Operator.NotSettledAnswers.Enqueue(WaitChoice.KeepWaiting, WaitChoice.RedoStage);
            int asked = 0;
            rig.Operator.OnNotSettled = () => { if (++asked == 2) rig.AtmosphereNoiseKpa = 0; };
            await rig.Procedure().RunAsync(model, CancellationToken.None);
            Check(rig.Operator.NotSettledAsked == 2 && rig.Operator.Instructions.Count(i => i == ManualBallValveSwitcher.AtmosphereInstruction) == 2,
                "not settled: 待ち続ける waits again, この段をやり直す asks the ball valves again");
        }

        // 6. 通信が切れた(GC16): 出力を止めて「再開」。止まった段の最初から、GC16 を探し直す。済んだ段(①②)はやり直さない。
        {
            var rig = new Rig();
            rig.Gauge.FailReadsAfter = 3;
            rig.Operator.ResumeAnswers.Enqueue(true);
            var result = await rig.Procedure().RunAsync(model, CancellationToken.None);
            Check(rig.Operator.ResumeAsked == 1 && rig.Operator.ResumeMessages[0].Contains("③") && rig.Operator.ResumeMessages[0].Contains("WF1973 の出力を止めました"),
                "communication lost: stop output and ask 再開 for the stopped stage");
            Check(rig.Locator.Finds == 2 && rig.Operator.CurrentAsked == 1 && rig.Output.Commands.SequenceEqual(["0", "5", "stop", "5", "stop"]) &&
                rig.Operator.Instructions.Count(i => i == ManualBallValveSwitcher.AtmosphereInstruction) == 2 && result.Calibration.Pressure.Output2 > 500,
                "communication lost: 再開 searches GC16 again and restarts ③ only (① ② kept): " + string.Join(",", rig.Output.Commands));
        }
        {
            var rig = new Rig();
            rig.Meter.FailNext = true; // ① で NR-500 が読めない
            rig.Operator.ResumeAnswers.Enqueue(false);
            await ExpectCanceled(rig, model, "communication lost: 中止 stops the automatic reading");
            Check(rig.Locator.Finds == 0 && rig.Operator.Notices.Count == 0 && rig.Output.Commands.Last() == "stop",
                "communication lost before ③: no GC16 search and no valve notice");
        }
        {
            var rig = new Rig();
            rig.Locator.Fail = true; // GC16 が見つからない
            rig.Operator.ResumeAnswers.Enqueue(false);
            await ExpectCanceled(rig, model, "GC16 not found: 中止 returns to manual entry");
            Check(rig.Operator.ResumeMessages.Single().Contains("GC16 が見つかりません"), "GC16 not found: the reason is shown");
        }

        // 7. ② で中止(マルチメータの値を入れない)・画面の中止(キャンセル)でも、出力を止める。
        {
            var rig = new Rig();
            rig.Operator.CurrentAnswer = null;
            await ExpectCanceled(rig, model, "② 中止 stops the automatic reading");
            Check(rig.Output.Commands.SequenceEqual(["0", "5", "stop"]) && rig.Locator.Finds == 0, "② 中止: output stopped, GC16 not searched");
        }
        {
            var rig = new Rig();
            using var cancel = new CancellationTokenSource();
            rig.Operator.OnAdjustWait = cancel.Cancel; // ④ の「合わせた」待ちで画面の中止
            bool canceled = false;
            try { await rig.Procedure().RunAsync(model, cancel.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled && rig.Output.Commands.Last() == "stop" && rig.Gauge.Disposals == 1 && rig.Operator.Notices.Count == 0,
                "cancel during ④: output stopped, GC16 released, no valve notice (already in the measuring direction)");
        }
        {
            var rig = new Rig();
            rig.Output.FailStop = true;
            rig.Operator.CurrentAnswer = null;
            var procedure = rig.Procedure();
            try { await procedure.RunAsync(model, CancellationToken.None); } catch (OperationCanceledException) { }
            Check(procedure.OutputStopFailed, "output stop failure is reported to the caller (出力状態不明)");
        }

        // 8. 校正記録: 読み取り方・目標の供給圧力・経過時間・0 点の結果。手入力のときは後ろ 2 つを書かない。
        {
            var calibration = new As110Calibration(new(-0.002f, 0f, 0.993f, 29.9f), new(0.001f, 0f, 5.401f, 540f));
            var at = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.FromHours(9));
            string auto = new CalibrationRecord(at, calibration, "データ取り", "NR-500", "x.xdt",
                new CalibrationReading(true, model, "COM7", TimeSpan.FromSeconds(372), "+0.030 kPa（±0.1 kPa 以内、ゼロ調整 1 回）")).ToText();
            foreach (string expected in new[] { "読み取り方: GC16 で読み取り（COM7）", "目標の供給圧力: 539 kPa（AS110-035）",
                "GC16 との通信確認からゼロ調整までの経過時間: 6 分 12 秒", "0 点の確かめ: +0.030 kPa（±0.1 kPa 以内、ゼロ調整 1 回）" })
                Check(auto.Contains(expected), "record (GC16) has: " + expected);
            string edited = new CalibrationRecord(at, calibration, "データ取り", "NR-500", "x.xdt",
                new CalibrationReading(true, model, "COM7", null, "z", EditedAfterRead: true)).ToText();
            Check(edited.Contains("読み取り方: GC16 で読み取り（COM7）。そのあと手で直した") && edited.Contains("経過時間: 不明"), "record notes manual edits after the GC16 reading");
            string manual = new CalibrationRecord(at, calibration, "データ取り", "NR-500", "x.xdt", CalibrationReading.Manual(null)).ToText();
            Check(manual.Contains("読み取り方: 手入力") && manual.Contains("目標の供給圧力: 型式を選んでいない") &&
                !manual.Contains("経過時間") && !manual.Contains("0 点"), "record (manual) has the method and target only");
            string old = new CalibrationRecord(at, calibration, "データ取り", "NR-500", "x.xdt").ToText();
            Check(!old.Contains("読み取り方"), "record without reading keeps the previous content");
        }

        // 9. 校正中の印: 始めに書き、終わったら消す。残っていれば次の起動で後片付け。
        {
            string folder = Path.Combine(Path.GetTempPath(), "as110-marker-" + Guid.NewGuid().ToString("N"));
            var marker = new CalibrationInProgressMarker(Path.Combine(folder, "校正中.txt"));
            Check(!marker.Exists && marker.ReadText() is null && marker.Clear(), "marker: none at first, clearing nothing is fine");
            marker.Begin(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.FromHours(9)), "AS110-035");
            Check(marker.Exists && marker.ReadText()!.Contains("型式: AS110-035") && marker.Clear() && !marker.Exists, "marker: written at start, removed at the end");
            Directory.Delete(folder, true);
        }

        // 10. 決まりの値(実機で変える前提の既定値)。
        {
            var s = AutoCalibrationSettings.Default;
            Check(s.CommandVolts == 5 && s.StableWindow == TimeSpan.FromSeconds(5) && s.StableToleranceKpa == 0.2 && s.WaitLimit == TimeSpan.FromSeconds(15) &&
                s.ZeroSettle == TimeSpan.FromSeconds(3) && s.ZeroIdealKpa == 0.1 && s.ZeroAllowedKpa == 0.2 && s.ZeroRetries == 3 &&
                s.LowDeviationKpa == 5 && s.HighDeviationRatio == 0.10, "settings: defaults match SPEC 20〜24・27");
            bool rejected = false;
            try { (s with { CommandVolts = 6 }).Validate(); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "settings: WF1973 command above 5 V is rejected");
        }
    }

    private static async Task ExpectCanceled(Rig rig, ProductModel model, string name)
    {
        bool canceled = false;
        try { await rig.Procedure().RunAsync(model, CancellationToken.None); }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled, name);
    }

    private static bool Near(float actual, double expected) => Math.Abs(actual - expected) < 1e-3;

    // 模擬の現場: 弁の向き・レギュレーター・GC16 のずれ・時計をまとめて持つ。
    private sealed class Rig
    {
        public readonly FakeClock Clock = new();
        public readonly FakeOutput Output = new();
        public readonly FakeMeter Meter;
        public readonly FakeGauge Gauge;
        public readonly FakeLocator Locator;
        public readonly FakeOperator Operator;
        public bool Atmosphere;
        public double AtmosphereKpa;
        public double AtmosphereNoiseKpa;
        public double RegulatorKpa = 539.4;
        public DateTimeOffset? HighSettledAt;

        public Rig()
        {
            Meter = new FakeMeter(this);
            Gauge = new FakeGauge(this);
            Locator = new FakeLocator(this);
            Operator = new FakeOperator(this);
        }

        public double TruePressure => Atmosphere
            ? AtmosphereKpa + (AtmosphereNoiseKpa == 0 ? 0 : AtmosphereNoiseKpa * Math.Sin(Clock.Now.ToUnixTimeMilliseconds() / 700.0))
            : Operator.Adjusted ? RegulatorKpa : 300;

        // CH1: 30 mA 指令(5 V)で約 0.99 V。CH2: 540 kPa で約 5.4 V。
        public static double Ch1Volts(double commandVolts) => -0.002 + commandVolts * 0.199;
        public static double Ch2Volts(double kpa) => 0.001 + kpa * 0.01;

        public AutoCalibrationProcedure Procedure() =>
            new(Locator, Output, Meter, new ManualBallValveSwitcher(Operator), Operator, Clock);
    }

    private sealed class FakeClock : ICalibrationClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 11, 13, 8, 0, 0, TimeSpan.FromHours(9));
        public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Now += delay;
            return Task.CompletedTask;
        }
        public void Advance(TimeSpan by) => Now += by;
    }

    private sealed class FakeOutput : ICurrentCommandOutput
    {
        public readonly List<string> Commands = new();
        public double? Volts;
        public bool FailStop;
        public void SetVolts(double volts) { Commands.Add(volts.ToString(System.Globalization.CultureInfo.InvariantCulture)); Volts = volts; }
        public void Stop()
        {
            Commands.Add("stop");
            Volts = null;
            if (FailStop) throw new IOException("発振器の応答がありません");
        }
    }

    private sealed class FakeMeter(Rig rig) : IChannelVoltageMeter
    {
        public bool FailNext;
        public double ReadVolts(int channel)
        {
            if (FailNext) { FailNext = false; throw new InvalidDataException("LOGGER から CH1 の今の値を読めません（無効値）。"); }
            return channel == 1 ? Rig.Ch1Volts(rig.Output.Volts ?? 0) : Rig.Ch2Volts(rig.TruePressure);
        }
    }

    private sealed class FakeGauge(Rig rig) : IPressureGauge
    {
        private double _bias = 0.8; // ゼロ調整の前のずれ
        public readonly Queue<double> ZeroResiduals = new();
        public int ZeroAdjusts, Disposals, Reads;
        public int? FailReadsAfter;
        public double? ZeroAdjustedAtKpa;
        public string PortName => "COM7";

        public Task<double> ReadKilopascalsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailReadsAfter is { } limit && ++Reads > limit)
            {
                FailReadsAfter = null;
                throw new IOException("GC16 から返事がありません。");
            }
            rig.Clock.Advance(TimeSpan.FromMilliseconds(20));
            double value = Math.Round(rig.TruePressure + _bias, 2);
            if (!rig.Atmosphere && rig.Operator.Adjusted) rig.HighSettledAt = rig.Clock.Now;
            return Task.FromResult(value);
        }

        public Task ZeroAdjustAsync(CancellationToken cancellationToken)
        {
            ZeroAdjusts++;
            ZeroAdjustedAtKpa = rig.TruePressure;
            _bias = -rig.TruePressure + (ZeroResiduals.Count > 0 ? ZeroResiduals.Dequeue() : 0.03);
            return Task.CompletedTask;
        }

        public void Dispose() => Disposals++;
    }

    private sealed class FakeLocator(Rig rig) : IPressureGaugeLocator
    {
        public int Finds;
        public bool Fail;
        public DateTimeOffset? FirstConfirmedAt { get; private set; }
        public Task<IPressureGauge> FindAsync(CancellationToken cancellationToken)
        {
            if (Fail) throw new InvalidOperationException("GC16 が見つかりません。電源・変換ケーブル・本体の通信設定(S-1 が rS)を確認してください。");
            Finds++;
            FirstConfirmedAt ??= rig.Clock.Now;
            return Task.FromResult<IPressureGauge>(rig.Gauge);
        }
    }

    private sealed class FakeOperator(Rig rig) : IAutoCalibrationOperator
    {
        public readonly List<string> Instructions = new(), Notices = new(), Statuses = new();
        public readonly List<string> DeviationMessages = new(), NotSettledMessages = new(), ResumeMessages = new();
        public readonly Queue<DeviationChoice> DeviationAnswers = new();
        public readonly Queue<WaitChoice> NotSettledAnswers = new();
        public readonly Queue<bool> ZeroRetryAnswers = new(), ResumeAnswers = new();
        public double? CurrentAnswer = 29.9;
        public Action? OnDeviation, OnNotSettled, OnAdjustWait;
        public int PressuresShown, CurrentAsked, DeviationAsked, NotSettledAsked, ZeroRetryAsked, ResumeAsked;
        public bool Adjusted;
        public DateTimeOffset? AdjustedAt;

        public void ShowStatus(string text) => Statuses.Add(text);
        public void ShowPressure(double? kilopascals)
        {
            PressuresShown++;
            // ④ の「合わせた」は、GC16 の値を 3 回見せたあとで押す。
            if (_adjusting is { } adjusting && ++_adjustPolls >= 3)
            {
                _adjusting = null;
                Adjusted = true;
                AdjustedAt = rig.Clock.Now;
                adjusting.TrySetResult(true);
            }
        }

        private TaskCompletionSource<bool>? _adjusting;
        private int _adjustPolls;

        public Task<bool> ConfirmAsync(string instruction, CancellationToken cancellationToken)
        {
            Instructions.Add(instruction);
            if (instruction == ManualBallValveSwitcher.AtmosphereInstruction) rig.Atmosphere = true;
            if (instruction == ManualBallValveSwitcher.SupplyInstruction) { rig.Atmosphere = false; Adjusted = false; }
            return Task.FromResult(true);
        }

        public Task NotifyAsync(string instruction)
        {
            Notices.Add(instruction);
            rig.Atmosphere = false;
            return Task.CompletedTask;
        }

        public Task<double?> AskActualCurrentAsync(CancellationToken cancellationToken) { CurrentAsked++; return Task.FromResult(CurrentAnswer); }

        public Task<bool> WaitRegulatorAdjustedAsync(double targetKilopascals, CancellationToken cancellationToken)
        {
            OnAdjustWait?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            var adjusting = new TaskCompletionSource<bool>();
            cancellationToken.Register(() => adjusting.TrySetCanceled());
            _adjusting = adjusting;
            _adjustPolls = 0;
            return adjusting.Task;
        }

        public Task<WaitChoice> AskNotSettledAsync(string message, CancellationToken cancellationToken)
        {
            NotSettledAsked++;
            NotSettledMessages.Add(message);
            OnNotSettled?.Invoke();
            return Task.FromResult(NotSettledAnswers.Dequeue());
        }

        public Task<DeviationChoice> AskDeviationAsync(string message, CancellationToken cancellationToken)
        {
            DeviationAsked++;
            DeviationMessages.Add(message);
            OnDeviation?.Invoke();
            return Task.FromResult(DeviationAnswers.Dequeue());
        }

        public Task<bool> AskZeroRetryAsync(string message, CancellationToken cancellationToken) { ZeroRetryAsked++; return Task.FromResult(ZeroRetryAnswers.Dequeue()); }

        public Task<bool> AskResumeAsync(string message, CancellationToken cancellationToken)
        {
            ResumeAsked++;
            ResumeMessages.Add(message);
            return Task.FromResult(ResumeAnswers.Dequeue());
        }
    }
}

internal static class QueueExtensions
{
    public static void Enqueue<T>(this Queue<T> queue, params T[] items)
    {
        foreach (var item in items) queue.Enqueue(item);
    }
}
