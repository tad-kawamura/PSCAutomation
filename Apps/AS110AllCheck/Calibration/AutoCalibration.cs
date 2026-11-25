using System.Globalization;
using PSCAutomation.Wrappers.LoggerFiles;

namespace PSCAutomation.Apps.AS110AllCheck;

// 校正の自動読み取り(段階 1。2026-11-13 承認、SPEC「校正の自動読み取り」)。2026-11-18 に 2 つの読み取りに分けた(SPEC「段階 1 の直しと 1 日 1 回の流れ」)。
// 流れ図「校正_変更」の ①〜④ を行い、8 項目(電流 2 点・圧力 2 点)を作る。最後に人が校正画面で「適用」を押す(ここでは適用しない)。
//   「マルチメータで読み取る」(RunCurrentAsync):
//   ① 電流の低点: WF1973 0 V(0 mA の指令)から、7351E で 0 mA ±0.1 mA に入るよう微調整(2026-11-19)→ NR-500 CH1 の電圧の 1 秒平均と、7351E の電流
//   ② 電流の高点: WF1973 5 V(30 mA の指令)から、7351E で 30 mA ±0.1 mA に入るよう微調整(2026-11-19)→ CH1 の電圧の 1 秒平均と、7351E の電流
//      7351E が見つからなければ「もう一度探す / 電流を手で入れる / 中止」。手で入れるときも電圧は自動で読む
//   「GC16 で読み取る」(RunPressureAsync):
//   ③ 圧力の低点: 30 mA、弁を大気側(①閉・②開)→ GC16 が落ち着いたらゼロ調整 → 3 秒後の GC16 の値と CH2 の電圧
//   ④ 圧力の高点: 30 mA のまま、弁を供給側(①開・②閉)→ 人がレギュレーターを目標に合わせて「合わせた」→ 落ち着いた GC16 の値と CH2 の電圧
// NR-500 の電圧は、LOGGER に測定させて読む(IChannelVoltageMeter。測定していないと今の値が読めないため。2026-11-17 の実機確認)。
// 機器と人の操作は下の interface で受け取る(機器なしの模擬に差し替えられる形)。弁は段階 1 では人がボールバルブを切り替える実装を使う。
// 異常時(決まったこと 17・18・21・27): 通信の失敗は出力を止めて「再開(止まった段の最初から)/ 中止」。済んだ段の値は残す。
// 中止・失敗しても、呼び出し側(校正画面)で手入力に切り替えれば測定へ進める(人の指示。この中だけの「止める」)。
internal sealed class AutoCalibrationProcedure
{
    private readonly IPressureGaugeLocator _locator;
    private readonly ICurrentCommandOutput _current;
    private readonly ICurrentMeterLocator _currentMeters;
    private readonly IChannelVoltageMeter _meter;
    private readonly IValveSwitcher _valves;
    private readonly IAutoCalibrationOperator _operator;
    private readonly ICalibrationClock _clock;
    private readonly AutoCalibrationSettings _settings;

    private IPressureGauge? _gauge;
    private ICurrentMeter? _currentMeter;
    // 7351E が見つからず、人が「電流を手で入れる」を選んだ(この読み取りのあいだ)。
    private bool _manualCurrent;
    private double? _currentVolts;
    private ValvePosition _valvePosition = ValvePosition.Measurement;
    // 段ごとの結果(済んだ段は再開しても残す)。
    private (float Volts, float Milliamps)? _currentLow, _currentHigh;
    // ① で合わせた WF1973 の電圧(② の最初の直線に使う)。
    private double _currentLowCommandVolts;
    private (float Volts, float Kilopascals)? _pressureLow, _pressureHigh;
    private CalibrationZeroCheck? _zero;
    private TimeSpan? _sinceGaugeConfirmed;

    public AutoCalibrationProcedure(IPressureGaugeLocator locator, ICurrentCommandOutput current, ICurrentMeterLocator currentMeters,
        IChannelVoltageMeter meter, IValveSwitcher valves, IAutoCalibrationOperator calibrationOperator, ICalibrationClock clock,
        AutoCalibrationSettings? settings = null)
    {
        _locator = locator;
        _current = current;
        _currentMeters = currentMeters;
        _meter = meter;
        _valves = valves;
        _operator = calibrationOperator;
        _clock = clock;
        _settings = settings ?? AutoCalibrationSettings.Default;
        _settings.Validate();
    }

    // 後片付けで出力を止められなかったとき true(呼び出し側で「出力状態不明」にする)。
    public bool OutputStopFailed { get; private set; }
    // 使った GC16 の COM ポート(見つけていなければ null)。
    public string? GaugePort { get; private set; }
    // 使ったマルチメータの名前(見つけていなければ null)。
    public string? CurrentMeterName { get; private set; }

