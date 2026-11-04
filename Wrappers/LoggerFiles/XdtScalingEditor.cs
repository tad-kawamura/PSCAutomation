using System.Buffers.Binary;

namespace PSCAutomation.Wrappers.LoggerFiles;

public sealed record ScalingPoints(float Input1, float Output1, float Input2, float Output2);

// XDTと、確認済みNR-X100 YDTの既存スケーリングを変更する共通クラス。
// チャンネル番号は1始まり、入力・換算の2点を指定する。
// 旧LOGGERとLOGGER Xのヘッダを判別し、対応する位置を使う。
// 変更はメモリ上で行い、Saveで任意のファイルへ出力する。
// 単位名・スケーリング有効設定・測定データは変更しない。
public sealed class XdtScalingEditor
{
    private readonly byte[] _data;
    private readonly int _firstValueOffset;
    private readonly int _channelStride;
    private readonly bool _isNrxYdt;

    public XdtScalingEditor(string templatePath)
    {
        _data = File.ReadAllBytes(templatePath);
        if (_data.Length < 20) throw new InvalidDataException("XDTヘッダが不足しています。");
        _isNrxYdt = _data.AsSpan(0, 11).SequenceEqual("NRX100P.DAT"u8);
        if (_isNrxYdt)
        {
            // 添付NR-X100雛形で確認した形式のみ対応。CH2以降の対応は未検証。
            if (_data.Length < 2388 ||
                BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(16, 4)) != 0x01060000 ||
                BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(2360, 4)) != 3 ||
                !_data.AsSpan(2384, 4).SequenceEqual(new byte[] { 0x6d, 0, 0x41, 0 }))
                throw new InvalidDataException("未対応のNR-X YDT形式、またはCH1の単位がmAではありません。");
            _firstValueOffset = 2368;
            _channelStride = 0;
            return;
        }
        if (string.Equals(Path.GetExtension(templatePath), ".ydt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("NR-X100のYDTヘッダを確認できません。");
        uint signature = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(16, 4));
        (_firstValueOffset, _channelStride) = signature switch
        {
            0x00030003 => (820, 68),
            0x00030101 => (1140, 92),
            _ => throw new InvalidDataException("未対応のXDT形式です。スケーリングを変更できません。")
        };
    }

    public void SetScaling(int channel, float input1, float output1, float input2, float output2)
    {
        if (channel < 1) throw new ArgumentOutOfRangeException(nameof(channel), "CH番号は1以上です。");
        if (!float.IsFinite(input1) || !float.IsFinite(output1) ||
            !float.IsFinite(input2) || !float.IsFinite(output2) || input1 == input2)
            throw new ArgumentException("スケーリングの値は有限値で、2つの入力値は異なる必要があります。");
        if (_isNrxYdt && channel != 1)
            throw new NotSupportedException("NR-X YDTのスケーリング変更はCH1のみ対応しています。");
        long position = _firstValueOffset + (long)_channelStride * (channel - 1);
        if (position + 16 > _data.Length)
            throw new InvalidDataException($"CH{channel}のスケーリング領域がありません。");
        int offset = (int)position;
        // 有効設定がない領域には書き込まない。雛形で対象CHのスケーリングを有効にしておく。
        if (BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(offset - 4, 4)) != 1)
            throw new InvalidDataException($"CH{channel}のスケーリングが有効ではありません。雛形の設定を確認してください。");
        for (int i = 0; i < 4; i++)
        {
            float current = BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(offset + 4 * i, 4));
            if (!float.IsFinite(current))
                throw new InvalidDataException($"CH{channel}のスケーリング領域を確認できません。");
        }
        BinaryPrimitives.WriteSingleLittleEndian(_data.AsSpan(offset, 4), input1);
        BinaryPrimitives.WriteSingleLittleEndian(_data.AsSpan(offset + 4, 4), output1);
        BinaryPrimitives.WriteSingleLittleEndian(_data.AsSpan(offset + 8, 4), input2);
        BinaryPrimitives.WriteSingleLittleEndian(_data.AsSpan(offset + 12, 4), output2);
    }

    public ScalingPoints GetScaling(int channel)
    {
        if (channel < 1) throw new ArgumentOutOfRangeException(nameof(channel));
        if (_isNrxYdt && channel != 1)
            throw new NotSupportedException("NR-X YDTのスケーリング読み出しはCH1のみ対応しています。");
        long position = _firstValueOffset + (long)_channelStride * (channel - 1);
        if (position + 16 > _data.Length)
            throw new InvalidDataException($"CH{channel}のスケーリング領域がありません。");
        int offset = (int)position;
        if (BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(offset - 4, 4)) != 1)
            throw new InvalidDataException($"CH{channel}のスケーリングが有効ではありません。");
        return new(
            BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(offset, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(offset + 4, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(offset + 8, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(offset + 12, 4)));
    }

    public void Save(string outputPath) => File.WriteAllBytes(outputPath, _data);
}
