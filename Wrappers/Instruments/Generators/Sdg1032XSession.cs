using System.Globalization;
using System.Text.RegularExpressions;

namespace PSCAutomation.Wrappers.Instruments;

// SDG1032X専用のコマンドで、渡されたバースト設定をCH1へ適用する(D1 バースト出力)。
// 設定値を読み戻して確認し、初回トリガー時に出力ONにする。
// 同じ設定の準備は繰り返さず、呼び出しごとにトリガーを送信する。
// 負荷は本体設定を使用し、開始位相の指定がなければ本体設定を維持する。
// 元: AS110AllCheck Generator/Sdg1032XSession.cs(AS110BoltCheck 版に StopOutput・SetDcOutput・Dispose 時の出力OFFを足した版)。
// モノレポで通信を IInstrumentSession 経由にした。送るコマンドと順番は元のまま。
public sealed class Sdg1032XSession : IFunctionGeneratorSession
{
    private IInstrumentSession? _instrument;
    private GeneratorBurstSettings? _preparedSettings;
    private bool _outputEnabled;

    // 接続済みの機器を受け取る(通常は FunctionGeneratorConnection.Connect を使う。模擬の差し込みにも使う)。
    public Sdg1032XSession(IInstrumentSession instrument)
    {
        _instrument = instrument ?? throw new ArgumentNullException(nameof(instrument));
        Identity = instrument.Identity;
    }

    public string Identity { get; }
    public bool IsConnected => _instrument is not null;
    public string PreparationSummary { get; private set; } = "未設定";

    public string QueryIdentity() => Query("*IDN?");

    public void PrepareBurst(GeneratorBurstSettings settings)
    {
        _ = _instrument ?? throw new ObjectDisposedException(nameof(Sdg1032XSession));
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        if (_preparedSettings == settings) return;
        _preparedSettings = null;
        _outputEnabled = false;
        try
        {
            Write("C1:OUTP OFF");
            // 波形変更で位相が初期化される場合に備え、現在値を先に保存する。
            Write("C1:BTWV STATE,ON");
            double startPhase = settings.StartPhaseDegrees ?? ReadNumber(Query("C1:BTWV?"), "STPS");
            Write("C1:MDWV STATE,OFF");
            Write("C1:SWWV STATE,OFF");
            Write("C1:BTWV STATE,OFF");
            // 負荷設定LOADは変更しない。接続先に合う本体設定を使用する。
            Write("C1:BSWV WVTP,RAMP");
            Write("C1:BSWV SYM,50");
            Write("C1:BSWV FRQ," + Number(settings.FrequencyHz));
            Write("C1:BSWV OFST," + Number(settings.OffsetVolts));
            Write("C1:BSWV AMP," + Number(settings.AmplitudeVpp));

            // その他のバーストパラメータはSTATE,ON後に設定する。
            Write("C1:BTWV STATE,ON");
            Write("C1:BTWV GATE_NCYC,NCYC");
            Write("C1:BTWV TRSR,MAN");
            Write("C1:BTWV TRMD,OFF");
            Write("C1:BTWV TIME," + settings.BurstCycles.ToString(CultureInfo.InvariantCulture));
            Write("C1:BTWV CARR,WVTP,RAMP");
            Write("C1:BTWV CARR,SYM,50");
            Write("C1:BTWV CARR,FRQ," + Number(settings.FrequencyHz));
            Write("C1:BTWV CARR,OFST," + Number(settings.OffsetVolts));
            Write("C1:BTWV CARR,AMP," + Number(settings.AmplitudeVpp));
            Write("C1:BTWV STPS," + Number(startPhase));
            // 搬送波設定による再計算後に、遅延0を指定する。
            Write("C1:BTWV DLAY,0");

            WaitForCompletion();
            string burst = Query("C1:BTWV?");
            string output = Query("C1:OUTP?");
            // *OPC?だけでは、設定が制限値に丸められたことを検出できない。
            // 出力を有効にする前に実際の設定値を確認する。
            ExpectText(burst, "STATE", "ON");
            ExpectText(burst, "WVTP", "RAMP");
            ExpectText(burst, "TRSR", "MAN");
            ExpectText(burst, "GATE_NCYC", "NCYC");
            ExpectNumber(burst, "FRQ", settings.FrequencyHz);
            ExpectNumber(burst, "AMP", settings.AmplitudeVpp);
            ExpectNumber(burst, "OFST", settings.OffsetVolts);
            ExpectNumber(burst, "SYM", 50);
            ExpectNumber(burst, "TIME", settings.BurstCycles);
            ExpectNumber(burst, "STPS", startPhase);
            double delay = ReadNumber(burst, "DLAY");
            // 読み戻し遅延はログに表示する。1 µsを超えることだけで設定失敗としない。
            if (delay < 0)
                throw new InvalidOperationException("SDG1032X のバースト遅延を確認してください：" + burst);

            PreparationSummary = $"CH1／{Number(settings.FrequencyHz)} Hz／{Number(settings.AmplitudeVpp)} Vp-p／" +
                $"オフセット{Number(settings.OffsetVolts)} V／{settings.BurstCycles}周期／三角波／" +
                $"開始位相{Value(burst, "STPS")}°／負荷{Value(output, "LOAD")}／極性{Value(output, "PLRT")}／設定遅延{Number(delay * 1000)} ms";
            // 起動時は設定のみ。出力OFFを読み戻して確認する。
            string disabled = Query("C1:OUTP?");
            if (!Regex.IsMatch(disabled, @"\b(?:OUTP(?:UT)?\s+)?OFF\s*,", RegexOptions.IgnoreCase))
                throw new InvalidOperationException("SDG1032X CH1の出力OFFを確認できません：" + disabled);
            _preparedSettings = settings;
        }
        catch
        {
            try { Write("C1:OUTP OFF"); } catch { }
            _preparedSettings = null;
            _outputEnabled = false;
            throw;
        }
    }

