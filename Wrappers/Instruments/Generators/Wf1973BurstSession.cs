namespace PSCAutomation.Wrappers.Instruments;

// WF1973専用のコマンドで、渡されたバースト設定を適用する(D1 バースト出力)。
// 三角波の設定、設定完了確認、トリガー送信を担当する。
// 同じ設定の準備は繰り返さず、初回トリガー時に出力ONにする。
// 負荷は本体設定を使用し、開始位相の指定がなければ本体設定を維持する。
// 元: AS110AllCheck Generator/Wf1973Session.cs(AS110BoltCheck 版に StopOutput・SetDcOutput・Dispose 時の出力OFFを足した版)。
// モノレポで名前を Wf1973BurstSession にした(AS100201SwitchController の連続出力用 Wf1973Session と区別するため)。
// 通信は IInstrumentSession 経由。送るコマンドと順番は元のまま。
public sealed class Wf1973BurstSession : IFunctionGeneratorSession
{
    private IInstrumentSession? _instrument;
    private GeneratorBurstSettings? _preparedSettings;
    private bool _outputEnabled;

    // 接続済みの機器を受け取る(通常は FunctionGeneratorConnection.Connect か Connect を使う。模擬の差し込みにも使う)。
    public Wf1973BurstSession(IInstrumentSession instrument)
    {
        _instrument = instrument ?? throw new ArgumentNullException(nameof(instrument));
        Identity = instrument.Identity;
    }

    public string Identity { get; }
    public string PreparationSummary => _preparedSettings is null
        ? "未設定" : _preparedSettings.Summary + "／負荷は本体設定";
    public bool IsConnected => _instrument is not null;

    // WF1973 だけを探して接続する(USB に限らない。元の Wf1973Session.Connect。AS110AllCheck では未使用だった)。
    public static Wf1973BurstSession Connect() => new(VisaInstrumentSession.Connect(
        identity => identity.Contains("WF1973", StringComparison.OrdinalIgnoreCase),
        instrument =>
        {
            // 接続確立時の初期状態。波形設定は接続確認中に準備する。
            instrument.Write(":OUTPut:STATe OFF");
            instrument.Write(":SOURce:VOLTage:OFFSet 0");
        },
        "WF1973 を確認できません。電源、USB、NI-VISA を確認してください。"));

    public string QueryIdentity() => Instrument.Query("*IDN?");

    // 起動時は波形設定のみ準備し、出力OFFを維持する。
    public void PrepareBurst(GeneratorBurstSettings settings)
    {
        var io = Instrument;
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        if (_preparedSettings == settings) return;
        _preparedSettings = null;
        _outputEnabled = false;

        // 出力OFFの状態で設定する。
        io.Write(":OUTPut:STATe OFF");
        // 本体に残っている波形設定に依存せず、三角波を指定する。
        io.Write(":SOURce:FUNCtion:SHAPe RAMP");
        io.Write(":SOURce:FUNCtion:RAMP:SYMMetry 50PCT");
        io.Write(":SOURce:FREQuency:FIXed " + GeneratorBurstSettings.Number(settings.FrequencyHz) + "HZ");
        io.Write(":SOURce:VOLTage:OFFSet " + GeneratorBurstSettings.Number(settings.OffsetVolts) + "V");
        io.Write(":SOURce:VOLTage " + GeneratorBurstSettings.Number(settings.AmplitudeVpp) + "VPP");
        if (settings.StartPhaseDegrees is double phase)
            io.Write(":SOURce:PHASe " + GeneratorBurstSettings.Number(phase));

        // トリガーを受けたときだけ、指定された周期数を出力する。
        io.Write(":SOURce:BURSt:MODE TRIGger");
        io.Write(":SOURce:BURSt:TRIGger:NCYCles " + settings.BurstCycles.ToString(System.Globalization.CultureInfo.InvariantCulture));
        io.Write(":SOURce:BURSt:TRIGger:TDELay 0");
        io.Write(":TRIGger:BURSt:SOURce EXTernal");
        io.Write(":SOURce:BURSt:STATe ON");

        // 設定完了を待つ。
        string response = io.Query("*OPC?");
        if (response != "1")
            throw new InvalidOperationException(
                $"WF1973 の設定完了を確認できません（応答：{response}）。");

        _preparedSettings = settings;
    }

    public void TriggerBurst()
    {
        var io = Instrument;

        if (_preparedSettings is null)
            throw new InvalidOperationException("WF1973の波形設定が完了していません。");
        // 初回の測定開始時に出力を有効にする。以後はOFF/ONを繰り返さない。
        if (!_outputEnabled)
        {
            io.Write(":OUTPut:STATe ON");
            _outputEnabled = true;
        }

        // 呼び出しごとにバーストのトリガーを送信する。
        io.Write("*TRG");
    }

    public void StopOutput()
    {
        var io = Instrument;
        io.Write(":OUTPut:STATe OFF");
        _outputEnabled = false;
    }

    public void SetDcOutput(double volts)
    {
        if (!double.IsFinite(volts) || Math.Abs(volts) > 5) throw new ArgumentOutOfRangeException(nameof(volts));
        StopOutput();
        _preparedSettings = null;
        var io = _instrument!;
        io.Write(":SOURce:BURSt:STATe OFF");
        io.Write(":SOURce:FUNCtion:SHAPe DC");
        io.Write(":SOURce:VOLTage:OFFSet " + GeneratorBurstSettings.Number(volts) + "V");
        io.Write(":OUTPut:STATe ON");
        _outputEnabled = true;
    }

    public void Dispose()
    {
        if (_instrument is null) return;
        // 接続を手放した後は出力を止められないため、解放前に出力OFFを試す。
        // 通信できない場合も解放は続ける（呼び出し側で出力状態不明として扱う）。
        try
        {
            _instrument.TimeoutMilliseconds = 500;
            _instrument.Write(":OUTPut:STATe OFF");
        }
        catch { }
        _instrument.Dispose();
        _instrument = null;
        _preparedSettings = null;
        _outputEnabled = false;
    }

    private IInstrumentSession Instrument =>
        _instrument ?? throw new ObjectDisposedException(nameof(Wf1973BurstSession));
}
