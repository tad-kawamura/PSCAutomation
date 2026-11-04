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
