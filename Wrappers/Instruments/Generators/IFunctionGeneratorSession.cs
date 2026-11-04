namespace PSCAutomation.Wrappers.Instruments;

// 発振器の接続と、機種に共通する操作をまとめるファイル(バースト出力)。
// USB接続の発振器を探し、WF1973／SDG1032Xを自動判別する。
// 接続時は出力OFF・オフセット0Vに初期化する。
// 信号条件はアプリ側、機種別コマンドは各Sessionクラスが担当する。
// 元: AS110AllCheck Generator/IFunctionGeneratorSession.cs(AS110BoltCheck 版に StopOutput・SetDcOutput を足した版)。
// モノレポで、探索と COM の解放を VisaInstrumentSession へ移した(送るコマンドと順番は元のまま)。
public interface IFunctionGeneratorSession : IDisposable
{
    string Identity { get; }
    string PreparationSummary { get; }
    bool IsConnected { get; }
    string QueryIdentity();
    void PrepareBurst(GeneratorBurstSettings settings);
    void TriggerBurst();
    void SetDcOutput(double volts);
    void StopOutput();
}

public static class FunctionGeneratorConnection
{
    public const string FailureMessage =
        "WF1973／SDG1032X を確認できません。電源、USB、NI-VISA を確認してください。";

    // USB 接続の WF1973 か SDG1032X を探して接続する(先に見つかった方)。
    // 見つからなければ InvalidOperationException。通信できなかったリソースは理由をメッセージに付ける。
    public static IFunctionGeneratorSession Connect()
    {
        var instrument = VisaInstrumentSession.Connect(
            identity => IsWf1973(identity) || IsSdg1032X(identity),
            InitializeOutputOff,
            FailureMessage,
            new VisaSearchOptions { UsbOnly = true, ReportCommunicationErrors = true });
        return Create(instrument);
    }

    // 接続済みの機器を、機種に合うバースト用クラスで包む(機種は *IDN? の 2 項目めで判定)。
    public static IFunctionGeneratorSession Create(IInstrumentSession instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        if (IsWf1973(instrument.Identity)) return new Wf1973BurstSession(instrument);
        if (IsSdg1032X(instrument.Identity)) return new Sdg1032XSession(instrument);
        throw new ArgumentException("WF1973／SDG1032X ではありません(" + instrument.Identity + ")。", nameof(instrument));
    }

    // 接続確立時の初期状態(機種別)。波形設定は PrepareBurst で行う。
    public static void InitializeOutputOff(IInstrumentSession instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        if (IsWf1973(instrument.Identity))
        {
            instrument.Write(":OUTPut:STATe OFF");
            instrument.Write(":SOURce:VOLTage:OFFSet 0");
        }
        else if (IsSdg1032X(instrument.Identity))
        {
            instrument.Write("C1:OUTP OFF");
            instrument.Write("C1:BSWV OFST,0");
        }
    }

    // "メーカー,機種,シリアル,版" の 2 項目めで判定する(元のまま。部分一致にしない)。
    internal static bool IsWf1973(string identity) => Model(identity).Equals("WF1973", StringComparison.OrdinalIgnoreCase);
    internal static bool IsSdg1032X(string identity) => Model(identity).Equals("SDG1032X", StringComparison.OrdinalIgnoreCase);

    private static string Model(string identity)
    {
        string[] fields = identity.Split(',');
        return fields.Length > 1 ? fields[1].Trim() : "";
    }
}
