using System.Globalization;
using PSCAutomation.Wrappers.LoggerFiles;
using PSCAutomation.Wrappers.WaveLogger.Automation;

namespace PSCAutomation.Apps.AS110AllCheck;

// 漏れ試験の自動判定(specs/AS110AllCheck/SPEC.md「漏れ試験の自動判定」、2026-11-12 承認)。画面・機器に依存しない。
// 人は LOGGER の画面で圧力 CH の「最大値 − 現在値 ≦ 1.0 kPa」なら合格としていた。試験中に読み出した全点で
// 「収集開始からの最大値 − その点の値」を比べ、一度でもしきい値を超えたら不合格(その時点で打ち切る)。
// 最大値の下限(型式ごと。初期値は AS110-035 530 kPa・AS110-015 550 kPa)も点ごとに比べ、届いていなければその時点で不合格(2026-11-12 続き)。

// 最後に行った漏れ試験の結果。NotJudged は自動判定をしなかった(NR-X。人が判断する)。
internal enum LeakOutcome { NotJudged, Pass, Fail, Unmeasurable }

internal static class LeakOutcomeText
{
    public static string Of(LeakOutcome outcome) => outcome switch
    {
        LeakOutcome.Pass => "合格",
        LeakOutcome.Fail => "不合格",
        LeakOutcome.Unmeasurable => "判定できません",
        _ => "判定なし（人が判断）",
    };

    // 「人の判断で合格」の確認の始めの文(2026-11-13、全体見直し。以前は「漏れ判定は判定できませんです。」になった)。
    public static string OverrideQuestion(LeakOutcome outcome) =>
        outcome == LeakOutcome.Unmeasurable ? "漏れ判定は判定できませんでした。" : "漏れ判定は不合格です。";
}

// 圧力のチャンネル。換算は校正を適用した作業用 xdt の係数(XdtChannel.ReadScaledValues と同じ式、float に丸める)。
// AverageWindow は LOGGER のチャンネル設定の移動平均の点数(なしは 1)。今の雛形の圧力は 7 点(docs/operation.md)。
// 判定には移動平均をかけた値を使う(Excel 転送・LOGGER の波形と同じ値。XdtChannel.ReadProcessedValues と同じ中心の窓)。
internal sealed record LeakPressureChannel(int Number, double Gain, double Offset, int AverageWindow = 1)
{
    // メーカー API のチャンネル番号(xdt の CH 番号 − 1)。
    public int ApiChannel => Number - 1;

    // 無効値(振り切れ・未接続などで LOGGER が返す 3.4×10^38)・非数値は null。
    public double? Scale(float raw)
    {
        if (!float.IsFinite(raw) || raw >= WaveLoggerSession.InvalidRawValueThreshold) return null;
        double value = (float)(Gain * raw + Offset);
        return double.IsFinite(value) ? value : null;
    }

    public static LeakPressureChannel FromXdt(string path, int channelNumber)
    {
        var info = XdtFile.Load(path).GetChannel(channelNumber).Info;
        return new LeakPressureChannel(info.Number, info.Gain, info.Offset, info.MovingAverage && info.Window > 1 ? info.Window : 1);
    }
}

internal sealed class LeakJudge
{
    // 値は LOGGER の画面と同じ 0.001 kPa に丸めてから比べる(人が画面の数字で見ていた判定と合わせる。
    // float の値どうしの差は 540 kPa 付近で 0.00006 kPa ほどずれ、画面で差 1.000 でも 1.00006 になり得るため)。
    public const int Digits = 3;
    // 丸めた値どうしの引き算の誤差で、ちょうどしきい値が「超えた」にならないための余裕。
    private const double Tolerance = 1e-9;

    // maximumLowerLimitKpa は最大値の下限(null は比べない)。
    public LeakJudge(double thresholdKpa, double? maximumLowerLimitKpa = null)
    {
        if (!(thresholdKpa > 0) || !double.IsFinite(thresholdKpa)) throw new ArgumentOutOfRangeException(nameof(thresholdKpa));
        if (maximumLowerLimitKpa is double limit && !double.IsFinite(limit)) throw new ArgumentOutOfRangeException(nameof(maximumLowerLimitKpa));
        Threshold = thresholdKpa;
        MaximumLowerLimit = maximumLowerLimitKpa;
    }

