using System.Buffers.Binary;

namespace PSCAutomation.Wrappers.LoggerFiles;

// 旧 LOGGER(NR-500)の xdt に入っている「波形ウィンドウの表示レンジ(縦軸の上限・下限)」を読み書きする。
// アプリが保存した xdt は雛形の表示レンジ(10mA、74〜84 kPa)のままなので、別の電流では制御圧力の線が
// 画面の外に出て、人が LOGGER で見直すときに混乱する。人が LOGGER で測ったファイルは「収集中オートスケール」で
// 中心が測定値(0.5 kPa 刻み)・幅 10 kPa(0.5 kPa/Div × 20 Div)になっているので、保存後にそれと同じ形に直す。
// 測定データ・スケーリング・チャンネル名は触らない。旧 LOGGER 形式(NR-500)のみ対応。元: AS110NoiseTest Conversion/XdtDisplayRange.cs。
//
// 位置(docs/xdt形式解析メモ.md「表示レンジ」。学習データ・試験データの 507 本すべてで同じ):
//   0x17D0 から 0x30 バイトごとに波形ウィンドウ 1 のチャンネル表示ブロックが CH1, CH2, CH3, … と並ぶ
//     +0x00 u32 線の色、+0x04 float 上限、+0x08 float 下限、+0x24 u16 有効(1)、+0x26 u16 CH 番号(1 始まり)
//   0x1914 から同じ並びでウィンドウ 2(供給圧力を出している方。制御圧力は既定の ±549.875 のまま)
public static class XdtDisplayRange
{
    private const int Window1Offset = 0x17D0;
    private const int BlockSize = 0x30;
    private const int UpperOffset = 0x04;
    private const int LowerOffset = 0x08;
    private const int EnabledOffset = 0x24;
    private const int ChannelOffset = 0x26;

    public sealed record Range(float Upper, float Lower)
    {
        public float Span => Upper - Lower;
        public float Center => (Upper + Lower) / 2;
    }

    // ウィンドウ 1 の channel(1 始まり)の表示レンジ。形式が違えば InvalidDataException。
    public static Range Read(string path, int channel) => Read(File.ReadAllBytes(path), channel);

    public static Range Read(byte[] data, int channel)
    {
        int block = LocateBlock(data, channel);
        return new(
            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(block + UpperOffset, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(block + LowerOffset, 4)));
    }

    // ウィンドウ 1 の channel の表示レンジを、幅はそのままに center を中心にする。center は stepKpa 刻みに丸める
    // (人が LOGGER で測ったファイルと同じ 0.5 刻み)。書き換えたら true、すでに同じなら false(ファイルは触らない)。
    public static bool Center(string path, int channel, double center, double step = 0.5)
    {
        if (!double.IsFinite(center)) throw new ArgumentException("中心値が有限ではありません。", nameof(center));
        if (!double.IsFinite(step) || step <= 0) throw new ArgumentException("刻みは正の値にしてください。", nameof(step));
        byte[] data = File.ReadAllBytes(path);
        int block = LocateBlock(data, channel);
        Range old = Read(data, channel);
        if (!float.IsFinite(old.Span) || old.Span <= 0)
            throw new InvalidDataException($"CH{channel} の表示レンジが不正です(上限 {old.Upper}、下限 {old.Lower})。");
        float rounded = (float)(Math.Round(center / step, MidpointRounding.AwayFromZero) * step);
        float upper = rounded + old.Span / 2;
        float lower = rounded - old.Span / 2;
        if (upper == old.Upper && lower == old.Lower) return false;
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(block + UpperOffset, 4), upper);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(block + LowerOffset, 4), lower);
        File.WriteAllBytes(path, data);
        return true;
    }

    // 形式を確かめてブロックの先頭を返す。旧 LOGGER 形式(署名 03 00 03 00)で、各ブロックの CH 番号が並びどおりのときだけ扱う。
    private static int LocateBlock(byte[] data, int channel)
    {
        if (channel < 1) throw new ArgumentOutOfRangeException(nameof(channel), "CH 番号は 1 以上です。");
        if (data.Length < 20 || !data.AsSpan(0, 10).SequenceEqual("NR500P.DAT"u8))
            throw new InvalidDataException("NR-500 の xdt ではありません。");
        if (!data.AsSpan(16, 4).SequenceEqual(new byte[] { 3, 0, 3, 0 }))
            throw new InvalidDataException("旧 WAVE LOGGER の xdt ではないので、表示レンジの位置が分かりません。");
        int block = Window1Offset + BlockSize * (channel - 1);
        if (block + BlockSize > data.Length)
            throw new InvalidDataException($"CH{channel} の表示ブロックがありません。");
        // 先頭から対象までのブロックが CH1, CH2, … と並んでいることを確かめる(位置の思い違いで別の所を書かないように)
        for (int ch = 1; ch <= channel; ch++)
        {
            int b = Window1Offset + BlockSize * (ch - 1);
            ushort enabled = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(b + EnabledOffset, 2));
            ushort number = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(b + ChannelOffset, 2));
            if (enabled != 1 || number != ch)
                throw new InvalidDataException($"表示ブロック {ch} が CH{ch} ではありません(有効 {enabled}、CH {number})。xdt の形式が想定と違います。");
        }
        return block;
    }
}
