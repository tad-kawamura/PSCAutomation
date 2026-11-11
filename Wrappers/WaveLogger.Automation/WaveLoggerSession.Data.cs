using System.Runtime.InteropServices;

namespace PSCAutomation.Wrappers.WaveLogger.Automation;

/// <summary>
/// <see cref="WaveLoggerSession.ReadRawData"/> の結果。<see cref="TotalCount"/> は PC に読み出されている点数(GetDataCount)。
/// <see cref="Values"/> は要求したチャンネルの順に、<see cref="Start"/> 点目からの換算前の値。
/// 点数が <see cref="Start"/> より少ないとき(点数が減った)は、各チャンネルとも空の配列を返す(判断は呼び出し元)。
/// </summary>
public sealed record LoggerRawDataBlock(long TotalCount, long Start, IReadOnlyList<float[]> Values)
{
    public int Count => Values.Count == 0 ? 0 : Values[0].Length;
}

// 測定中も使える値の取り出し(メーカー API の GetDataCount / GetArrayData / GetCurrentData。説明書 WLPROAuto_M.pdf 7.4)。
// 2026-11-11 に AS100201SwitchController のために追加。既存の関数の動作は変えていない。
// NR-500・旧 WAVE LOGGER で実機確認(Wrappers/Tests/WaveLogger.LiveReadCheck): 測定中に取り出せ、保存した xdt と全点一致。
// WAVE LOGGER X にも同名の関数がある(型情報で確認)が、実機は未確認。
public sealed partial class WaveLoggerSession
{
    /// <summary>GetData / GetCurrentData が異常時に返す値(3.4×10^38、説明書 7.4.2.3 / 7.4.2.4)の判定に使うしきい値。</summary>
    public const float InvalidRawValueThreshold = 3.0e38f;

    /// <summary>
    /// アクティブなファイルから、<paramref name="start"/> 点目(0 始まり)以降で PC に読み出されている点を、
    /// チャンネルごとに換算前の値でまとめて取り出す。測定中も使える(読み出された分だけ返す)。
    /// <paramref name="channels"/> はメーカー API のチャンネル番号(0 始まり。xdt の CH 番号 − 1)。
    /// 取り出せた点数が要求と合わないときは例外。換算(スケーリング)と無効値の判断は呼び出し元が行う。
    /// </summary>
    public LoggerRawDataBlock ReadRawData(int unit, IReadOnlyList<int> channels, long start)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Count == 0) throw new ArgumentException("チャンネルを 1 つ以上指定してください。", nameof(channels));
        ArgumentOutOfRangeException.ThrowIfNegative(start);

        using var observation = ObserveOperation?.Invoke("LOGGER.ReadRawData");
        dynamic app = GetApplication();
        object? document = app.GetActiveFile();
        if (document is null) throw new InvalidOperationException("LOGGER にアクティブなファイルがありません。");
        try
        {
            dynamic doc = document;
            long total = Convert.ToInt64(doc.GetDataCount());
            if (total < 0) throw new InvalidOperationException($"GetDataCount() = {total}");
            long available = Math.Max(0, total - start);
            if (available > int.MaxValue) throw new InvalidOperationException($"一度に取り出せる点数を超えています（{available} 点）。");
            int count = (int)available;
            var values = new float[channels.Count][];
            for (int i = 0; i < channels.Count; i++)
            {
                float[] buffer = new float[count];
                if (count > 0)
                {
                    long got = Convert.ToInt64(doc.GetArrayData(unit, channels[i], checked((int)start), count, ref buffer, 0));
                    if (got != count || buffer.Length < count)
                        throw new InvalidOperationException(
                            $"GetArrayData(ユニット {unit}, チャンネル {channels[i]}, {start} 点目から {count} 点) = {got}（配列 {buffer.Length}）");
                    if (buffer.Length > count) buffer = buffer[..count];
                }
                values[i] = buffer;
            }
            return new LoggerRawDataBlock(total, start, values);
        }
        finally
        {
            // この呼出しで取得した参照だけを解放する(HasActiveFile と同じ)。
            if (Marshal.IsComObject(document)) Marshal.ReleaseComObject(document);
        }
    }

    /// <summary>
    /// 計測中の本体で更新されている最新値(換算前)を返す。無効値(<see cref="InvalidRawValueThreshold"/> 以上)は null。
    /// <paramref name="channel"/> はメーカー API のチャンネル番号(0 始まり)。
    /// </summary>
    public float? GetCurrentRawValue(int unit, int channel)
    {
        using var observation = ObserveOperation?.Invoke("LOGGER.GetCurrentData");
        dynamic app = GetApplication();
        object? document = app.GetActiveFile();
        if (document is null) throw new InvalidOperationException("LOGGER にアクティブなファイルがありません。");
        try
        {
            dynamic doc = document;
            float value = Convert.ToSingle(doc.GetCurrentData(unit, channel));
            return value >= InvalidRawValueThreshold || !float.IsFinite(value) ? null : value;
        }
        finally
        {
            if (Marshal.IsComObject(document)) Marshal.ReleaseComObject(document);
        }
    }
}