    // 「マルチメータで読み取る」: ①② を行い、電流の 2 点を返す。
    public async Task<CurrentReadingResult> RunCurrentAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunStagesAsync(AutoCalibrationStage.CurrentLow, AutoCalibrationStage.CurrentHigh, null, cancellationToken);
            var points = new ScalingPoints(_currentLow!.Value.Volts, _currentLow.Value.Milliamps, _currentHigh!.Value.Volts, _currentHigh.Value.Milliamps);
            return new CurrentReadingResult(points, _manualCurrent ? null : CurrentMeterName);
        }
        finally
        {
            await CleanUpAsync();
        }
    }

    // 「GC16 で読み取る」: ③④ を行い、圧力の 2 点を返す。
    public async Task<PressureReadingResult> RunPressureAsync(ProductModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        try
        {
            await RunStagesAsync(AutoCalibrationStage.PressureLow, AutoCalibrationStage.PressureHigh, model, cancellationToken);
            var points = new ScalingPoints(_pressureLow!.Value.Volts, _pressureLow.Value.Kilopascals, _pressureHigh!.Value.Volts, _pressureHigh.Value.Kilopascals);
            return new PressureReadingResult(points, model, _zero!, _sinceGaugeConfirmed, GaugePort ?? "");
        }
        finally
        {
            await CleanUpAsync();
        }
    }

    private async Task RunStagesAsync(AutoCalibrationStage first, AutoCalibrationStage last, ProductModel? model, CancellationToken cancellationToken)
    {
        var stage = first;
        while (stage <= last)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await RunStageAsync(stage, model, cancellationToken);
                stage++;
            }
            catch (RedoStageException redo)
            {
                stage = RestartStage(redo.From, stage, first);
                _operator.ShowStatus(StageName(stage) + "を最初からやり直します。");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 17: まず出力を止め、何が起きたかを出す。「再開」で止まった段の最初から(GC16・7351E は探し直す。COM 番号が変わっても探せる)。
                // ② で電流の通り道・CH1 の配線がおかしかったときは、① もその状態で読んでいるので ① から(2026-11-25、人の指摘)。
                StopCurrent();
                ReleaseGauge();
                ReleaseCurrentMeter();
                var restart = RestartStage((ex as RestartStageException)?.From, stage, first);
                string message = $"{StageName(stage)}で止まりました。\r\n{ex.Message}\r\n\r\n" +
                    "WF1973 の出力を止めました。ケーブル・機器を確かめて「再開」を押すと、" +
                    (restart == stage ? "この段の最初から" : StageName(restart) + "から") + "やり直します。";
                if (!await _operator.AskResumeAsync(message, cancellationToken))
                    throw new OperationCanceledException("校正の自動読み取りを中止しました。", ex);
                stage = restart;
            }
        }
    }

    private async Task RunStageAsync(AutoCalibrationStage stage, ProductModel? model, CancellationToken ct)
    {
        _operator.ShowStage(stage);
        _operator.ShowStatus(StageName(stage) + "を始めます。");
        // 電圧を読むため、LOGGER に校正用の雛形で測定させる(済んでいれば何もしない)。
        await _meter.PrepareAsync(ct);
        await (stage switch
        {
            AutoCalibrationStage.CurrentLow => CurrentLowAsync(ct),
            AutoCalibrationStage.CurrentHigh => CurrentHighAsync(ct),
            AutoCalibrationStage.PressureLow => PressureLowAsync(ct),
            _ => PressureHighAsync(model!, ct),
        });
    }

    // ① 0 V(0 mA の指令)から始め、7351E で読みながら 0 mA ±0.1 mA に入るよう WF1973 を微調整して(2026-11-19、人の指示。② と同じ要領)、
    // CH1 の電圧と、そのときの実際の電流を読む(0 mA 固定にはしない。2026-11-18、人)。人が電流を入れるときは微調整しない(0 V のまま)。
    private async Task CurrentLowAsync(CancellationToken ct)
    {
        SetCurrent(0);
        var meter = await EnsureCurrentMeterAsync(ct);
        await _clock.DelayAsync(_settings.CurrentSettle, ct);
        if (meter is not null) await TuneCurrentAsync(meter, AutoCalibrationStage.CurrentLow, ct);
        _currentLowCommandVolts = _currentVolts ?? 0;
        var (volts, milliamps) = await ReadCurrentPointAsync(AutoCalibrationStage.CurrentLow, meter, ct);
        if (Math.Abs(milliamps) > _settings.LowCurrentToleranceMilliamps)
            await AskCurrentDeviationAsync($"① 0 mA の指令で {F(milliamps)} mA 流れています（±{F(_settings.LowCurrentToleranceMilliamps)} mA を超えています）。\r\n" +
                "マルチメータの配線・アンプ・入れた値を確かめてください。", ct);
        _currentLow = (volts, (float)milliamps);
        _operator.ShowStatus($"① 電流の低点：{F(volts)} V = {F((float)milliamps)} mA");
    }

    // ② 5 V(30 mA の指令)から始め、7351E で読みながら 30 mA ±0.1 mA に入るよう WF1973 を微調整する(2026-11-19、人の指示)。
    // 実際の電流は 7351E で読む(見つからなければ人が入れる。決まったこと 23、2026-11-18)。人が入れるときは微調整しない(5 V のまま)。
    private async Task CurrentHighAsync(CancellationToken ct)
    {
        SetCurrent(_settings.CommandVolts);
        var meter = await EnsureCurrentMeterAsync(ct);
        await _clock.DelayAsync(_settings.CurrentSettle, ct);
        if (meter is not null) await TuneCurrentAsync(meter, AutoCalibrationStage.CurrentHigh, ct);
        var (volts, milliamps) = await ReadCurrentPointAsync(AutoCalibrationStage.CurrentHigh, meter, ct);
        // 30 mA が出ていれば、低点からはっきり上がる(アンプの設定で約 1〜5 V)。「低点以下」だけを見ると、出力が止まっていても
        // ノイズで少し上なら通り、0 mA のままの電圧を高点にした(2026-11-14、全体見直し・区切り 4)。
        // 電流(7351E か人が入れた値)で分ける(2026-11-25、人の指示): 30 mA になっていなければ電流の通り道(アンプの電源・コイルのコネクタ・
        // 7351E の端子)、なっていれば NR-500 CH1 の配線。WF1973 は通信と本体エラーで確かめているので並べない。7351E の通信不良は読めた時点で外れる。
        // どれも ① を同じ状態(アンプの電源が切れている・コネクタが外れている・CH1 が外れている)で読んだおそれがあるので、① からやり直す
        // (2026-11-25、人の指摘)。30 mA ±0.1 mA に入らないだけ(25 mA 以上)のときと、通信の失敗は、今までどおり ② から。
        if (_currentLow is { } low && volts - low.Volts < _settings.MinCurrentRiseVolts)
            throw new RestartStageException(milliamps < _settings.MinHighCurrentMilliamps
                ? $"電流が 30 mA になりません（{F(milliamps)} mA）。次を確かめてください。\r\n" + CurrentPathChecklist
                : $"電流は {F(milliamps)} mA 流れていますが、CH1 の電圧が上がりません（低点 {F(low.Volts)} V → {F(volts)} V）。\r\n" +
                    "NR-500 CH1 の配線を確かめてください。", AutoCalibrationStage.CurrentLow);
        if (milliamps < _settings.MinHighCurrentMilliamps)
            await AskCurrentDeviationAsync($"② 30 mA の指令で {F(milliamps)} mA しか流れていません（{F(_settings.MinHighCurrentMilliamps)} mA 未満）。\r\n" +
                "アンプの電源・配線・マルチメータのレンジ・入れた値を確かめてください。「やり直す」は ① からやり直します。", ct, AutoCalibrationStage.CurrentLow);
        if (_currentLow is { } lowPoint && (float)milliamps <= lowPoint.Milliamps)
            throw new RestartStageException($"電流の高点 {F(milliamps)} mA が低点 {F(lowPoint.Milliamps)} mA 以下です。配線を確かめてください。",
                AutoCalibrationStage.CurrentLow);
        _currentHigh = (volts, (float)milliamps);
        _operator.ShowStatus($"② 電流の高点：{F(volts)} V = {F((float)milliamps)} mA");
    }

    // ② で電流が 30 mA にならないときに確かめるもの(2026-11-25、人が挙げた 3 つ)。7351E は部品が DC 電流に設定するので、残るのは端子の挿し間違い。
    internal const string CurrentPathChecklist =
        "・電流アンプの電源が入っているか\r\n" +
        "・コイルのコネクタがつながっているか（つなぎ忘れ・接触不良）\r\n" +
        "・7351E のケーブルが電流の端子に挿さっているか";

    // ① は 0 mA ±0.1 mA、② は 30 mA ±0.1 mA に入るまで、WF1973 の電圧を直す(最大 MaxCurrentAdjustments 回。2026-11-19、人の指示)。
    // 次の電圧は、前の点と今の点を結ぶ直線から求める(② の最初は ① の点から。① の最初は 1 V = 6 mA とみなす。直線が求まらないときも 6 mA/V)。
    // 外れが大きいとき(① は ±1 mA を超える、② は 25 mA 未満 = 配線・アンプの電源の異常)は直さず、今までの知らせに任せる。
    // 電圧の範囲は MinCommandVolts(-0.5 V)〜 上限(WF1973 は出力を切らずに MaxCommandVolts の 5.5 V まで、出し直ししかできない発振器は 5 V まで)。
    // 範囲の端でも届かない・回数を使い切ったときは「やり直す / このまま使う / 中止」(校正値は読んだ電流のままなので、このまま使ってもずれない)。
    private async Task TuneCurrentAsync(ICurrentMeter meter, AutoCalibrationStage stage, CancellationToken ct)
    {
        bool high = stage == AutoCalibrationStage.CurrentHigh;
        string name = high ? "②" : "①";
        double target = high ? _settings.TargetHighMilliamps : _settings.TargetLowMilliamps;
        double tolerance = high ? _settings.HighCurrentToleranceMilliamps : _settings.LowCurrentTuneToleranceMilliamps;
        double upper = _current.CanAdjust ? _settings.MaxCommandVolts : Math.Min(_settings.MaxCommandVolts, AutoCalibrationSettings.SetVoltsLimit);
        double lower = _settings.MinCommandVolts;
        (double Volts, double Milliamps)? previous = high && _currentLow is { } low ? (_currentLowCommandVolts, low.Milliamps) : null;
        for (int adjustments = 0; ; adjustments++)
        {
            double volts = _currentVolts ?? (high ? _settings.CommandVolts : 0);
            double milliamps = await meter.ReadMilliampsAsync(ct);
            if (!double.IsFinite(milliamps)) throw new InvalidDataException($"マルチメータ（{meter.Name}）の電流を読めません。");
            if (Math.Abs(milliamps - target) <= tolerance + 1e-9)
            {
                if (adjustments > 0)
                    _operator.ShowStatus($"{name} 発振器を {F4(volts)} V にして {F(milliamps)} mA に合わせました（{target} ±{tolerance} mA。{adjustments} 回直した）。");
                return;
            }
            if (high ? milliamps < _settings.MinHighCurrentMilliamps : Math.Abs(milliamps) > _settings.LowCurrentToleranceMilliamps) return;
            double slope = previous is { } p && Math.Abs(volts - p.Volts) > 1e-6 ? (milliamps - p.Milliamps) / (volts - p.Volts) : double.NaN;
            if (!(slope > 0) || !double.IsFinite(slope)) slope = _settings.NominalMilliampsPerVolt;
            previous = (volts, milliamps);
            double next = Math.Round(volts + (target - milliamps) / slope, 4);
            bool atLimit = (next > upper && volts >= upper) || (next < lower && volts <= lower);
            if (atLimit || adjustments >= _settings.MaxCurrentAdjustments)
            {
                string reason = atLimit
                    ? $"発振器を{(next > upper ? "上限" : "下限")}の {F4(next > upper ? upper : lower)} V にしても {F(milliamps)} mA です"
                    : $"発振器を {adjustments} 回直しても {F(milliamps)} mA です（今 {F4(volts)} V）";
                await AskCurrentDeviationAsync($"{name} {reason}。{target} ±{tolerance} mA に入りません。\r\n" +
                    "アンプ・配線・マルチメータを確かめてください。「このまま使う」を選んでも、校正値は読んだ電流で作るのでずれません。", ct);
                return;
            }
            next = Math.Clamp(next, lower, upper);
            _operator.ShowStatus($"{name} {F4(volts)} V で {F(milliamps)} mA。発振器を {F4(next)} V に直します。");
            AdjustCurrent(next);
            await _clock.DelayAsync(_settings.CurrentSettle, ct);
        }
    }

    // 電圧(NR-500 の 1 秒平均)と電流を、同じ時間帯に読む。マルチメータが無いときは、人が入れた電流のあとで電圧を読む。
    private async Task<(float Volts, double Milliamps)> ReadCurrentPointAsync(AutoCalibrationStage stage, ICurrentMeter? meter, CancellationToken ct)
    {
        if (meter is null)
        {
            double? entered = await _operator.AskActualCurrentAsync(stage, ct);
            if (entered is not { } value) throw new OperationCanceledException("校正の自動読み取りを中止しました。");
            if (!double.IsFinite(value) || (stage == AutoCalibrationStage.CurrentHigh && value <= 0))
                throw new InvalidDataException("マルチメータの電流は数で入れてください（② は 0 より大きい数）。");
            return (await ReadVoltsAsync(_settings.CurrentChannel, "電流", ct), value);
        }
        var reading = meter.ReadMilliampsAsync(ct);
        float volts;
        try
        {
            volts = await ReadVoltsAsync(_settings.CurrentChannel, "電流", ct);
        }
        catch
        {
            // 電圧の読み取りが失敗・中止しても、7351E の読み取りが終わるのを待ってから抜ける。このあと 7351E との通信を閉じるが、
            // 部品(Adcmt7351ESession)は同時に使われるのを防いでいないので、読み取りの途中で閉じないようにする(2026-11-22)。
            // 電流の値はもう使わないので、読み取りの失敗はここでは見ない(元の失敗を知らせる)。
            try { await reading; }
            catch (Exception) { }
            throw;
        }
        double milliamps = await reading;
        if (!double.IsFinite(milliamps)) throw new InvalidDataException($"マルチメータ（{meter.Name}）の電流を読めません。");
        return (volts, milliamps);
    }

    // 電流が外れていたら知らせる(「やり直す」はこの段の最初から。2026-11-18、AI の案を人が承認)。
    // redoFrom を渡すと、「やり直す」はその段から(② で電流が少なすぎるときは ① から。2026-11-25)。
    private async Task AskCurrentDeviationAsync(string message, CancellationToken ct, AutoCalibrationStage? redoFrom = null)
    {
        var choice = await _operator.AskDeviationAsync(message, "やり直す", ct);
        if (choice == DeviationChoice.Readjust) throw new RedoStageException(redoFrom);
        if (choice == DeviationChoice.Abort) throw new OperationCanceledException("校正の自動読み取りを中止しました。");
    }

    // 7351E を探す。見つからなければ「もう一度探す / 電流を手で入れる / 中止」(2026-11-18、人が選んだ)。手で入れるなら null。
    private async Task<ICurrentMeter?> EnsureCurrentMeterAsync(CancellationToken ct)
    {
        if (_manualCurrent) return null;
        if (_currentMeter is not null) return _currentMeter;
        while (true)
        {
            _operator.ShowStatus("マルチメータ（7351E）を探しています。");
            try
            {
                _currentMeter = await _currentMeters.FindAsync(ct);
                CurrentMeterName = _currentMeter.Name;
                _operator.ShowStatus($"マルチメータ（{_currentMeter.Name}）につなぎました。");
                return _currentMeter;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var choice = await _operator.AskCurrentMeterMissingAsync(
                    "マルチメータ（7351E）が見つかりません。\r\n" + ex.Message + "\r\n\r\n" +
                    "USB と電源を確かめて「もう一度探す」を押すか、「電流を手で入れる」を押してください（電圧は自動で読みます）。", ct);
                if (choice == CurrentMeterMissingChoice.Retry) continue;
                if (choice == CurrentMeterMissingChoice.Abort) throw new OperationCanceledException("校正の自動読み取りを中止しました。", ex);
                _manualCurrent = true;
                _operator.ShowStatus("電流は手で入れます（電圧は自動で読みます）。");
                return null;
            }
        }
    }

    // ③ 大気側で落ち着いたらゼロ調整し、0 点を確かめて記録する(決まったこと 20・21・27、AI の案 8)。
    private async Task PressureLowAsync(CancellationToken ct)
    {
        SetCurrent(_settings.CommandVolts);
        var gauge = await EnsureGaugeAsync(ct);
        _valvePosition = ValvePosition.Switching;
        await _valves.SetAtmosphereAsync(ct);
        _valvePosition = ValvePosition.Atmosphere;
        while (true)
        {
            double settled = await WaitSettledAsync(gauge, "③ 圧力の低点（大気圧）", ct);
            if (Math.Abs(settled) <= _settings.LowDeviationKpa) break;
            // 外れているあいだはゼロ調整しない。
            var choice = await _operator.AskDeviationAsync(
                $"③ 大気圧のはずが {F(settled)} kPa で落ち着きました（±{F(_settings.LowDeviationKpa)} kPa を超えています）。\r\n" +
                "GC16 が大気圧になっているか（配管・バルブの状態）を確かめて下さい。", "合わせ直す", ct);
            if (choice == DeviationChoice.UseAsIs) break;
            if (choice == DeviationChoice.Abort) throw new OperationCanceledException("校正の自動読み取りを中止しました。");
        }
        _sinceGaugeConfirmed = _locator.FirstConfirmedAt is { } confirmed ? _clock.Now - confirmed : null;
        int adjustments = 0, round = 0;
        double zero;
        while (true)
        {
            await gauge.ZeroAdjustAsync(ct);
            adjustments++;
            round++;
            _operator.ShowStatus($"③ GC16 をゼロ調整しました（{adjustments} 回目）。{_settings.ZeroSettle.TotalSeconds:0.#} 秒待って 0 点を確かめます。");
            await _clock.DelayAsync(_settings.ZeroSettle, ct);
            zero = await gauge.ReadKilopascalsAsync(ct);
            _operator.ShowPressure(zero);
            if (Math.Abs(zero) <= _settings.ZeroAllowedKpa) break;
            // ±0.2 kPa を超えたら自動でゼロ調整し直す(最大 3 回)。それでも超えたら「もう一度 / 中止」。超えたままは続けられない。
            if (round <= _settings.ZeroRetries) continue;
            bool again = await _operator.AskZeroRetryAsync(
                $"③ ゼロ調整を {round} 回しても 0 点が {F(zero)} kPa です（±{F(_settings.ZeroAllowedKpa)} kPa を超えています）。\r\n" +
                "大気に開いているか確かめて「もう一度」を押してください。", ct);
            if (!again) throw new OperationCanceledException("校正の自動読み取りを中止しました。");
            round = 0;
        }
        float volts = await ReadVoltsAsync(_settings.PressureChannel, "圧力", ct);
        _zero = new CalibrationZeroCheck(zero, adjustments, Math.Abs(zero) <= _settings.ZeroIdealKpa, _settings.ZeroIdealKpa, _settings.ZeroAllowedKpa);
        _pressureLow = (volts, (float)zero);
        _operator.ShowStatus($"③ 圧力の低点：{F(volts)} V = {F((float)zero)} kPa（0 点 {_zero.Describe()}）");
    }

    // ④ 供給側にし、人がレギュレーターを合わせて「合わせた」を押してから落ち着きを待つ(決まったこと 20・22・27)。
    private async Task PressureHighAsync(ProductModel model, CancellationToken ct)
    {
        SetCurrent(_settings.CommandVolts);
        var gauge = await EnsureGaugeAsync(ct);
        _valvePosition = ValvePosition.Switching;
        await _valves.SetSupplyAsync(ct);
        _valvePosition = ValvePosition.Measurement;
        double target = model.SupplyPressure;
        while (true)
        {
            await WaitRegulatorAdjustedAsync(gauge, target, ct);
            double settled = await WaitSettledAsync(gauge, "④ 圧力の高点", ct);
            // 落ち着いたときの CH2 の電圧を、その場で読む(知らせのあいだに変わらないように)。
            float volts = await ReadVoltsAsync(_settings.PressureChannel, "圧力", ct);
            // 高点が低点より上かを、この段の中で確かめる(止まったら④からやり直せる)。それまでは④のあとの最後の確かめで止まり、
            // 手入力用の文が出て、①〜③の値も消えた(2026-11-14、全体見直し・区切り 4)。
            if (_pressureLow is { } low && volts <= low.Volts)
                throw new InvalidDataException($"圧力の高点の電圧 {F(volts)} V が低点 {F(low.Volts)} V 以下です。NR-500 CH{_settings.PressureChannel}（圧力）の配線を確かめてください。");
            double limit = target * _settings.HighDeviationRatio;
            if (Math.Abs(settled - target) > limit)
            {
                var choice = await _operator.AskDeviationAsync(
                    $"④ {F(settled)} kPa で落ち着きました。目標 {target} kPa から ±{F(limit)} kPa（{_settings.HighDeviationRatio:P0}）を超えています。\r\n" +
                    "供給圧力がかかっているか（配管・バルブの状態）と、レギュレータを確かめて下さい。", "合わせ直す", ct);
                if (choice == DeviationChoice.Readjust) continue;
                if (choice == DeviationChoice.Abort) throw new OperationCanceledException("校正の自動読み取りを中止しました。");
            }
            // 「このまま使う」を選んでも、低点以下の圧力は高点にできない。
            if (_pressureLow is { } lowPoint && (float)settled <= lowPoint.Kilopascals)
                throw new InvalidDataException($"圧力の高点 {F(settled)} kPa が低点 {F(lowPoint.Kilopascals)} kPa 以下です。供給圧力がかかっているか（配管・バルブの状態）を確かめて下さい。");
            _pressureHigh = (volts, (float)settled);
            _operator.ShowStatus($"④ 圧力の高点：{F(volts)} V = {F((float)settled)} kPa（目標 {target} kPa）");
            return;
        }
    }

    // 人が「合わせた」を押すまで、GC16 の値を出し続ける(人はそれを見てレギュレーターを回す)。
    private async Task WaitRegulatorAdjustedAsync(IPressureGauge gauge, double target, CancellationToken ct)
    {
        using var prompt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var adjusted = _operator.WaitRegulatorAdjustedAsync(target, prompt.Token);
        try
        {
            while (!adjusted.IsCompleted)
            {
                _operator.ShowPressure(await gauge.ReadKilopascalsAsync(ct));
                await Task.WhenAny(adjusted, _clock.DelayAsync(_settings.SampleInterval, ct));
                ct.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            // 読み取りが失敗したときは、出したままの「合わせた」を引っ込める。
            await prompt.CancelAsync();
        }
        if (!await adjusted) throw new OperationCanceledException("校正の自動読み取りを中止しました。");
    }

    // 直近 StableWindow(5 秒)の GC16 の値がすべて、最後の値から ±StableTolerance(0.2 kPa)以内になるまで待つ。
    // 待つ上限(15 秒)を過ぎたら弁の向きを確かめるよう知らせ、「待ち続ける / この段をやり直す / 中止」(決まったこと 18・22)。
    private async Task<double> WaitSettledAsync(IPressureGauge gauge, string stageName, CancellationToken ct)
    {
        var samples = new List<(DateTimeOffset At, double Kpa)>();
        DateTimeOffset start = _clock.Now;
        DateTimeOffset deadline = start + _settings.WaitLimit;
        _operator.ShowStatus($"{stageName}：GC16 の値が落ち着くのを待っています。");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            double value = await gauge.ReadKilopascalsAsync(ct);
            DateTimeOffset now = _clock.Now;
            _operator.ShowPressure(value);
            samples.Add((now, value));
            samples.RemoveAll(s => s.At < now - _settings.StableWindow);
            if (now - start >= _settings.StableWindow && samples.All(s => Math.Abs(s.Kpa - value) <= _settings.StableToleranceKpa))
                return value;
            if (now >= deadline)
            {
                var choice = await _operator.AskNotSettledAsync(
                    $"{stageName}：{_settings.WaitLimit.TotalSeconds:0} 秒待っても GC16 の値が落ち着きません（今 {F(value)} kPa）。\r\n" +
                    "配管・バルブの状態を確かめて下さい。", ct);
                if (choice == WaitChoice.Abort) throw new OperationCanceledException("校正の自動読み取りを中止しました。");
                if (choice == WaitChoice.RedoStage) throw new RedoStageException();
                // 待ち続ける: 知らせのあいだの空きをまたがないよう、見始めからやり直す。
                samples.Clear();
                start = _clock.Now;
                deadline = start + _settings.WaitLimit;
                continue;
            }
            await _clock.DelayAsync(_settings.SampleInterval, ct);
        }
    }

    private async Task<IPressureGauge> EnsureGaugeAsync(CancellationToken ct)
    {
        if (_gauge is not null) return _gauge;
        _operator.ShowStatus("GC16 を探しています。");
        _gauge = await _locator.FindAsync(ct);
        GaugePort = _gauge.PortName;
        _operator.ShowStatus($"GC16 を {_gauge.PortName} で見つけ、通信で操作する状態（REMOTE）にしました。");
        return _gauge;
    }

    private void SetCurrent(double volts)
    {
        if (_currentVolts == volts) return;
        _currentVolts = null;
        _current.SetVolts(volts);
        _currentVolts = volts;
    }

    // ② の微調整(出力を切らずに電圧だけ変える。できない発振器は出し直し)。
    private void AdjustCurrent(double volts)
    {
        if (_currentVolts == volts) return;
        _currentVolts = null;
        _current.AdjustVolts(volts);
        _currentVolts = volts;
    }

    private async Task<float> ReadVoltsAsync(int channel, string name, CancellationToken ct)
    {
        double volts = await _meter.ReadVoltsAsync(channel, ct);
        if (!double.IsFinite(volts)) throw new InvalidDataException($"NR-500 CH{channel}（{name}）の電圧を読めません。");
        return (float)volts;
    }

    private void StopCurrent()
    {
        _currentVolts = null;
        try
        {
            _current.Stop();
            OutputStopFailed = false;
        }
        catch (Exception ex)
        {
            OutputStopFailed = true;
            _operator.ShowStatus("WF1973 の出力を止められません：" + ex.Message);
        }
    }

    private void ReleaseGauge()
    {
        var gauge = _gauge;
        _gauge = null;
        if (gauge is null) return;
        try { gauge.Dispose(); }
        catch (Exception ex) { _operator.ShowStatus("GC16 を前面キーが効く状態に戻せません（電源を入れ直すと戻ります）：" + ex.Message); }
    }

    private void ReleaseCurrentMeter()
    {
        var meter = _currentMeter;
        _currentMeter = null;
        if (meter is null) return;
        try { meter.Dispose(); }
        catch (Exception ex) { _operator.ShowStatus("マルチメータとの通信を閉じられません：" + ex.Message); }
    }

    // 終わったら(成功・中止・失敗とも): WF1973 の出力を止め、GC16 を RM,0 に戻し、7351E を離し、弁を測定の向き(①開・②閉)に戻す(AI の案 7、決まったこと 19)。
    // LOGGER(校正用の雛形の測定)は校正画面を閉じるまで続ける(2026-11-18、人が選んだ)ので、ここでは止めない。
    private async Task CleanUpAsync()
    {
        StopCurrent();
        ReleaseGauge();
        ReleaseCurrentMeter();
        if (_valvePosition != ValvePosition.Measurement)
        {
            try
            {
                await _valves.RestoreForMeasurementAsync();
                _valvePosition = ValvePosition.Measurement;
            }
            catch (Exception ex) { _operator.ShowStatus("弁を測定の向きに戻せません：" + ex.Message); }
        }
    }

    public static string StageName(AutoCalibrationStage stage) => stage switch
    {
        AutoCalibrationStage.CurrentLow => "① 電流の低点（0 mA）",
        AutoCalibrationStage.CurrentHigh => "② 電流の高点（30 mA）",
        AutoCalibrationStage.PressureLow => "③ 圧力の低点（大気圧）",
        _ => "④ 圧力の高点（供給圧力）",
    };

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string F4(double value) => value.ToString("0.0000", CultureInfo.InvariantCulture);

    private enum ValvePosition { Measurement, Switching, Atmosphere }
    // 「やり直す」: From が null ならこの段の最初から、あればその段から。
    private sealed class RedoStageException(AutoCalibrationStage? from = null) : Exception
    {
        public AutoCalibrationStage? From { get; } = from;
    }

    // 「再開・中止」で止める失敗のうち、「再開」で前の段からやり直すもの(② の電流の通り道・CH1 の配線 → ①。2026-11-25)。
    private sealed class RestartStageException(string message, AutoCalibrationStage from) : Exception(message)
    {
        public AutoCalibrationStage From { get; } = from;
    }

    // やり直す段: 指定が無ければ今の段。この読み取りの最初の段より前には戻らない。
    private static AutoCalibrationStage RestartStage(AutoCalibrationStage? from, AutoCalibrationStage current, AutoCalibrationStage first) =>
        from is { } stage && stage < current ? (stage < first ? first : stage) : current;
}

