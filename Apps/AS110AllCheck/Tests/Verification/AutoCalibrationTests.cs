using System.Text;
using PSCAutomation.Apps.AS110AllCheck;

// 校正の自動読み取り(段階 1。2026-11-18 に電流と圧力の 2 つの読み取りに分けた)を、機器なしの模擬(GC16・7351E・WF1973・NR-500・人)で確かめる。
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

        // 1. ふつうの流れ: 「マルチメータで読み取る」(①②)と「GC16 で読み取る」(③④)で 8 項目ができ、それぞれ後片付け(出力停止・7351E を離す・GC16 を RM,0)をする。
        //    弁は最後に測定の向きのままなので戻す知らせは出さない。① の電流は 0 mA 固定ではなく 7351E の値(2026-11-18)。
        {
            var rig = new Rig();
            var (c, current, pressure) = await rig.RunBothAsync(model, CancellationToken.None);
            Check(Near(c.Current.Output1, 0.01) && c.Current.Output2 == 29.9f && Near(c.Current.Input1, Rig.Ch1Volts(0)) && Near(c.Current.Input2, Rig.Ch1Volts(5)),
                "auto calibration: current points are CH1 at 0 V and at 5 V with the 7351E mA (① too)");
            Check(current.MeterName == "7351E" && current.MethodText == "マルチメータで読み取り（7351E）" && rig.CurrentMeters.Finds == 1 &&
                rig.CurrentMeters.Meter.Reads == 2 && rig.CurrentMeters.Meter.Disposals == 1 && rig.Operator.CurrentAsked == 0,
                "auto calibration: 7351E found once, read for ① and ②, released; nobody types the current");
            Check(Near(c.Pressure.Output1, 0.03) && Near(c.Pressure.Input1, Rig.Ch2Volts(0)) && Near(c.Pressure.Output2, 539.43) && Near(c.Pressure.Input2, Rig.Ch2Volts(539.4)),
                "auto calibration: pressure points are the zero-checked GC16 value and the settled high value with CH2");
            Check(rig.Output.Commands.SequenceEqual(["0", "5", "stop", "5", "stop"]),
                "auto calibration: WF1973 0 V then 5 V and stopped after the multimeter reading; 5 V from ③ to ④ then stopped: " + string.Join(",", rig.Output.Commands));
            Check(rig.Locator.Finds == 1 && rig.Gauge.Disposals == 1 && rig.Gauge.ZeroAdjusts == 1, "auto calibration: GC16 found once, zero-adjusted once, released (RM,0) once");
            Check(rig.Operator.Instructions.SequenceEqual([ManualBallValveSwitcher.AtmosphereInstruction, ManualBallValveSwitcher.SupplyInstruction]),
                "auto calibration: ball valves asked atmosphere then supply, no restore notice after success");
            Check(pressure.Zero.WithinIdeal && pressure.Zero.Adjustments == 1 && pressure.Zero.Describe().Contains("±0.1 kPa 以内"), "auto calibration: zero check within ±0.1 kPa");
            Check(pressure.SinceGaugeConfirmed is { } since && since > TimeSpan.Zero && pressure.GaugePort == "COM7", "auto calibration: elapsed time since GC16 was confirmed and the port");
            Check(rig.Operator.AdjustedAt is { } adjusted && rig.HighSettledAt is { } settled && settled - adjusted >= TimeSpan.FromSeconds(5),
                "auto calibration: ④ waits at least 5 s of steady values after 合わせた");
            Check(rig.Operator.PressuresShown > 10, "auto calibration: GC16 values are shown while waiting");
            Check(rig.Meter.Prepares == 4 && rig.Meter.Reads == 4, "auto calibration: the LOGGER is prepared before each stage and read once per stage");
        }
        {
            // 片方だけ使える(押す順番も問わない): 「GC16 で読み取る」だけでも圧力の 2 点が返り、7351E は探さない。
            var rig = new Rig();
            var pressure = await rig.Procedure().RunPressureAsync(model, CancellationToken.None);
            Check(Near(pressure.Pressure.Output2, 539.43) && rig.CurrentMeters.Finds == 0 && rig.Output.Commands.SequenceEqual(["5", "stop"]),
                "GC16 only: pressure points without the multimeter; WF1973 5 V then stopped");
        }

        // 2. 0 点が ±0.2 kPa を超えたら自動でゼロ調整し直す(最大 3 回)。それでも超えたら「もう一度」で続ける。±0.1〜0.2 は「許容範囲」。
        {
            var rig = new Rig();
            rig.Gauge.ZeroResiduals.Enqueue(0.3, 0.3, 0.25, 0.4, 0.15);
            rig.Operator.ZeroRetryAnswers.Enqueue(true);
            var (c, _, pressure) = await rig.RunBothAsync(model, CancellationToken.None);
            Check(rig.Gauge.ZeroAdjusts == 5 && rig.Operator.ZeroRetryAsked == 1 && pressure.Zero.Adjustments == 5 && !pressure.Zero.WithinIdeal &&
                pressure.Zero.Describe().Contains("許容範囲") && Near(c.Pressure.Output1, 0.15f),
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
            await rig.RunBothAsync(model, CancellationToken.None);
            Check(rig.Operator.DeviationAsked == 1 && rig.Operator.DeviationMessages[0].Contains("③") && rig.Operator.DeviationLabels[0] == "合わせ直す" &&
                rig.Gauge.ZeroAdjusts == 1 && rig.Gauge.ZeroAdjustedAtKpa == 0, "③ deviation over ±5 kPa: no zero adjustment until 合わせ直す and settled at atmosphere");
        }

        // 4. ④ で目標から ±10% を超えたら知らせる。「このまま使う」ならその値で記録する。
        {
            var rig = new Rig { RegulatorKpa = 450 };
            rig.Operator.DeviationAnswers.Enqueue(DeviationChoice.UseAsIs);
            var (c, _, _) = await rig.RunBothAsync(model, CancellationToken.None);
            Check(rig.Operator.DeviationAsked == 1 && rig.Operator.DeviationMessages[0].Contains("④") && Near(c.Pressure.Output2, 450.03),
                "④ deviation over ±10%: このまま使う records the settled value");
        }
        {
            var rig = new Rig { RegulatorKpa = 520 }; // 539 kPa の ±10%(±53.9)以内なので知らせない
            await rig.RunBothAsync(model, CancellationToken.None);
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
            await rig.RunBothAsync(model, CancellationToken.None);
            Check(rig.Operator.NotSettledAsked == 2 && rig.Operator.Instructions.Count(i => i == ManualBallValveSwitcher.AtmosphereInstruction) == 2,
                "not settled: 待ち続ける waits again, この段をやり直す asks the ball valves again");
        }

        // 6. 通信が切れた(GC16): 出力を止めて「再開」。止まった段の最初から、GC16 を探し直す。済んだ段はやり直さない。
        {
            var rig = new Rig();
            rig.Gauge.FailReadsAfter = 3;
            rig.Operator.ResumeAnswers.Enqueue(true);
            var (c, _, _) = await rig.RunBothAsync(model, CancellationToken.None);
            Check(rig.Operator.ResumeAsked == 1 && rig.Operator.ResumeMessages[0].Contains("③") && rig.Operator.ResumeMessages[0].Contains("WF1973 の出力を止めました"),
                "communication lost: stop output and ask 再開 for the stopped stage");
            Check(rig.Locator.Finds == 2 && rig.CurrentMeters.Meter.Reads == 2 && rig.Output.Commands.SequenceEqual(["0", "5", "stop", "5", "stop", "5", "stop"]) &&
                rig.Operator.Instructions.Count(i => i == ManualBallValveSwitcher.AtmosphereInstruction) == 2 && c.Pressure.Output2 > 500,
                "communication lost: 再開 searches GC16 again and restarts ③ only: " + string.Join(",", rig.Output.Commands));
        }
        {
            var rig = new Rig();
            rig.Meter.FailNext = true; // ① で NR-500 が読めない
            rig.Operator.ResumeAnswers.Enqueue(false);
            await ExpectCanceled(rig, model, "communication lost: 中止 stops the automatic reading");
            Check(rig.Locator.Finds == 0 && rig.Operator.Notices.Count == 0 && rig.Output.Commands.Last() == "stop" && rig.CurrentMeters.Meter.Disposals == 1,
                "communication lost in ①: no GC16 search, no valve notice, the 7351E is released");
        }
        {
            var rig = new Rig();
            rig.Locator.Fail = true; // GC16 が見つからない
            rig.Operator.ResumeAnswers.Enqueue(false);
            await ExpectCanceled(rig, model, "GC16 not found: 中止 returns to manual entry");
            Check(rig.Operator.ResumeMessages.Single().Contains("GC16 が見つかりません"), "GC16 not found: the reason is shown");
        }
        {
            // LOGGER が測定を始められない(校正用の雛形が無い・NR-500 が答えない): 段の最初で止まり、「再開」で同じ段から。
            var rig = new Rig();
            rig.Meter.FailPrepare = true;
            rig.Operator.ResumeAnswers.Enqueue(true);
            var (c, _, _) = await rig.RunBothAsync(model, CancellationToken.None);
            Check(rig.Operator.ResumeMessages.Single().Contains("①") && rig.Operator.ResumeMessages[0].Contains("記録を始めません") &&
                rig.Output.Commands.Take(3).SequenceEqual(["stop", "0", "5"]) && Near(c.Current.Input1, Rig.Ch1Volts(0)),
                "LOGGER not recording: stopped at the start of ①, 再開 reads ① again: " + string.Join(",", rig.Output.Commands));
        }

        // 6b. 30 mA が出ていない(アンプの電源忘れ・出力が止まった): ② の電圧が ① から 0.3 V 上がらなければ止める。
        //     それまでは「低点以下」だけを見ていて、ノイズで少し上なら 0 mA のままの電圧を高点にした(2026-11-14、全体見直し・区切り 4)。
        {
            var rig = new Rig { AmplifierOff = true };
            rig.Operator.ResumeAnswers.Enqueue(true);
            rig.Operator.OnResume = () => rig.AmplifierOff = false;
            var (c, _, _) = await rig.RunBothAsync(model, CancellationToken.None);
            Check(rig.Operator.ResumeAsked == 1 && rig.Operator.ResumeMessages[0].Contains("②") && rig.Operator.ResumeMessages[0].Contains("上がっていません") &&
                rig.Operator.ResumeMessages[0].Contains("アンプの電源"), "② no current: stopped although the voltage is slightly above the low point");
            Check(rig.CurrentMeters.Meter.Reads == 3 && rig.CurrentMeters.Finds == 2 && Near(c.Current.Input2, Rig.Ch1Volts(5)),
                "② no current: 再開 finds the 7351E again and reads ② again (① kept)");
        }

        // 6c. 圧力の電圧が上がらない(CH2 の配線): ④ の中で止め、「再開」で ④ だけやり直す(③ は残す)。
        //     それまでは ④ のあとの最後の確かめで止まり、手入力用の文が出て、全部やり直しになった(2026-11-14、全体見直し・区切り 4)。
        {
            var rig = new Rig { Ch2Stuck = true };
            rig.Operator.ResumeAnswers.Enqueue(true);
            rig.Operator.OnResume = () => rig.Ch2Stuck = false;
            var (c, _, _) = await rig.RunBothAsync(model, CancellationToken.None);
            Check(rig.Operator.ResumeAsked == 1 && rig.Operator.ResumeMessages[0].Contains("④") && rig.Operator.ResumeMessages[0].Contains("CH2（圧力）の配線"),
                "④ pressure voltage not above the low point: stopped inside ④ with the wiring hint");
            Check(rig.CurrentMeters.Meter.Reads == 2 && rig.Gauge.ZeroAdjusts == 1 &&
                rig.Operator.Instructions.Count(i => i == ManualBallValveSwitcher.AtmosphereInstruction) == 1 &&
                rig.Operator.Instructions.Count(i => i == ManualBallValveSwitcher.SupplyInstruction) == 2 &&
                Near(c.Pressure.Input2, Rig.Ch2Volts(539.4)), "④ pressure voltage: 再開 restarts ④ only (③ kept)");
        }
        {
            // 低点以下の圧力は高点にしない。
            var rig = new Rig { RegulatorKpa = -1 };
            rig.Operator.ResumeAnswers.Enqueue(false);
            await ExpectCanceled(rig, model, "④ pressure not above the low point is not recorded");
            Check(rig.Operator.ResumeMessages.Single().Contains("以下です"), "④ pressure not above the low point: the reason is shown");
        }

        // 6d. 7351E(2026-11-18): 見つからなければ「もう一度探す / 電流を手で入れる / 中止」。手で入れるときも電圧は自動で読む。
        {
            var rig = new Rig();
            rig.CurrentMeters.FailFinds = 1;
            rig.Operator.MeterMissingAnswers.Enqueue(CurrentMeterMissingChoice.Retry);
            var current = await rig.Procedure().RunCurrentAsync(CancellationToken.None);
            Check(rig.Operator.MeterMissingAsked == 1 && rig.Operator.MeterMissingMessages[0].Contains("7351E") && rig.CurrentMeters.Finds == 1 &&
                current.MeterName == "7351E", "7351E not found: もう一度探す finds it and reads with it");
        }
        {
            var rig = new Rig();
            rig.CurrentMeters.FailFinds = 99;
            rig.Operator.MeterMissingAnswers.Enqueue(CurrentMeterMissingChoice.Manual);
            rig.Operator.CurrentAnswers.Enqueue(-0.02, 29.8);
            var current = await rig.Procedure().RunCurrentAsync(CancellationToken.None);
            Check(rig.Operator.MeterMissingAsked == 1 && rig.Operator.CurrentStages.SequenceEqual([AutoCalibrationStage.CurrentLow, AutoCalibrationStage.CurrentHigh]) &&
                Near(current.Current.Output1, -0.02) && Near(current.Current.Output2, 29.8) && Near(current.Current.Input2, Rig.Ch1Volts(5)) &&
                current.MeterName is null && current.MethodText == "電圧を読み取り、電流は手で入れた" && rig.Meter.Reads == 2,
                "7351E not found: 電流を手で入れる asks ① and ② (① may be slightly negative) and still reads the voltages");
        }
        {
            var rig = new Rig();
            rig.CurrentMeters.FailFinds = 99;
            rig.Operator.MeterMissingAnswers.Enqueue(CurrentMeterMissingChoice.Manual);
            rig.Operator.CurrentAnswers.Enqueue(0.01, null);
            bool canceled = false;
            try { await rig.Procedure().RunCurrentAsync(CancellationToken.None); } catch (OperationCanceledException) { canceled = true; }
            Check(canceled && rig.Output.Commands.SequenceEqual(["0", "5", "stop"]) && rig.Locator.Finds == 0, "manual current: 中止 at ② stops the output");
        }
        {
            var rig = new Rig();
            rig.CurrentMeters.FailFinds = 99;
            rig.Operator.MeterMissingAnswers.Enqueue(CurrentMeterMissingChoice.Abort);
            bool canceled = false;
            try { await rig.Procedure().RunCurrentAsync(CancellationToken.None); } catch (OperationCanceledException) { canceled = true; }
            Check(canceled && rig.Output.Commands.SequenceEqual(["0", "stop"]), "7351E not found: 中止 stops the reading and the output");
        }

        // 6e. 電流の外れ(2026-11-18): ① は 0 mA から ±1 mA、② は 25 mA 未満で知らせる。「やり直す」はその段の最初から。
        {
            var rig = new Rig();
            rig.CurrentMeters.Meter.LowOffsetMilliamps = 1.5;
            rig.Operator.DeviationAnswers.Enqueue(DeviationChoice.Readjust);
            rig.Operator.OnDeviation = () => rig.CurrentMeters.Meter.LowOffsetMilliamps = 0;
            var current = await rig.Procedure().RunCurrentAsync(CancellationToken.None);
            Check(rig.Operator.DeviationAsked == 1 && rig.Operator.DeviationMessages[0].Contains("①") && rig.Operator.DeviationLabels[0] == "やり直す" &&
                rig.CurrentMeters.Meter.Reads == 3 && Near(current.Current.Output1, 0.01), "① current over ±1 mA: やり直す reads ① again");
        }
        {
            var rig = new Rig();
            rig.CurrentMeters.Meter.HighMilliamps = 22;
            rig.Operator.DeviationAnswers.Enqueue(DeviationChoice.UseAsIs);
            var current = await rig.Procedure().RunCurrentAsync(CancellationToken.None);
            Check(rig.Operator.DeviationAsked == 1 && rig.Operator.DeviationMessages[0].Contains("②") && rig.Operator.DeviationMessages[0].Contains("25 mA 未満") &&
                current.Current.Output2 == 22f, "② current under 25 mA: このまま使う records it");
        }

        // 7. 画面の中止(キャンセル)・出力停止の失敗。
        {
            var rig = new Rig();
            using var cancel = new CancellationTokenSource();
            rig.Operator.OnAdjustWait = cancel.Cancel; // ④ の「合わせた」待ちで画面の中止
            bool canceled = false;
            try { await rig.Procedure().RunPressureAsync(model, cancel.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled && rig.Output.Commands.Last() == "stop" && rig.Gauge.Disposals == 1 && rig.Operator.Notices.Count == 0,
                "cancel during ④: output stopped, GC16 released, no valve notice (already in the measuring direction)");
        }
        {
            var rig = new Rig();
            rig.Output.FailStop = true;
            rig.CurrentMeters.FailFinds = 99;
            rig.Operator.MeterMissingAnswers.Enqueue(CurrentMeterMissingChoice.Abort);
            var procedure = rig.Procedure();
            try { await procedure.RunCurrentAsync(CancellationToken.None); } catch (OperationCanceledException) { }
            Check(procedure.OutputStopFailed, "output stop failure is reported to the caller (出力状態不明)");
        }

        // 8. 校正記録: 読み取り方・目標の供給圧力・経過時間・0 点の結果。手入力のときは後ろ 2 つを書かない。
        {
            var calibration = new As110Calibration(new(-0.002f, 0f, 0.993f, 29.9f), new(0.001f, 0f, 5.401f, 540f));
            var at = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.FromHours(9));
            string auto = new CalibrationRecord(at, calibration, "データ取り", "NR-500", "x.xdt",
                new CalibrationReading(true, model, "COM7", TimeSpan.FromSeconds(372), "+0.030 kPa（±0.1 kPa 以内、ゼロ調整 1 回）",
                    CurrentMethod: "マルチメータで読み取り（7351E）")).ToText();
            foreach (string expected in new[] { "読み取り方: 電流 マルチメータで読み取り（7351E） ／ 圧力 GC16 で読み取り（COM7）", "目標の供給圧力: 539 kPa（AS110-035）",
                "GC16 との通信確認からゼロ調整までの経過時間: 6 分 12 秒", "0 点の確かめ: +0.030 kPa（±0.1 kPa 以内、ゼロ調整 1 回）" })
                Check(auto.Contains(expected), "record (multimeter + GC16) has: " + expected);
            string edited = new CalibrationRecord(at, calibration, "データ取り", "NR-500", "x.xdt",
                new CalibrationReading(true, model, "COM7", null, "z", EditedAfterRead: true)).ToText();
            Check(edited.Contains("読み取り方: 電流 手入力 ／ 圧力 GC16 で読み取り（COM7）。そのあと手で直した") && edited.Contains("経過時間: 不明"),
                "record notes manual edits after the GC16 reading and a hand-typed current");
            string currentOnly = new CalibrationRecord(at, calibration, "データ取り", "NR-500", "x.xdt",
                new CalibrationReading(false, null, CurrentMethod: "電圧を読み取り、電流は手で入れた", CurrentEditedAfterRead: true)).ToText();
            Check(currentOnly.Contains("読み取り方: 電流 電圧を読み取り、電流は手で入れた。そのあと手で直した ／ 圧力 手入力") && !currentOnly.Contains("0 点"),
                "record with the current reading only has no GC16 lines");
            string manual = new CalibrationRecord(at, calibration, "データ取り", "NR-500", "x.xdt", CalibrationReading.Manual(null)).ToText();
            Check(manual.Contains("読み取り方: 手入力") && manual.Contains("目標の供給圧力: 型式を選んでいない") &&
                !manual.Contains("経過時間") && !manual.Contains("0 点"), "record (manual) has the method and target only");
            string old = new CalibrationRecord(at, calibration, "データ取り", "NR-500", "x.xdt").ToText();
            Check(!old.Contains("読み取り方"), "record without reading keeps the previous content");
            string fromToday = new CalibrationRecord(at, calibration, "データ取り", "NR-500", "x.xdt", ReadingLines: ["校正した時刻: 2026/11/13 08:00:00 (+09:00)", "読み取り方: 手入力"]).ToText();
            Check(fromToday.Contains("校正した時刻: 2026/11/13 08:00:00 (+09:00)") && fromToday.Contains("読み取り方: 手入力"),
                "record from today's calibration copies its lines");
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
                s.LowDeviationKpa == 5 && s.HighDeviationRatio == 0.10 && s.MinCurrentRiseVolts == 0.3, "settings: defaults match SPEC 20〜24・27");
            Check(s.VoltageAverageWindow == TimeSpan.FromSeconds(1) && s.LowCurrentToleranceMilliamps == 1 && s.MinHighCurrentMilliamps == 25,
                "settings: 1 s voltage average and the current deviation limits (2026-11-18)");
            bool rejected = false;
            try { (s with { CommandVolts = 6 }).Validate(); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "settings: WF1973 command above 5 V is rejected");
        }

        await CalibrationLoggerReaderTests.RunAsync();
        TodayCalibrationTests.Run();
    }

    private static async Task ExpectCanceled(Rig rig, ProductModel model, string name)
    {
        bool canceled = false;
        try { await rig.RunBothAsync(model, CancellationToken.None); }
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
        public readonly FakeCurrentMeterLocator CurrentMeters;
        public readonly FakeOperator Operator;
        public bool Atmosphere;
        public double AtmosphereKpa;
        public double AtmosphereNoiseKpa;
        public double RegulatorKpa = 539.4;
        public DateTimeOffset? HighSettledAt;
        // アンプの電源が入っていない(指令を出しても CH1 が上がらない。ノイズで少しだけ上)。
        public bool AmplifierOff;
        // CH2 が圧力に付いてこない(配線外れ)。
        public bool Ch2Stuck;

        public Rig()
        {
            Meter = new FakeMeter(this);
            Gauge = new FakeGauge(this);
            Locator = new FakeLocator(this);
            CurrentMeters = new FakeCurrentMeterLocator(this);
            Operator = new FakeOperator(this);
        }

        public double TruePressure => Atmosphere
            ? AtmosphereKpa + (AtmosphereNoiseKpa == 0 ? 0 : AtmosphereNoiseKpa * Math.Sin(Clock.Now.ToUnixTimeMilliseconds() / 700.0))
            : Operator.Adjusted ? RegulatorKpa : 300;

        // CH1: 30 mA 指令(5 V)で約 0.99 V。CH2: 540 kPa で約 5.4 V。
        public static double Ch1Volts(double commandVolts) => -0.002 + commandVolts * 0.199;
        public static double Ch2Volts(double kpa) => 0.001 + kpa * 0.01;

        public AutoCalibrationProcedure Procedure() =>
            new(Locator, Output, CurrentMeters, Meter, new ManualBallValveSwitcher(Operator), Operator, Clock);

        // 校正画面で「マルチメータで読み取る」→「GC16 で読み取る」の順に押したときと同じ(読み取りの画面は 1 回ずつ別)。
        public async Task<(As110Calibration Calibration, CurrentReadingResult Current, PressureReadingResult Pressure)> RunBothAsync(
            ProductModel model, CancellationToken cancellationToken)
        {
            var current = await Procedure().RunCurrentAsync(cancellationToken);
            var pressure = await Procedure().RunPressureAsync(model, cancellationToken);
            var calibration = new As110Calibration(current.Current, pressure.Pressure);
            calibration.Validate();
            return (calibration, current, pressure);
        }
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

    // NR-500 の電圧(LOGGER の 1 秒平均)の模擬。読むたびに 1.3 秒進む。
    private sealed class FakeMeter(Rig rig) : IChannelVoltageMeter
    {
        public bool FailNext, FailPrepare;
        public int Prepares, Reads;

        public Task PrepareAsync(CancellationToken cancellationToken)
        {
            Prepares++;
            if (FailPrepare) { FailPrepare = false; throw new InvalidOperationException("LOGGER が 5 秒待っても記録を始めません（状態 1）。"); }
            return Task.CompletedTask;
        }

        public Task<double> ReadVoltsAsync(int channel, CancellationToken cancellationToken)
        {
            Reads++;
            rig.Clock.Advance(TimeSpan.FromSeconds(1.3));
            if (FailNext) { FailNext = false; throw new InvalidDataException("LOGGER から CH1 の電圧を読めません（無効値）。"); }
            if (channel == 1) return Task.FromResult(rig.AmplifierOff ? Rig.Ch1Volts(0) + (rig.Output.Volts > 0 ? 0.001 : 0) : Rig.Ch1Volts(rig.Output.Volts ?? 0));
            return Task.FromResult(rig.Ch2Stuck ? Rig.Ch2Volts(0) : Rig.Ch2Volts(rig.TruePressure));
        }
    }

    // 7351E の模擬: 0 V の指令で 0.01 mA、5 V で 29.9 mA(アンプの電源が無ければ 0)。
    private sealed class FakeCurrentMeter(Rig rig) : ICurrentMeter
    {
        public int Reads, Disposals;
        public double LowOffsetMilliamps;
        public double HighMilliamps = 29.9;
        public string Name => "7351E";

        public Task<double> ReadMilliampsAsync(CancellationToken cancellationToken)
        {
            Reads++;
            double volts = rig.Output.Volts ?? 0;
            double milliamps = rig.AmplifierOff ? 0 : volts > 0 ? HighMilliamps : 0.01 + LowOffsetMilliamps;
            return Task.FromResult(milliamps);
        }

        public void Dispose() => Disposals++;
    }

    private sealed class FakeCurrentMeterLocator(Rig rig) : ICurrentMeterLocator
    {
        public readonly FakeCurrentMeter Meter = new(rig);
        public int Finds;
        // この回数だけ見つからない。
        public int FailFinds;

        public Task<ICurrentMeter> FindAsync(CancellationToken cancellationToken)
        {
            if (FailFinds > 0)
            {
                FailFinds--;
                throw new InvalidOperationException("7351E を確認できません。電源、USB、VISA を確認してください。");
            }
            Finds++;
            return Task.FromResult<ICurrentMeter>(Meter);
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
        public readonly List<string> DeviationMessages = new(), DeviationLabels = new(), NotSettledMessages = new(), ResumeMessages = new(), MeterMissingMessages = new();
        public readonly List<AutoCalibrationStage> CurrentStages = new();
        public readonly Queue<DeviationChoice> DeviationAnswers = new();
        public readonly Queue<WaitChoice> NotSettledAnswers = new();
        public readonly Queue<CurrentMeterMissingChoice> MeterMissingAnswers = new();
        public readonly Queue<bool> ZeroRetryAnswers = new(), ResumeAnswers = new();
        public readonly Queue<double?> CurrentAnswers = new();
        public Action? OnDeviation, OnNotSettled, OnAdjustWait, OnResume;
        public int PressuresShown, CurrentAsked, DeviationAsked, NotSettledAsked, ZeroRetryAsked, ResumeAsked, MeterMissingAsked;
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

        public Task<double?> AskActualCurrentAsync(AutoCalibrationStage stage, CancellationToken cancellationToken)
        {
            CurrentAsked++;
            CurrentStages.Add(stage);
            return Task.FromResult(CurrentAnswers.Dequeue());
        }

        public Task<CurrentMeterMissingChoice> AskCurrentMeterMissingAsync(string message, CancellationToken cancellationToken)
        {
            MeterMissingAsked++;
            MeterMissingMessages.Add(message);
            return Task.FromResult(MeterMissingAnswers.Dequeue());
        }

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

        public Task<DeviationChoice> AskDeviationAsync(string message, string readjustLabel, CancellationToken cancellationToken)
        {
            DeviationAsked++;
            DeviationMessages.Add(message);
            DeviationLabels.Add(readjustLabel);
            OnDeviation?.Invoke();
            return Task.FromResult(DeviationAnswers.Dequeue());
        }

        public Task<bool> AskZeroRetryAsync(string message, CancellationToken cancellationToken) { ZeroRetryAsked++; return Task.FromResult(ZeroRetryAnswers.Dequeue()); }

        public Task<bool> AskResumeAsync(string message, CancellationToken cancellationToken)
        {
            ResumeAsked++;
            ResumeMessages.Add(message);
            OnResume?.Invoke();
            return Task.FromResult(ResumeAnswers.Dequeue());
        }
    }
}

// 校正用の雛形での LOGGER の測定と、電圧の 1 秒平均(2026-11-18)を、模擬の LOGGER で確かめる。
internal static class CalibrationLoggerReaderTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    public static async Task RunAsync()
    {
        string folder = Path.Combine(Path.GetTempPath(), "as110-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string template = Path.Combine(folder, CalibrationLoggerReader.TemplateFileName);
        File.WriteAllBytes(template, [0]);
        try
        {
            // 1. 初めて読むとき: 開いているファイルを閉じ、校正用の雛形を開いて Start。点数が増え始めるのを待つ。
            //    読むと決めたあとに記録された点だけを平均する(決める前の点は混ぜない)。
            {
                var (logger, clock, log, reader) = Make(template);
                logger.ActiveFile = "作業用.xdt";
                logger.Level = 1.0;
                await reader.PrepareAsync(CancellationToken.None);
                Check(logger.Closes == 1 && logger.ActiveFile == template && logger.Starts == 1 && reader.Opened,
                    "reader: closes the working file, opens the calibration template and starts the LOGGER");
                Check(log.Any(l => l.Contains("Start から記録が増え始めるまで")), "reader: the time from Start to the first points is logged");
                logger.Level = 2.0; // 読むと決めた時点からあと
                double volts = await reader.ReadVoltsAsync(1, CancellationToken.None);
                Check(Math.Abs(volts - 2.0) < 1e-9 && logger.LastReadCount >= 100 && log.Any(l => l.Contains("点の平均")),
                    $"reader: averages only the points recorded after the decision ({volts} V, {logger.LastReadCount} points)");
                Check(logger.Starts == 1, "reader: does not restart while recording");
                reader.Close();
                Check(logger.Stops == 1 && logger.ActiveFile is null && !reader.Opened, "reader: Close stops the LOGGER and closes the template");
                reader.Close();
                Check(logger.Stops == 1, "reader: Close twice is harmless");
            }
            // 2. 記録の長さを使い切って止まっていたら Start し直す。
            {
                var (logger, _, _, reader) = Make(template);
                await reader.PrepareAsync(CancellationToken.None);
                logger.StopAfterPoints = logger.Count + 2; // すぐ止まる(平均に足りない)
                double volts = await reader.ReadVoltsAsync(2, CancellationToken.None);
                Check(logger.Starts == 2 && Math.Abs(volts - logger.Level) < 1e-9, "reader: restarts when the recording has ended");
            }
            // 3. 無効値(3.4×10^38)は電圧にしない。
            {
                var (logger, _, _, reader) = Make(template);
                logger.Invalid = true;
                bool failed = false;
                try { await reader.ReadVoltsAsync(1, CancellationToken.None); }
                catch (InvalidDataException ex) { failed = ex.Message.Contains("無効値"); }
                Check(failed, "reader: invalid values are rejected");
            }
            // 4. Start しても記録が増えない(トリガー待ちのまま): 5 秒で諦める。
            {
                var (logger, _, _, reader) = Make(template);
                logger.NeverRecords = true;
                bool failed = false;
                try { await reader.PrepareAsync(CancellationToken.None); }
                catch (InvalidOperationException ex) { failed = ex.Message.Contains("記録を始めません"); }
                Check(failed, "reader: gives up when the LOGGER never records");
            }
            // 6. リポジトリの校正用の雛形(AI が作った。データ調整の雛形の周期を 10 ms、CH1・CH2 の換算を「1 V = 1」にしたもの)。
            {
                string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", CalibrationLoggerReader.TemplateFileName);
                var info = PSCAutomation.Wrappers.LoggerFiles.XdtFile.Load(fixture).Info;
                var editor = new PSCAutomation.Wrappers.LoggerFiles.XdtScalingEditor(fixture);
                var identity = new PSCAutomation.Wrappers.LoggerFiles.ScalingPoints(0, 0, 1, 1);
                // サンプリング数の設定(旧 LOGGER 形式の 524 バイト目)は 65000(約 10 分 50 秒)。2026-11-18 に人が WAVE LOGGER で 65000 と出ることを確かめた。
                int samplingCount = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(fixture).AsSpan(524, 4));
                Check(info.Timing.SampleInterval == TimeSpan.FromMilliseconds(10) && editor.GetScaling(1) == identity && editor.GetScaling(2) == identity &&
                    samplingCount == 65000, "calibration template: 10 ms, 65000 samples and the voltage as it is on CH1 and CH2");
            }
            // 5. 校正用の雛形が無い。
            {
                var (_, _, _, reader) = Make(Path.Combine(folder, "無い.xdt"));
                bool failed = false;
                try { await reader.PrepareAsync(CancellationToken.None); }
                catch (FileNotFoundException ex) { failed = ex.Message.Contains("校正用の LOGGER 雛形"); }
                Check(failed && !reader.Opened, "reader: a missing calibration template is reported before touching the LOGGER");
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static (FakeLoggerSource Logger, Clock Clock, List<string> Log, CalibrationLoggerReader Reader) Make(string template)
    {
        var clock = new Clock();
        var logger = new FakeLoggerSource(clock);
        var log = new List<string>();
        return (logger, clock, log, new CalibrationLoggerReader(logger, template, clock, log.Add));
    }

    private sealed class Clock : ICalibrationClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 11, 18, 9, 0, 0, TimeSpan.FromHours(9));
        public Task Delay(TimeSpan delay, CancellationToken cancellationToken) { Now += delay; return Task.CompletedTask; }
    }

    // 模擬の LOGGER: Start から 0.2 秒後に記録が始まり、10 ms ごとに 1 点(値は Level)。PC に届く遅れは無し。
    private sealed class FakeLoggerSource(Clock clock) : ICalibrationLoggerSource
    {
        private readonly List<float> _points = new();
        private DateTimeOffset? _startedAt;
        public string? ActiveFile;
        public double Level = 1.5;
        public bool Invalid, NeverRecords;
        public long? StopAfterPoints;
        public int Starts, Stops, Closes, LastReadCount;

        public long Count { get { Advance(); return _points.Count; } }

        private void Advance()
        {
            if (_startedAt is not { } started || NeverRecords) return;
            long due = Math.Max(0, (long)((clock.Now - started - TimeSpan.FromSeconds(0.2)).TotalMilliseconds / 10));
            if (StopAfterPoints is { } limit) due = Math.Min(due, limit);
            while (_points.Count < due) _points.Add(Invalid ? 3.4e38f : (float)Level);
            if (StopAfterPoints is { } stopAt && _points.Count >= stopAt) { _startedAt = null; StopAfterPoints = null; }
        }

        public bool HasActiveFile() => ActiveFile is not null;
        public long OpenFile(string path) { ActiveFile = path; return 0; }
        public long CloseActiveFile() { Closes++; ActiveFile = null; return 0; }
        public long GetState() { Advance(); return _startedAt is null ? 1 : NeverRecords ? 3 : 4; }
        public long Start() { Starts++; _points.Clear(); _startedAt = clock.Now; return 0; }
        public void Stop() { Stops++; Advance(); _startedAt = null; }
        public long GetDataCount() => Count;
        public float[] ReadRaw(int channelIndex, long start, int count)
        {
            Advance();
            LastReadCount = count;
            return _points.Skip((int)start).Take(count).ToArray();
        }
    }
}

// 今日の校正値のファイル(2026-11-18、1 日 1 回の流れ)。
internal static class TodayCalibrationTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    public static void Run()
    {
        string folder = Path.Combine(Path.GetTempPath(), "as110-today-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TodayCalibrationStore(Path.Combine(folder, TodayCalibrationStore.FolderName));
            var day = new DateTime(2026, 11, 18);
            var at = new DateTimeOffset(2026, 11, 18, 8, 15, 30, TimeSpan.FromHours(9));
            var calibration = new As110Calibration(new(-0.002f, 0.01f, 0.993f, 29.9f), new(0.001f, 0.03f, 5.401f, 539.43f));
            var model = ProductModel.All.Single(m => m.Name == "AS110-035");
            var reading = new CalibrationReading(true, model, "COM7", TimeSpan.FromSeconds(372), "+0.030 kPa（±0.1 kPa 以内、ゼロ調整 1 回）",
                CurrentMethod: "マルチメータで読み取り（7351E）");
            Check(store.Read(day) is null, "today: no file means no calibration yet");
            var today = TodayCalibration.Create(at, calibration, "NR-500", reading);
            string path = store.Write(today);
            Check(path.EndsWith(Path.Combine(TodayCalibrationStore.FolderName, "校正値_2026-11-18.txt")) && File.Exists(path) && !File.Exists(path + ".tmp"),
                "today: written to 校正値\\校正値_yyyy-MM-dd.txt next to the exe");
            string text = File.ReadAllText(path);
            Check(text.Contains("校正した時刻: 2026/11/18 08:15:30 (+09:00)") && text.Contains("CH1 電流: 低点 -0.002 V = 0.01 mA、高点 0.993 V = 29.9 mA") &&
                text.Contains("読み取り方: 電流 マルチメータで読み取り（7351E） ／ 圧力 GC16 で読み取り（COM7）"), "today: the file is readable Japanese text");
            var back = store.Read(day)!;
            Check(back.Calibration == calibration && back.CalibratedAt == at && back.Hardware == "NR-500" && back.ReadingLines.SequenceEqual(today.ReadingLines),
                "today: the eight values, the time and the reading lines are read back as written");
            Check(back.IsFor(new DateTime(2026, 11, 18, 23, 59, 0)) && !back.IsFor(new DateTime(2026, 11, 19, 0, 0, 0)), "today: valid until midnight");
            Check(back.RecordLines[0] == "校正した時刻: 2026/11/18 08:15:30 (+09:00)" && back.RecordLines.Skip(1).SequenceEqual(back.ReadingLines),
                "today: the record lines start with the calibration time");
            // やり直したら同じ日のファイルを上書きする。前の日のファイルは消さない。
            File.WriteAllText(store.PathFor(day.AddDays(-1)), "前の日");
            store.Write(today with { CalibratedAt = at.AddHours(1) });
            Check(store.Read(day)!.CalibratedAt == at.AddHours(1) && File.Exists(store.PathFor(day.AddDays(-1))) &&
                Directory.GetFiles(store.Folder).Length == 2, "today: re-calibration overwrites the same day's file and keeps the previous days");
            // 壊れた・日付の違うファイルは読めないとして扱う(古い値では進めない)。
            File.WriteAllText(store.PathFor(day), text.Replace("29.9 mA", "abc mA"));
            bool broken = false;
            try { store.Read(day); } catch (InvalidDataException) { broken = true; }
            File.WriteAllText(store.PathFor(day), text.Replace("2026/11/18 08:15:30", "2026/11/17 08:15:30"));
            bool otherDay = false;
            try { store.Read(day); } catch (InvalidDataException ex) { otherDay = ex.Message.Contains("今日ではありません"); }
            Check(broken && otherDay, "today: a broken file or a file of another day is reported as unreadable");
            // 書けないとき(フォルダーの代わりにファイルがある)は IOException(呼び出し側で知らせ、アプリを閉じるまではその値で進める)。
            string blocked = Path.Combine(folder, "blocked");
            File.WriteAllText(blocked, "");
            bool cannotWrite = false;
            try { new TodayCalibrationStore(blocked).Write(today); } catch (IOException) { cannotWrite = true; }
            Check(cannotWrite, "today: a folder that cannot be written raises IOException");
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
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
