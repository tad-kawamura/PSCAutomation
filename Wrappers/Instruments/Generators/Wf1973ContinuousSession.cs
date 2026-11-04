using System.Globalization;

namespace PSCAutomation.Wrappers.Instruments;

// 連続出力の設定値。値はアプリが渡す(wrapper は製品の既定値を持たない)。
// 元: AS100201SwitchController Generator/GeneratorContinuousSettings.cs。namespace と public のみ変更。
public sealed record GeneratorContinuousSettings(double AmplitudeVpp, double FrequencyHz, double OffsetVolts);

// WF1973の連続出力コマンド(D2 連続出力)。設定値とスイッチ操作の方針は呼び出し元が保持する。
// 元: AS100201SwitchController Generator/Wf1973Session.cs。
// モノレポで名前を Wf1973ContinuousSession にした(AS110AllCheck のバースト用 Wf1973Session と区別するため)。
// コンストラクタと InitializeContinuousOutput を public にした(元は internal。アプリ側の互換テストが模擬を差し込むため)。
public sealed class Wf1973ContinuousSession : IDisposable
{
    private IInstrumentSession? _instrument;
    private bool _outputEnabled;

    public Wf1973ContinuousSession(IInstrumentSession instrument)
    {
        _instrument = instrument ?? throw new ArgumentNullException(nameof(instrument));
        Identity = instrument.Identity;
    }

    public string Identity { get; }
    public bool IsConnected => _instrument is not null;

    public static Wf1973ContinuousSession Connect() => new(VisaInstrumentSession.Connect(
        identity => identity.Contains("WF1973", StringComparison.OrdinalIgnoreCase),
        InitializeContinuousOutput,
        "WF1973 を確認できません。電源、USB、NI-VISA を確認してください。"));

    public static void InitializeContinuousOutput(IInstrumentSession instrument)
    {
        instrument.Write(":OUTPut:STATe OFF");
        instrument.Write(":SOURce:VOLTage:OFFSet 0");
        instrument.Write(":SOURce:BURSt:STATe OFF");
    }

    // 波形の形も指定して接続する(2026-11-04 追加。AS100201SwitchController の移植時の実機確認で、
    // 別のアプリが DC にした WF1973 では Connect() のままだと DC が出続けたため)。
    // Connect() は本体に残っている形のまま出す(元の動き)。こちらは接続時に形を送って本体の設定に依存しない。
    public static Wf1973ContinuousSession Connect(GeneratorWaveform waveform)
    {
        ValidateWaveform(waveform);
        return new(VisaInstrumentSession.Connect(
            identity => identity.Contains("WF1973", StringComparison.OrdinalIgnoreCase),
            instrument => InitializeContinuousOutput(instrument, waveform),
            "WF1973 を確認できません。電源、USB、NI-VISA を確認してください。"));
    }

    // 出力 OFF のまま波形の形を設定し、元の初期化(オフセット 0・バースト OFF)をして、設定完了を *OPC? で確かめる。
    // 三角波は Wf1973BurstSession.PrepareBurst と同じコマンド(RAMP・対称 50%)。
    public static void InitializeContinuousOutput(IInstrumentSession instrument, GeneratorWaveform waveform)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        ValidateWaveform(waveform);
        instrument.Write(":OUTPut:STATe OFF");
        instrument.Write(":SOURce:FUNCtion:SHAPe RAMP");
        instrument.Write(":SOURce:FUNCtion:RAMP:SYMMetry 50PCT");
        instrument.Write(":SOURce:VOLTage:OFFSet 0");
        instrument.Write(":SOURce:BURSt:STATe OFF");
        string response = instrument.Query("*OPC?");
        if (response != "1")
            throw new InvalidOperationException($"WF1973 の設定完了を確認できません（応答：{response}）。");
    }

    // 接続したまま波形の形だけを送り直す(2026-11-04 追加。AS100201SwitchController の「確認」ボタン用)。
    // 出力の ON / OFF・振幅・周波数・オフセット・位相は変えない。出力中に呼ぶと形が切り替わる間、出力が乱れることがある。
    public void SetWaveform(GeneratorWaveform waveform)
    {
        ValidateWaveform(waveform);
        var instrument = _instrument ?? throw new ObjectDisposedException(nameof(Wf1973ContinuousSession));
        instrument.Write(":SOURce:FUNCtion:SHAPe RAMP");
        instrument.Write(":SOURce:FUNCtion:RAMP:SYMMetry 50PCT");
        string response = instrument.Query("*OPC?");
        if (response != "1")
            throw new InvalidOperationException($"WF1973 の設定完了を確認できません（応答：{response}）。");
    }

    private static void ValidateWaveform(GeneratorWaveform waveform)
    {
        if (waveform != GeneratorWaveform.Triangle)
            throw new ArgumentOutOfRangeException(nameof(waveform), "未対応の波形です。");
    }

    public string QueryIdentity() =>
        (_instrument ?? throw new ObjectDisposedException(nameof(Wf1973ContinuousSession))).Query("*IDN?");

    // UI スレッドの STA から呼ぶ。出力中は OFF にせず位相同期で再開始する。
    // 各 VISA コマンドの間で次の押下を受け付ける。
    // superseded が true なら残りのコマンドを送らず、呼び出し側が新しい位相でやり直す。
    public async Task<bool> StartWithPhaseAsync(GeneratorContinuousSettings settings, int phaseDegrees, Func<bool> superseded)
    {
        if (phaseDegrees is < 0 or >= 360)
            throw new ArgumentOutOfRangeException(nameof(phaseDegrees));

        var instrument = _instrument ?? throw new ObjectDisposedException(nameof(Wf1973ContinuousSession));
        instrument.Write(
            ":SOURce:VOLTage " + settings.AmplitudeVpp.ToString(CultureInfo.InvariantCulture) + ";" +
            "PHASe " + phaseDegrees.ToString(CultureInfo.InvariantCulture) + ";" +
            "FREQuency:FIXed " + settings.FrequencyHz.ToString(CultureInfo.InvariantCulture));
        await Task.Yield();
        if (superseded()) return false;

        instrument.Write(":SOURce:VOLTage:OFFSet " + settings.OffsetVolts.ToString(CultureInfo.InvariantCulture));
        await Task.Yield();
        if (superseded()) return false;

        instrument.Write(":SOURce:PHASe:INITiate");
        await Task.Yield();
        if (superseded()) return false;

        if (!_outputEnabled)
        {
            instrument.Write(":OUTPut:STATe ON");
            _outputEnabled = true;
        }
        return true;
    }

    public void Dispose()
    {
        _instrument?.Dispose();
        _instrument = null;
    }
}