    public void TriggerBurst()
    {
        _ = _instrument ?? throw new ObjectDisposedException(nameof(Sdg1032XSession));
        if (_preparedSettings is null)
            throw new InvalidOperationException("SDG1032Xの波形設定が完了していません。");
        // 初回の測定開始時だけ出力ON。設定通信は起動時に完了済み。
        if (!_outputEnabled)
        {
            Write("C1:OUTP ON");
            _outputEnabled = true;
        }
        // WF1973の*TRGとは別の機種専用コマンド。指定周期数を出力する。
        Write("C1:BTWV MTRIG");
    }

    private void Write(string command) => Instrument.Write(command);

    private string Query(string command) => Instrument.Query(command);

    private IInstrumentSession Instrument =>
        _instrument ?? throw new ObjectDisposedException(nameof(Sdg1032XSession));

    private void WaitForCompletion()
    {
        string response = Query("*OPC?");
        // ヘッダー有無の両形式に対応する。
        if (!Regex.IsMatch(response, @"^(?:\*?OPC\s+)?1$", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("SDG1032Xの設定完了を確認できません：" + response);
    }

    private static string Number(double value) => value.ToString("G", CultureInfo.InvariantCulture);

    private static string Value(string response, string key)
    {
        Match match = Regex.Match(response,
            @"(?:^|[\s,])" + Regex.Escape(key) + @"\s*,\s*([^,\r\n]+)", RegexOptions.IgnoreCase);
        if (!match.Success)
            throw new InvalidOperationException($"SDG1032Xの{key}を読み取れません：{response}");
        return match.Groups[1].Value.Trim();
    }

    private static double ReadNumber(string response, string key)
    {
        string value = Value(response, key);
        Match match = Regex.Match(value, @"^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?");
        if (!match.Success || !double.TryParse(match.Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double result) || double.IsNaN(result) || double.IsInfinity(result))
            throw new InvalidOperationException($"SDG1032Xの{key}が不正です：{response}");
        return result;
    }

    private static void ExpectNumber(string response, string key, double expected)
    {
        double actual = ReadNumber(response, key);
        if (Math.Abs(actual - expected) > Math.Max(1e-7, Math.Abs(expected) * 1e-5))
            throw new InvalidOperationException(
                $"SDG1032Xの{key}が指定値と異なります（指定{Number(expected)}／実際{Number(actual)}）。" +
                "振幅の場合は本体の負荷設定と許容振幅を確認してください。\n" + response);
    }

    private static void ExpectText(string response, string key, string expected)
    {
        if (!Value(response, key).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"SDG1032Xの{key}が{expected}ではありません：{response}");
    }

    public void StopOutput()
    {
        Write("C1:OUTP OFF");
        _outputEnabled = false;
    }

    public void SetDcOutput(double volts)
    {
        if (!double.IsFinite(volts) || Math.Abs(volts) > 5) throw new ArgumentOutOfRangeException(nameof(volts));
        StopOutput();
        _preparedSettings = null;
        Write("C1:BTWV STATE,OFF");
        Write("C1:BSWV WVTP,DC");
        Write("C1:BSWV OFST," + Number(volts));
        Write("C1:OUTP ON");
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
            _instrument.Write("C1:OUTP OFF");
        }
        catch { }
        _instrument.Dispose();
        _instrument = null;
        _preparedSettings = null;
        _outputEnabled = false;
    }
}