internal enum AutoCalibrationStage { CurrentLow, CurrentHigh, PressureLow, PressureHigh }
internal enum WaitChoice { KeepWaiting, RedoStage, Abort }
internal enum DeviationChoice { Readjust, UseAsIs, Abort }
internal enum CurrentMeterMissingChoice { Retry, Manual, Abort }

// 読み取りの決まり。実機で変えられるように 1 か所にまとめる(決まったこと 20〜24・27、2026-11-18 の直し)。
internal sealed record AutoCalibrationSettings
{
    public static readonly AutoCalibrationSettings Default = new();
    // ②〜④ で WF1973 に出す電圧(30 mA の指令。1 V = 6 mA)。
    public double CommandVolts { get; init; } = 5.0;
    // ①② で出力を変えてから読み始めるまでの待ち。
    public TimeSpan CurrentSettle { get; init; } = TimeSpan.FromSeconds(1);
    // ② の電圧が ① からこれだけ上がっていなければ、30 mA が出ていないとみなして止める(AI が決めた値。アンプの設定で高点は約 1〜5 V)。
    public double MinCurrentRiseVolts { get; init; } = 0.3;
    // 電流の外れの知らせ(2026-11-18、AI の案を人が承認): ① は 0 mA から ±1 mA、② は 25 mA 未満。
    public double LowCurrentToleranceMilliamps { get; init; } = 1.0;
    public double MinHighCurrentMilliamps { get; init; } = 25.0;
    // ② の電流をこの範囲に入るよう WF1973 を微調整する(2026-11-19、人: 30 mA ±0.1 mA)。直す回数の上限と、電圧の上限
    // (人: 5.5 V あれば十分。WF1973 は部品の AdjustDcOutput で出力を切らずに変える。出し直ししかできない発振器は SetVoltsLimit まで)。
    public double TargetHighMilliamps { get; init; } = 30.0;
    public double HighCurrentToleranceMilliamps { get; init; } = 0.1;
    public int MaxCurrentAdjustments { get; init; } = 5;
    public double MaxCommandVolts { get; init; } = 5.5;
    // ① を 0 mA ±0.1 mA に合わせる(2026-11-19、人: ② と同じ要領)。アンプのずれで 0 mA に負の電圧が要ることがあるので、下限は -0.5 V(約 -3 mA)。
    public double TargetLowMilliamps { get; init; } = 0.0;
    public double LowCurrentTuneToleranceMilliamps { get; init; } = 0.1;
    public double MinCommandVolts { get; init; } = -0.5;
    // 前の点が無いときの直線の傾き(AS110 は 1 V = 6 mA)。
    public double NominalMilliampsPerVolt { get; init; } = 6.0;
    // 部品の SetDcOutput の上限(出力 OFF→ON で出す。変えない)。
    public const double SetVoltsLimit = 5.0;
    // GC16 を読む間隔(表示の更新は 0.3 秒ごと)。
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(0.3);
    // 落ち着いた: 直近 5 秒の値がすべて、最後の値から ±0.2 kPa 以内。
    public TimeSpan StableWindow { get; init; } = TimeSpan.FromSeconds(5);
    public double StableToleranceKpa { get; init; } = 0.2;
    // 待つ上限(③は弁の OK、④は「合わせた」から)。
    public TimeSpan WaitLimit { get; init; } = TimeSpan.FromSeconds(15);
    // ゼロ調整のあと 0 点を確かめるまでの待ち(移動平均 2.4 秒が入れ替わる分)。
    public TimeSpan ZeroSettle { get; init; } = TimeSpan.FromSeconds(3);
    public double ZeroIdealKpa { get; init; } = 0.1;
    public double ZeroAllowedKpa { get; init; } = 0.2;
    // 0 点が許容を超えたときに自動でゼロ調整し直す回数。
    public int ZeroRetries { get; init; } = 3;
    // 外れの知らせ: ③ はゼロ調整の前の値が ±5 kPa、④ は目標から ±10%。
    public double LowDeviationKpa { get; init; } = 5;
    public double HighDeviationRatio { get; init; } = 0.10;
    public int CurrentChannel { get; init; } = 1;
    public int PressureChannel { get; init; } = 2;
    // NR-500 の電圧は、読むと決めた時点よりあとに記録された、この長さの分を平均する(2026-11-18、人)。
    public TimeSpan VoltageAverageWindow { get; init; } = TimeSpan.FromSeconds(1);
    // LOGGER から PC に点が届く遅れの分、平均の窓の後ろに足して待つ(実機で平均 0.07 秒・最大 0.17 秒。AS100201 の確かめ)。
    public TimeSpan VoltageDeliveryMargin { get; init; } = TimeSpan.FromSeconds(0.3);
    // 平均に使う点の最少数。
    public int MinVoltagePoints { get; init; } = 5;
    // Start してから記録の点数が増え始めるまで・平均の点がそろうまで待つ上限。
    public TimeSpan LoggerStartTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan VoltageReadTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan LoggerPollInterval { get; init; } = TimeSpan.FromSeconds(0.1);

