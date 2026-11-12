using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PSCAutomation.Apps.AS100201SwitchController;

// 測定中の取り込み(SPEC_測定中の取り込み.md、2026-11-11 承認)。画面・機器に依存しない(取り出しは関数で受け取る)。
//   - F8 で測定を始めたら Begin。0.2 秒ごとに Tick で前回の続きの点を取り出し、換算して、加工した値を 1 行覚える
//   - LOGGER が停止に戻ったら Finish で残りの点を取り出して最後の 1 行を足し、表(CSV に書く中身)を返す
//   - 失敗(例外・点数が減った・無効値・測定中なのに 2 秒増えない)したら、理由を返して以後は取り出さない
//     (呼び出し側が測定を止めて知らせる。そこまでの行は残す)
internal sealed class As100201LiveCapture
{
    // メーカー API のユニット番号(NR-500 は 1。LiveReadCheck の既定と同じ)。
    public const int Unit = 1;
    // 測定中なのに点数が増えないまま、この秒数たったら失敗(実機の遅れは最大 0.17 秒、2026-11-11)。
    public const double StallSeconds = 2;
    // 取り込みの間隔(画面のタイマー)。
    public const int IntervalMilliseconds = 200;

    public static readonly string[] InfoHeader = ["取り込み回", "時刻", "F8からの秒", "側", "位相"];

    private readonly Func<int, IReadOnlyList<int>, long, LoggerRawDataBlock> _read;
    private As100201CaptureTemplate? _template;
    private List<double>[] _values = [];
    private int[] _apiChannels = [];
    private readonly List<string[]> _rows = [];
    private long _readPos;
    private long _beganAt;
    private long _lastProgressAt;

    public As100201LiveCapture(Func<int, IReadOnlyList<int>, long, LoggerRawDataBlock> read) => _read = read;

    // 取り込み中(Begin から Finish / Abandon まで)。
    public bool Active { get; private set; }
    // 失敗して取り出しをやめた(行は残っている)。
    public bool Failed { get; private set; }
    public string? FailureReason { get; private set; }
    public int TickCount { get; private set; }
    public long PointCount => _readPos;
    public int RowCount => _rows.Count;

    // F8 (Begin) からの秒。取り込み中でなければ null(押した行の備考に使う、2026-11-12)。
    public double? SecondsSinceBegin(long nowTimestamp) =>
        Active ? Stopwatch.GetElapsedTime(_beganAt, nowTimestamp).TotalSeconds : null;

    public void Begin(As100201CaptureTemplate template, long nowTimestamp)
    {
        _template = template;
        _apiChannels = template.Channels.Select(c => c.ApiChannel).ToArray();
        _values = template.Channels.Select(_ => new List<double>()).ToArray();
        _rows.Clear();
        _readPos = 0;
        _beganAt = _lastProgressAt = nowTimestamp;
        TickCount = 0;
        Failed = false;
        FailureReason = null;
        Active = true;
    }

    // 取り出さなかった間(ほかの操作の処理中)は、2 秒の見張りを数えない。
    public void Touch(long nowTimestamp)
    {
        if (Active) _lastProgressAt = nowTimestamp;
    }

    // 0.2 秒ごと。loggerState は LOGGER の GetState(4・5 が測定中)。失敗したら理由を返す(成功は null)。
    public string? Tick(long loggerState, long nowTimestamp, string side, string phase)
    {
        if (!Active || Failed) return null;
        TickCount++;
        bool running = loggerState is 4 or 5;
        string? reason = ReadMore(nowTimestamp, side, phase, out bool progressed);
        if (reason is not null) return Fail(reason);
        if (progressed || !running) _lastProgressAt = nowTimestamp;
        else if (Stopwatch.GetElapsedTime(_lastProgressAt, nowTimestamp).TotalSeconds >= StallSeconds)
            return Fail($"測定中なのに点数が {StallSeconds:0} 秒増えません（{_readPos} 点のまま）");
        return null;
    }