    public double Threshold { get; }
    public double? MaximumLowerLimit { get; }
    public long Points { get; private set; }
    public double Maximum { get; private set; } = double.NaN;
    public double Latest { get; private set; } = double.NaN;
    // 今の差(最大値 − 最新の値)。
    public double Difference => Points == 0 ? double.NaN : Maximum - Latest;
    // 試験中で一番大きかった差。
    public double MaxDifference { get; private set; } = double.NaN;
    // 不合格・判定できないが決まったら値が入り、以後は値を受け付けない。
    public LeakOutcome? Decided { get; private set; }
    public string? Problem { get; private set; }

    // 換算後(移動平均の後)の値を 1 点渡す。不合格が決まったら true。
    public bool Add(double value)
    {
        if (Decided is not null) return true;
        if (!double.IsFinite(value)) { MarkUnmeasurable($"{Points + 1} 点目が非数値です"); return true; }
        value = Math.Round(value, Digits, MidpointRounding.AwayFromZero);
        Points++;
        Latest = value;
        if (Points == 1 || value > Maximum) Maximum = value;
        double difference = Maximum - value;
        if (double.IsNaN(MaxDifference) || difference > MaxDifference) MaxDifference = difference;
        if (difference > Threshold + Tolerance)
        {
            Decided = LeakOutcome.Fail;
            Problem = $"差 {Format(difference)} kPa がしきい値 {Format(Threshold, 1)} kPa を超えました";
            return true;
        }
        // 最大値の下限も試験の始めから点ごとに比べる(人の指示、2026-11-12 続き)。ちょうど下限は合格。
        // 最大値は上がるだけなので、実際には始めの点が下限に届いていないとすぐ不合格になる。
        if (MaximumLowerLimit is double limit && Maximum < limit - Tolerance)
        {
            Decided = LeakOutcome.Fail;
            Problem = $"最大値 {Format(Maximum)} kPa が下限 {Format(limit, 1)} kPa に届きません（{Points} 点目）";
            return true;
        }
        return false;
    }

    public void MarkUnmeasurable(string reason)
    {
        if (Decided is not null) return;
        Decided = LeakOutcome.Unmeasurable;
        Problem = reason;
    }

    // 試験時間を終えたときの結果。値を 1 点も読めなかったら判定できない。
    public LeakOutcome Finish()
    {
        if (Decided is null && Points == 0) MarkUnmeasurable("圧力の値を 1 点も読めませんでした");
        return Decided ?? LeakOutcome.Pass;
    }

    public static string Format(double value, int digits = 3) =>
        double.IsFinite(value) ? value.ToString("F" + digits, CultureInfo.InvariantCulture) : "-";
}

// 試験中の読み出しと判定。LOGGER から前回の続きの点を取り出し、換算・移動平均をして LeakJudge に渡す
// (取り出しは関数で受け取る。機器なしで確かめられる)。
// 移動平均は中心の窓なので、窓の右半分(7 点なら 3 点。今の雛形は 2 ms 周期なので 6 ms)がそろった点から判定する。最後(Finish)は残りを窓を縮めて判定する。
// 値が取れない(例外・点数が減った・無効値・点数が 2 秒増えない)ときは「判定できない」にして打ち切る。
// 試験秒数を終えたとき、最後に点が増えてから 0.5 秒以上たっていたら「判定できない」(終わり際の値を見ないまま合格にしない。2026-11-13、全体見直し)。
internal sealed class LeakMonitor
{
    // メーカー API のユニット番号(NR-500 は 1。AS100201SwitchController の取り込みと同じ)。
    public const int Unit = 1;
    // 測定中なのに点数が増えないまま、この秒数たったら判定できない(実機の遅れは最大 0.17 秒、2026-11-11 NR-500)。
    public static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(2);
    // 試験秒数を終えたとき、最後に点が増えてからこの時間たっていたら判定できない(2026-11-13、全体見直し。人が承認)。
    // 以前は StallLimit の 2 秒がたつ前に試験が終わると、途中までの値だけで合格になった(10 秒の試験で 8.1 秒から値が来なくても合格)。
    // 実機の遅れ(最大 0.17 秒)の 3 倍ほど。実機で「判定できません」が出るようなら、ここを見直す。
    public static readonly TimeSpan EndGapLimit = TimeSpan.FromSeconds(0.5);