    public void Validate()
    {
        if (!double.IsFinite(CommandVolts) || CommandVolts <= 0 || CommandVolts > 5)
            throw new ArgumentOutOfRangeException(nameof(CommandVolts), "WF1973 に出す電圧は 0 より大きく 5 V 以下にしてください。");
        if (!double.IsFinite(MaxCommandVolts) || MaxCommandVolts < CommandVolts || MaxCommandVolts > 6 ||
            !(TargetHighMilliamps > 0) || !(HighCurrentToleranceMilliamps > 0) || MaxCurrentAdjustments < 0 ||
            !double.IsFinite(MinCommandVolts) || MinCommandVolts > 0 || MinCommandVolts < -AutoCalibrationSettings.SetVoltsLimit ||
            !double.IsFinite(TargetLowMilliamps) || !(LowCurrentTuneToleranceMilliamps > 0) || !(NominalMilliampsPerVolt > 0))
            throw new ArgumentOutOfRangeException(nameof(TargetHighMilliamps), "② の電流の微調整の決まりを確かめてください。");
        if (SampleInterval <= TimeSpan.Zero || StableWindow <= TimeSpan.Zero || WaitLimit <= TimeSpan.Zero ||
            CurrentSettle < TimeSpan.Zero || ZeroSettle < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(WaitLimit), "待ち時間を確かめてください。");
        if (StableToleranceKpa <= 0 || ZeroIdealKpa <= 0 || ZeroAllowedKpa < ZeroIdealKpa || ZeroRetries < 0 ||
            LowDeviationKpa <= 0 || HighDeviationRatio <= 0 || !(MinCurrentRiseVolts > 0) ||
            !(LowCurrentToleranceMilliamps > 0) || !(MinHighCurrentMilliamps > 0))
            throw new ArgumentOutOfRangeException(nameof(ZeroAllowedKpa), "落ち着き・0 点・外れの基準を確かめてください。");
        if (VoltageAverageWindow <= TimeSpan.Zero || VoltageDeliveryMargin < TimeSpan.Zero || MinVoltagePoints < 1 ||
            LoggerStartTimeout <= TimeSpan.Zero || VoltageReadTimeout <= TimeSpan.Zero || LoggerPollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(VoltageAverageWindow), "電圧の平均・LOGGER の待ちを確かめてください。");
    }
}

