using System.Globalization;
using System.Runtime.InteropServices;

namespace PSCAutomation.Wrappers.Instruments;

// WF1973 を DC 出力にして、オフセット電圧を出す(D3 DC 出力)。AS110NoiseTest では電流指令に使う(1V = 6mA)。
// 元の SetDcOutput(Wf1973BurstSession)は毎回出力 OFF→ON するため、フィードバック中の微調整用に
// 出力を切らずにオフセットだけ変える SetOffsetVolts を設けている。
// DC 以外の波形のまま ON にするとランプ波等が出るので、接続時に必ず DC に切り替える。
// 元: AS110NoiseTest Devices/Wf1973DcSource.cs。
// モノレポで通信を IInstrumentSession 経由にし、アプリ側の interface(IOffsetVoltageSource)の実装を外した
// (アプリ側で包む)。送るコマンドと順番は元のまま。
public sealed class Wf1973DcSource : IDisposable
{
    // ベンダー ID 0x0D4A(NF)/製品 ID 0x000D(WF1973)。
    public const string DefaultResourcePattern = "USB?*::0x0D4A::0x000D::?*::INSTR";
    // DC 波形・負荷 High-Z 時の本体の設定範囲(2026-11-03 実機で ±10V を確認)。
    private const double InstrumentLimitVolts = 10.0;
    // 出力 ON の直後は電流が出きっていない(2026-11-03 実機:待ちなしだと直後の 7351E の 1 回目が約 1.5% 低く、
    // 20ms 待てば正常)。余裕を見て待ってから戻る。ON のままオフセットを変える場合は待ち不要だった。
    private const int OutputOnSettleMilliseconds = 50;

    private IInstrumentSession? _instrument;

    // 接続済みで、PrepareDc を済ませた機器を受け取る(通常は Connect を使う。模擬の差し込みにも使う)。
    public Wf1973DcSource(IInstrumentSession instrument)
    {
        _instrument = instrument ?? throw new ArgumentNullException(nameof(instrument));
        Address = instrument.Address;
        Identity = instrument.Identity;
    }

    public string Address { get; }
    public string Identity { get; }
    public bool IsOutputOn { get; private set; }
    public double OffsetVolts { get; private set; }

    // resource を省略すると USB 接続の WF1973 を探す。
    // 接続時に 出力 OFF・バースト OFF・DC 波形・オフセット 0V にする。
    public static Wf1973DcSource Connect(string? resource = null, int timeoutMs = 2000) =>
        new(VisaInstrumentSession.Connect(
            identity => identity.Contains("WF1973", StringComparison.OrdinalIgnoreCase),
            PrepareDc,
            "WF1973 を確認できません。電源、USB、VISA を確認してください。",
            new VisaSearchOptions
            {
                ResourcePattern = DefaultResourcePattern,
                Resource = resource,
                TimeoutMilliseconds = timeoutMs,
                ReportCommunicationErrors = true,
                RejectedIdentityName = "WF1973",
            }));

    // 出力 OFF の状態で DC にし、オフセット 0V にする。DC になったことを問い合わせて確かめる。
    public static void PrepareDc(IInstrumentSession instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        // DC はバーストできないので先にバーストを止める。
        instrument.Write(":OUTPut:STATe OFF");
        instrument.Write(":SOURce:BURSt:STATe OFF");
        instrument.Write(":SOURce:FUNCtion:SHAPe DC");
        instrument.Write(":SOURce:VOLTage:OFFSet 0V");

        string shape = instrument.Query(":SOURce:FUNCtion:SHAPe?");
        if (!shape.Equals("DC", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"WF1973 を DC 波形にできません(応答:{shape})。");
        CheckError(instrument, "DC 出力の準備");
    }

    // 出力を切らずにオフセットだけ変える。
    public void SetOffsetVolts(double volts)
    {
        var instrument = Instrument;
        if (!double.IsFinite(volts) || Math.Abs(volts) > InstrumentLimitVolts)
            throw new ArgumentOutOfRangeException(nameof(volts), volts, "WF1973 のオフセット範囲外です。");
        // 0.1mV 単位(0.6µA 相当)に丸める。
        string text = volts.ToString("0.0000", CultureInfo.InvariantCulture);
        instrument.Write(":SOURce:VOLTage:OFFSet " + text + "V");
        CheckError(instrument, "オフセット " + text + "V の設定");
        OffsetVolts = double.Parse(text, CultureInfo.InvariantCulture);
    }

    public void SetOutput(bool on)
    {
        var instrument = Instrument;
        instrument.Write(on ? ":OUTPut:STATe ON" : ":OUTPut:STATe OFF");
        IsOutputOn = on;
        if (on) Thread.Sleep(OutputOnSettleMilliseconds);
    }

    // 本体がコマンドを受け付けなかった場合(範囲外など)を検出する。
    private static void CheckError(IInstrumentSession instrument, string action)
    {
        string response = instrument.Query(":SYSTem:ERRor?");
        if (!response.StartsWith("0,", StringComparison.Ordinal) &&
            !response.StartsWith("+0,", StringComparison.Ordinal))
            throw new InvalidOperationException($"WF1973 が {action} でエラーを返しました(応答:{response})。");
    }

    public void Dispose()
    {
        if (_instrument is null) return;
        // 接続を手放した後は出力を止められないため、解放前に出力 OFF・0V を試す。
        try
        {
            _instrument.TimeoutMilliseconds = 500;
            _instrument.Write(":OUTPut:STATe OFF");
            _instrument.Write(":SOURce:VOLTage:OFFSet 0V");
        }
        catch (COMException) { }
        _instrument.Dispose();
        _instrument = null;
        IsOutputOn = false;
    }

    private IInstrumentSession Instrument =>
        _instrument ?? throw new ObjectDisposedException(nameof(Wf1973DcSource));
}