    private readonly Func<int, IReadOnlyList<int>, long, LoggerRawDataBlock> _read;
    private long _readPos;
    private TimeSpan _lastProgress = TimeSpan.Zero;
    // 最後に読み出しを試みた時刻(試験の終わりに、値が来なくなってからの時間を見る)。
    private TimeSpan _lastPoll = TimeSpan.Zero;
    // 換算した値(移動平均の前)と、判定に渡し終えた点数。
    private readonly List<double> _scaled = [];
    private int _judged;

    public LeakMonitor(LeakPressureChannel channel, double thresholdKpa, Func<int, IReadOnlyList<int>, long, LoggerRawDataBlock> read, double? maximumLowerLimitKpa = null)
    {
        Channel = channel;
        Judge = new LeakJudge(thresholdKpa, maximumLowerLimitKpa);
        _read = read;
    }

    public LeakPressureChannel Channel { get; }
    public LeakJudge Judge { get; }

    // elapsed は LOGGER 開始からの時間。打ち切るとき true。
    public bool Poll(TimeSpan elapsed)
    {
        if (Judge.Decided is not null) return true;
        _lastPoll = elapsed;
        LoggerRawDataBlock block;
        try { block = _read(Unit, [Channel.ApiChannel], _readPos); }
        catch (Exception ex) { Judge.MarkUnmeasurable("LOGGER から値を取り出せません：" + ex.Message); return true; }
        if (block.TotalCount < _readPos) { Judge.MarkUnmeasurable($"LOGGER の点数が減りました（{_readPos} → {block.TotalCount}）"); return true; }
        long expected = block.TotalCount - _readPos;
        if (block.Values.Count != 1 || block.Values[0].Length != expected)
        {
            Judge.MarkUnmeasurable($"取り出した点数が合いません（{string.Join("・", block.Values.Select(v => v.Length))} / {expected}）");
            return true;
        }
        if (expected > 0)
        {
            _readPos = block.TotalCount;
            _lastProgress = elapsed;
            foreach (float raw in block.Values[0])
            {
                if (Channel.Scale(raw) is not double value)
                {
                    Judge.MarkUnmeasurable($"CH{Channel.Number} の {_scaled.Count + 1} 点目が無効値です（振り切れ・未接続）");
                    return true;
                }
                _scaled.Add(value);
            }
            return JudgeAvailable(final: false);
        }
        if (elapsed - _lastProgress >= StallLimit)
        {
            Judge.MarkUnmeasurable($"測定中なのに点数が {StallLimit.TotalSeconds:0} 秒増えません（{_readPos} 点のまま）");
            return true;
        }
        return false;
    }

    // 試験時間を終えたときの結果。移動平均の窓がそろわず残っていた最後の点も判定してから決める。
    // 終わり際に値が来なくなっていたら(最後の読み出しの時点で、点が増えてから EndGapLimit 以上)判定できない。
    public LeakOutcome Finish()
    {
        if (Judge.Decided is null) JudgeAvailable(final: true);
        TimeSpan gap = _lastPoll - _lastProgress;
        if (Judge.Decided is null && _readPos > 0 && gap >= EndGapLimit)
            Judge.MarkUnmeasurable($"試験の終わりの {gap.TotalSeconds:F1} 秒は値を読めませんでした（{_readPos} 点のまま）");
        return Judge.Finish();
    }

    // XdtChannel.ReadProcessedValues と同じ式(i 点目は [i − 窓/2, i − 窓/2 + 窓) の平均、端は窓を縮める、float に丸める)。
    private bool JudgeAvailable(bool final)
    {
        int window = Math.Max(1, Channel.AverageWindow), half = window / 2;
        while (_judged < _scaled.Count)
        {
            int right = _judged - half + window;
            if (!final && right > _scaled.Count) return false;
            right = Math.Min(right, _scaled.Count);
            int left = Math.Max(0, _judged - half);
            double sum = 0;
            for (int k = left; k < right; k++) sum += _scaled[k];
            _judged++;
            if (Judge.Add((float)(sum / (right - left)))) return true;
        }
        return false;
    }
}