// 0 点の確かめの結果(決まったこと 21)。
internal sealed record CalibrationZeroCheck(double ValueKpa, int Adjustments, bool WithinIdeal, double IdealKpa, double AllowedKpa)
{
    public string Describe() =>
        $"{ValueKpa.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture)} kPa（" +
        (WithinIdeal ? $"±{IdealKpa.ToString("0.0##", CultureInfo.InvariantCulture)} kPa 以内" :
            $"許容範囲。±{AllowedKpa.ToString("0.0##", CultureInfo.InvariantCulture)} kPa 以内") +
        $"、ゼロ調整 {Adjustments} 回）";
}

// 「マルチメータで読み取る」の結果。MeterName は使ったマルチメータ(電流を人が入れたときは null)。
internal sealed record CurrentReadingResult(ScalingPoints Current, string? MeterName)
{
    public string MethodText => MeterName is null ? "電圧を読み取り、電流は手で入れた" : $"マルチメータで読み取り（{MeterName}）";
}

// 「GC16 で読み取る」の結果。
internal sealed record PressureReadingResult(ScalingPoints Pressure, ProductModel Model, CalibrationZeroCheck Zero,
    TimeSpan? SinceGaugeConfirmed, string GaugePort);

// GC16(通信で操作する状態にしたもの)。Dispose で前面キーが効く状態(RM,0)に戻して手放す。
internal interface IPressureGauge : IDisposable
{
    string PortName { get; }
    Task<double> ReadKilopascalsAsync(CancellationToken cancellationToken);
    Task ZeroAdjustAsync(CancellationToken cancellationToken);
}

// GC16 を探す。見つけたら通信で操作する状態にして返す。見つからなければ例外。
internal interface IPressureGaugeLocator
{
    // この起動で最初に GC16 との通信を確かめた時刻(ウォーミングアップの代わりに経過時間を記録する。決まったこと 13)。
    DateTimeOffset? FirstConfirmedAt { get; }
    Task<IPressureGauge> FindAsync(CancellationToken cancellationToken);
}

// マルチメータ(7351E)。Dispose で通信を閉じる(本体の前面で読める状態に戻す)。
internal interface ICurrentMeter : IDisposable
{
    string Name { get; }
    Task<double> ReadMilliampsAsync(CancellationToken cancellationToken);
}

// マルチメータを探してつなぐ。見つからなければ例外。
internal interface ICurrentMeterLocator
{
    Task<ICurrentMeter> FindAsync(CancellationToken cancellationToken);
}

// 電流の指令(WF1973 の DC 出力)。
internal interface ICurrentCommandOutput
{
    // DC を出す(出力 OFF→ON。5 V まで)。
    void SetVolts(double volts);
    // SetVolts のあと、出力を切らずに電圧だけ変えられるか(WF1973。2026-11-19)。できなければ AdjustVolts は SetVolts と同じ(5 V まで)。
    bool CanAdjust { get; }
    void AdjustVolts(double volts);
    void Stop();
}

// NR-500 の電圧(CH 番号は 1 始まり)。LOGGER が測定しているあいだしか読めないので、校正用の雛形で測定させて読む(2026-11-18)。
internal interface IChannelVoltageMeter
{
    // 読み取りの前に、校正用の雛形を開いて測定させる(済んでいれば何もしない)。
    Task PrepareAsync(CancellationToken cancellationToken);
    // 読むと決めた時点よりあとに記録された点の平均(V)。
    Task<double> ReadVoltsAsync(int channel, CancellationToken cancellationToken);
}

// 弁①(供給側)・②(大気側)の切り替え(AI の案 3)。どの実装でも①と②を同時に開けない(開いている方を閉じてから、もう一方を開ける)。
// 段階 1 は人がボールバルブを切り替える(ManualBallValveSwitcher)。段階 2 で電磁弁の実装を足して差し替える。
internal interface IValveSwitcher
{
    // ①閉・②開(AS110 の中を大気圧にする)。
    Task SetAtmosphereAsync(CancellationToken cancellationToken);
    // ①開・②閉(供給圧力が AS110 にかかる。測定のときの向き)。
    Task SetSupplyAsync(CancellationToken cancellationToken);
    // 中止・失敗のあと、測定の向き(①開・②閉)に戻す。取り消せない。
    Task RestoreForMeasurementAsync();
}

// 人とのやりとり(校正の読み取り画面)。
internal interface IAutoCalibrationOperator
{
    void ShowStatus(string text);
    void ShowPressure(double? kilopascals);
    // いま行っている段(①〜④)。画面の「いまどこか」の表示用(2026-11-20)。表示しない実装は何もしなくてよい。
    void ShowStage(AutoCalibrationStage stage) { }
    // 指示を出して OK を待つ。false = 中止。
    Task<bool> ConfirmAsync(string instruction, CancellationToken cancellationToken);
    // 指示を出して OK を待つ(中止は選べない。後片付け用)。
    Task NotifyAsync(string instruction);
    // ①② マルチメータの電流(mA)を人が入れる(7351E が見つからないとき)。null = 中止。
    Task<double?> AskActualCurrentAsync(AutoCalibrationStage stage, CancellationToken cancellationToken);
    // 7351E が見つからない: もう一度探す / 電流を手で入れる / 中止。
    Task<CurrentMeterMissingChoice> AskCurrentMeterMissingAsync(string message, CancellationToken cancellationToken);
    // ④ 人がレギュレーターを目標に合わせて「合わせた」を押すのを待つ。false = 中止。
    Task<bool> WaitRegulatorAdjustedAsync(double targetKilopascals, CancellationToken cancellationToken);
    Task<WaitChoice> AskNotSettledAsync(string message, CancellationToken cancellationToken);
    // 外れの知らせ。readjustLabel は Readjust のボタンの文字(③④ は「合わせ直す」、①② は「やり直す」)。
    Task<DeviationChoice> AskDeviationAsync(string message, string readjustLabel, CancellationToken cancellationToken);
    // 0 点が合わない: true = もう一度、false = 中止。
    Task<bool> AskZeroRetryAsync(string message, CancellationToken cancellationToken);
    // 通信の失敗: true = 再開(止まった段の最初から)、false = 中止。
    Task<bool> AskResumeAsync(string message, CancellationToken cancellationToken);
}

internal interface ICalibrationClock
{
    DateTimeOffset Now { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemCalibrationClock : ICalibrationClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

// 段階 1 の弁: 人に切り替えてもらい、OK を待つ(電磁弁①②は未選定。決まったこと 15)。
// 2026-11-20(人の指示): 指示は「ボールバルブ①を閉じ②を開け」のような、そのスタンドだけの言い方をやめ、してほしい結果で書く
// (「GC16 が大気圧になるようにして下さい」)。供給側へ戻す指示は出さない: すぐあとの「レギュレータを回して目標圧力に設定して下さい」
// (IAutoCalibrationOperator.WaitRegulatorAdjustedAsync)の 1 画面にまとめた。OK を押したあとにもう 1 つ画面が出て、
// まだ人の作業が要ることが分かりにくかったため。
internal sealed class ManualBallValveSwitcher(IAutoCalibrationOperator calibrationOperator) : IValveSwitcher
{
    public const string AtmosphereInstruction = "GC16 が大気圧になるようにして下さい。\r\n\r\nできたら、下の「OK」を押す\r\n「OK」を押すまで、先へは進みません。";
    public const string RestoreInstruction = "供給圧力がかかる状態（測定のときの状態）に戻して下さい。";

    public async Task SetAtmosphereAsync(CancellationToken cancellationToken)
    {
        if (!await calibrationOperator.ConfirmAsync(AtmosphereInstruction, cancellationToken))
            throw new OperationCanceledException("校正の自動読み取りを中止しました。");
    }

    // 指示は出さない(すぐあとの「レギュレータを回して目標圧力に設定して下さい」にまとめた)。
    public Task SetSupplyAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RestoreForMeasurementAsync() => calibrationOperator.NotifyAsync(RestoreInstruction);
}