    // LOGGER が停止に戻った。残りの点を取り出して最後の行を足し、表を返す。取り込みをしていなければ null。
    // ここでの失敗は測定を止める理由にならない(もう止まっている)ので、理由を finishProblem に返すだけ。
    public As100201FeatureTable? Finish(long nowTimestamp, string side, string phase, out string? finishProblem)
    {
        finishProblem = null;
        if (!Active || _template is null) return null;
        if (!Failed)
        {
            TickCount++;
            finishProblem = ReadMore(nowTimestamp, side, phase, out bool progressed, forceRow: true);
            if (finishProblem is not null) Failed = true;
        }
        Active = false;
        return Snapshot();
    }

    // 記録なしで測定が終わった(雛形の削除など)。行は捨てる。
    public void Abandon() => Active = false;

    private string Fail(string reason)
    {
        Failed = true;
        FailureReason = reason;
        return reason;
    }

    private string? ReadMore(long nowTimestamp, string side, string phase, out bool progressed, bool forceRow = false)
    {
        progressed = false;
        var template = _template!;
        LoggerRawDataBlock block;
        try { block = _read(Unit, _apiChannels, _readPos); }
        catch (Exception ex) { return "LOGGER から値を取り出せません：" + ex.Message; }
        if (block.TotalCount < _readPos) return $"LOGGER の点数が減りました（{_readPos} → {block.TotalCount}）";
        if (block.Values.Count != _apiChannels.Length) return $"取り出したチャンネルの数が合いません（{block.Values.Count} / {_apiChannels.Length}）";
        long expected = block.TotalCount - _readPos;
        if (block.Values.Any(v => v.Length != expected))
            return $"取り出した点数が足りません（{string.Join("・", block.Values.Select(v => v.Length))} / {expected}）";
        for (int c = 0; c < _apiChannels.Length; c++)
        {
            float[] raw = block.Values[c];
            for (int i = 0; i < raw.Length; i++)
            {
                if (!float.IsFinite(raw[i]) || raw[i] >= WaveLoggerSession.InvalidRawValueThreshold)
                    return $"{template.Channels[c].Label} の {_readPos + i + 1} 点目が無効値です";
            }
        }
        for (int c = 0; c < _apiChannels.Length; c++)
            foreach (float raw in block.Values[c]) _values[c].Add(template.Channels[c].Scale(raw));
        progressed = expected > 0;
        _readPos = block.TotalCount;
        // 点が増えたときだけ行を足す(増えなかった回は前の行と同じ値になるため)。最後(Finish)は必ず足す。
        if (progressed || forceRow) AddRow(nowTimestamp, side, phase);
        return null;
    }

    private void AddRow(long nowTimestamp, string side, string phase)
    {
        var template = _template!;
        var features = As100201LiveFeatures.Compute(template.Channels, _values, (int)_readPos, template.IntervalSeconds);
        var row = new List<string>
        {
            TickCount.ToString(CultureInfo.InvariantCulture),
            DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            As100201LiveFeatures.Format(Math.Round(Stopwatch.GetElapsedTime(_beganAt, nowTimestamp).TotalSeconds, 3)),
            side,
            phase
        };
        row.AddRange(features);
        _rows.Add(row.ToArray());
    }

    private As100201FeatureTable Snapshot()
    {
        var header = InfoHeader.Concat(As100201LiveFeatures.Header(_template!.Channels)).ToArray();
        return new As100201FeatureTable(header, _rows.ToList());
    }
}

// CSV に書く中身(測定ごと)。書くのは xdt を保存したあと(As100201LearningSession の再試行に乗る)。
internal sealed record As100201FeatureTable(IReadOnlyList<string> Header, IReadOnlyList<string[]> Rows)
{
    // UTF-8(BOM 付き)、先頭にヘッダー行(台帳と同じ。Excel でそのまま開ける)。
    public void Write(string path)
    {
        var sb = new StringBuilder();
        sb.Append(As100201LiveFeatures.CsvLine(Header)).Append("\r\n");
        foreach (var row in Rows) sb.Append(As100201LiveFeatures.CsvLine(row)).Append("\r\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    public long LastPointCount => Rows.Count == 0 ? 0 :
        long.Parse(Rows[^1][As100201LiveCapture.InfoHeader.Length], CultureInfo.InvariantCulture);
}