// 画面で入れる試験秒数としきい値(SPEC: 秒数 1〜60 の整数、しきい値 0.1〜10.0 kPa を 0.1 刻み)。
// 最大値の下限は 0.1〜1000.0 kPa、不合格の表示は 0.5〜10.0 秒(どちらも 0.1 刻み。2026-11-12 続き)。
internal static class LeakTestInput
{
    public const int MinSeconds = 1, MaxSeconds = 60;
    public const double MinThreshold = 0.1, MaxThreshold = 10.0;
    public const double MinLowerLimit = 0.1, MaxLowerLimit = 1000.0;
    public const double MinFailDisplay = 0.5, MaxFailDisplay = 10.0;

    // 正しければ null、だめなら理由。
    public static string? Validate(string secondsText, string thresholdText, out int seconds, out double threshold)
    {
        threshold = 0;
        if (!int.TryParse(secondsText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || seconds < MinSeconds || seconds > MaxSeconds)
            return $"試験秒数は {MinSeconds}〜{MaxSeconds} の整数で入力してください（今：「{secondsText.Trim()}」）。";
        if (!double.TryParse(thresholdText.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out threshold)
            || threshold < MinThreshold - 1e-9 || threshold > MaxThreshold + 1e-9 || Math.Abs(threshold * 10 - Math.Round(threshold * 10)) > 1e-9)
            return $"しきい値は {MinThreshold:F1}〜{MaxThreshold:F1} kPa を 0.1 刻みで入力してください（今：「{thresholdText.Trim()}」）。";
        threshold = Math.Round(threshold, 1);
        return null;
    }

    // 試験秒数が LOGGER の収集の長さ(雛形の点数 × 周期)を超えていないか(2026-11-13、全体見直し。人が承認)。
    // 漏れ試験はデータ取りと同じ雛形で収集するので、LOGGER は雛形の点数で自分で止まる(今の雛形は 6500 点 × 2 ms = 13 秒。
    // AS110-006 のデータ取りは 13000 点 = 26 秒)。それより長い秒数では、止まった後の値を見られない
    // (以前は 14 秒が 13 秒ぶんだけで合格、15 秒以上は必ず「判定できません」になった)。長さが分からないとき(null)は確かめない。
    public static string? ValidateDuration(int seconds, TimeSpan? acquisitionLength)
    {
        if (acquisitionLength is not TimeSpan length || length <= TimeSpan.Zero || seconds <= length.TotalSeconds + 1e-9) return null;
        return $"試験秒数 {seconds} 秒は長すぎます。いまの LOGGER 雛形は {length.TotalSeconds:0.###} 秒で収集が終わるので、{Math.Floor(length.TotalSeconds + 1e-9):0} 秒までにしてください。";
    }

    // xdt の雛形の収集の長さ(点数 × 周期)。周期を読めなければ null。
    public static TimeSpan? AcquisitionLength(string xdtPath)
    {
        var file = XdtFile.Load(xdtPath);
        return file.SampleInterval is TimeSpan interval && interval > TimeSpan.Zero && file.Info.Samples > 0
            ? TimeSpan.FromTicks(interval.Ticks * file.Info.Samples) : null;
    }

    // 最大値の下限(型式ごと)。空欄はだめ(自動判定しないときは呼ばない)。
    public static string? ValidateLowerLimit(string text, string modelName, out double lowerLimit)
    {
        if (TryParseTenths(text, MinLowerLimit, MaxLowerLimit, out lowerLimit)) return null;
        return text.Trim().Length == 0
            ? $"{modelName} の最大値の下限が入っていません。{MinLowerLimit:F1}〜{MaxLowerLimit:F1} kPa を 0.1 刻みで入力してください。"
            : $"最大値の下限は {MinLowerLimit:F1}〜{MaxLowerLimit:F1} kPa を 0.1 刻みで入力してください（今：「{text.Trim()}」）。";
    }

    // 不合格・判定できないの表示秒数。
    public static string? ValidateFailDisplay(string text, out double seconds) =>
        TryParseTenths(text, MinFailDisplay, MaxFailDisplay, out seconds) ? null
            : $"不合格の表示は {MinFailDisplay:F1}〜{MaxFailDisplay:F1} 秒を 0.1 刻みで入力してください（今：「{text.Trim()}」）。";

    private static bool TryParseTenths(string text, double min, double max, out double value)
    {
        if (!double.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)
            || value < min - 1e-9 || value > max + 1e-9 || Math.Abs(value * 10 - Math.Round(value * 10)) > 1e-6)
        { value = 0; return false; }
        value = Math.Round(value, 1);
        return true;
    }
}
