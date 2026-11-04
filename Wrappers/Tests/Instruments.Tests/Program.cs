using System.Globalization;
using System.Runtime.InteropServices;
using PSCAutomation.Wrappers.Instruments;

// Instruments(VISA 機器。元: AS110AllCheck / AS110BoltCheck Generator/、AS100201SwitchController Generator/、
// AS110NoiseTest Devices/)の検査。モノレポで追加。
// 機器には何も送らない。通信を模擬(FakeInstrument / FakeChannel)に差し替えて、
// 送るコマンドと順番が移植元と同じこと、探索・所有権・解放の流れを確かめる。
// 実際の NI-VISA は、どの機器にも当たらないパターンでの探索だけに使う(NI-VISA が要る。ビルドにも要る)。
// 数値の書式が OS の地域設定に左右されないことを見るため、小数点がカンマの地域で動かす。
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
T Fails<T>(Action action, string name) where T : Exception
{
    try { action(); }
    catch (T ex) { return ex; }
    throw new Exception($"{name}: {typeof(T).Name} が出るべき操作が成功した");
}
void Same(IEnumerable<string> actual, string[] expected, string name)
{
    var list = actual.ToList();
    if (!list.SequenceEqual(expected))
        throw new Exception($"{name}: コマンドが違う\n  期待: {string.Join(" | ", expected)}\n  実際: {string.Join(" | ", list)}");
}

// ---------- VisaInstrumentSession(探索・所有権・解放) ----------
{
    var opened = new List<FakeChannel>();
    var channels = new Dictionary<string, FakeChannel>
    {
        ["TCPIP0::1::INSTR"] = new("NF,WF1973,LAN,1"),
        ["USB0::A::INSTR"] = new("OTHER,DMM,1,1"),
        ["USB0::B::INSTR"] = new("NF,WF1973,1234,1.0  "),
    };
    int releases = 0;
    int initialized = 0;
    var session = VisaInstrumentSession.Search(
        new[] { "", null, "TCPIP0::1::INSTR", "USB0::A::INSTR", "USB0::B::INSTR" },
        address => { var c = channels[address]; opened.Add(c); return c; },
        id => id.Contains("WF1973"), s => { initialized++; s.Write(":INIT"); },
        "見つからない", new VisaSearchOptions { UsbOnly = true, TimeoutMilliseconds = 1234 }, () => releases++);
    Check(!opened.Contains(channels["TCPIP0::1::INSTR"]), "UsbOnly で USB 以外を開いた");
    Check(channels["USB0::A::INSTR"].Closed == 1, "識別が合わない接続を閉じていない");
    Check(session.Address == "USB0::B::INSTR" && session.Identity == "NF,WF1973,1234,1.0", "採用した機器の名前・識別");
    Check(initialized == 1 && channels["USB0::B::INSTR"].Writes.SequenceEqual(new[] { "*IDN?", ":INIT" }), "接続直後の初期化");
    Check(channels["USB0::B::INSTR"].Closed == 0 && releases == 0, "採用した接続を閉じた");
    channels["USB0::B::INSTR"].Responses.Enqueue(" 1 \n");
    Check(session.Query("*OPC?") == "1", "Query が前後の空白を除かない");
    session.TimeoutMilliseconds = 500;
    Check(channels["USB0::B::INSTR"].TimeoutMilliseconds == 500, "タイムアウトの変更");
    session.Dispose();
    session.Dispose();
    Check(channels["USB0::B::INSTR"].Closed == 1 && releases == 1, "Dispose で 1 回だけ閉じて解放する");
    Fails<ObjectDisposedException>(() => session.Write("X"), "Dispose 後の送信");
    Console.WriteLine("PASS VisaInstrumentSession: USB 限定・識別・初期化・所有権の移動・1 回だけの解放");

    // 通信できないリソースは飛ばす。理由の一覧は指定したときだけ付ける
    var broken = new FakeChannel("") { FailIdentity = true };
    var dmm = new FakeChannel("ADC,7351E,1,1");
    var other = new FakeChannel("NF,WF1973,9,1");
    var ex = Fails<InvalidOperationException>(() => VisaInstrumentSession.Search(
        new[] { "USB0::X::INSTR", "USB0::Y::INSTR", "USB0::Z::INSTR" },
        a => a.Contains('X') ? broken : a.Contains('Y') ? dmm : other,
        id => id.Contains("SDG"), _ => { }, "SDG がない",
        new VisaSearchOptions { ReportCommunicationErrors = true, RejectedIdentityName = "SDG" }, () => releases++), "見つからない");
    Check(ex.Message.StartsWith("SDG がない\nUSB0::X::INSTR: ") &&
          ex.Message.Contains("USB0::Y::INSTR: SDG ではありません(ADC,7351E,1,1)") &&
          ex.Message.Contains("USB0::Z::INSTR: SDG ではありません(NF,WF1973,9,1)"), "失敗理由の一覧: " + ex.Message);
    Check(broken.Closed == 1 && dmm.Closed == 1 && other.Closed == 1 && releases == 1, "失敗時に閉じていない、または解放した");
    var plain = Fails<InvalidOperationException>(() => VisaInstrumentSession.Search(
        new[] { "USB0::X::INSTR" }, _ => new FakeChannel("") { FailIdentity = true },
        _ => true, _ => { }, "WF1973 を確認できません。", new VisaSearchOptions(), () => releases++), "詳細なし");
    Check(plain.Message == "WF1973 を確認できません。", "詳細なしのメッセージ: " + plain.Message);
    Console.WriteLine("PASS VisaInstrumentSession: 通信できないリソースを飛ばす。失敗理由の一覧は指定したときだけ");

    // 初期化での COMException は次を試す。それ以外は閉じてから投げる
    var first = new FakeChannel("NF,WF1973,1,1");
    var second = new FakeChannel("NF,WF1973,2,1");
    var ok = VisaInstrumentSession.Search(new[] { "USB0::1::INSTR", "USB0::2::INSTR" }, a => a.Contains('1') ? first : second,
        _ => true, s => { if (s.Address.Contains('1')) throw new COMException("初期化で通信失敗"); }, "x",
        new VisaSearchOptions(), () => { });
    Check(first.Closed == 1 && ok.Address == "USB0::2::INSTR", "初期化の通信失敗で次を試していない");
    var leaking = new FakeChannel("NF,WF1973,1,1");
    Fails<InvalidOperationException>(() => VisaInstrumentSession.Search(new[] { "USB0::1::INSTR" }, _ => leaking,
        _ => true, _ => throw new InvalidOperationException("DC にできない"), "x", new VisaSearchOptions(), () => { }), "初期化の失敗");
    Check(leaking.Closed == 1, "初期化が失敗した接続を閉じていない(元の AS110NoiseTest では閉じていなかった)");
    Console.WriteLine("PASS VisaInstrumentSession: 初期化の通信失敗は次を試し、それ以外の失敗は閉じてから投げる");

    // 実際の NI-VISA(ResourceManager)。どの機器にも当たらない VID/PID で探すので、機器には何も送らない
    var none = Fails<InvalidOperationException>(() => VisaInstrumentSession.Connect(_ => true, _ => { }, "該当なし",
        new VisaSearchOptions { ResourcePattern = "USB?*::0xFFFF::0xFFFF::?*::INSTR", ReportCommunicationErrors = true }), "該当なしのパターン");
    Check(none.Message == "該当なし", "該当なしのメッセージ: " + none.Message);
    Console.WriteLine("PASS VisaInstrumentSession: NI-VISA で該当なしのとき、VISA の例外ではなく指定のメッセージで止める");
}

// ---------- D1 バースト出力(FunctionGeneratorConnection / Wf1973BurstSession / Sdg1032XSession) ----------
{
    var wfInit = new FakeInstrument("NF Corporation,WF1973,1234,1.0");
    FunctionGeneratorConnection.InitializeOutputOff(wfInit);
    Same(wfInit.Commands, new[] { ":OUTPut:STATe OFF", ":SOURce:VOLTage:OFFSet 0" }, "WF1973 の接続時初期化");
    var sdgInit = new FakeInstrument("Siglent Technologies,SDG1032X,SDG1X,1.01");
    FunctionGeneratorConnection.InitializeOutputOff(sdgInit);
    Same(sdgInit.Commands, new[] { "C1:OUTP OFF", "C1:BSWV OFST,0" }, "SDG1032X の接続時初期化");
    Check(FunctionGeneratorConnection.Create(wfInit) is Wf1973BurstSession, "WF1973 をバースト用で包まない");
    Check(FunctionGeneratorConnection.Create(sdgInit) is Sdg1032XSession, "SDG1032X を包まない");
    Fails<ArgumentException>(() => FunctionGeneratorConnection.Create(new FakeInstrument("NF,WF1973X,1,1")), "機種名の部分一致");
    Console.WriteLine("PASS FunctionGeneratorConnection: 機種別の接続時初期化と、*IDN? の 2 項目めでの機種判定");

    var settings = new GeneratorBurstSettings(GeneratorWaveform.Triangle, 0.5, 3.2, -0.4, 0.5, 270);
    var fake = new FakeInstrument("NF,WF1973,1,1") { Responder = c => c == "*OPC?" ? "1" : "" };
    using var wf = new Wf1973BurstSession(fake);
    Check(wf.PreparationSummary == "未設定", "準備前の表示");
    Fails<InvalidOperationException>(wf.TriggerBurst, "準備前のトリガー");
    wf.PrepareBurst(settings);
    Same(fake.Commands, new[] {
        ":OUTPut:STATe OFF", ":SOURce:FUNCtion:SHAPe RAMP", ":SOURce:FUNCtion:RAMP:SYMMetry 50PCT",
        ":SOURce:FREQuency:FIXed 0.5HZ", ":SOURce:VOLTage:OFFSet -0.4V", ":SOURce:VOLTage 3.2VPP", ":SOURce:PHASe 270",
        ":SOURce:BURSt:MODE TRIGger", ":SOURce:BURSt:TRIGger:NCYCles 0.5", ":SOURce:BURSt:TRIGger:TDELay 0",
        ":TRIGger:BURSt:SOURce EXTernal", ":SOURce:BURSt:STATe ON", "*OPC?" }, "WF1973 のバースト準備");
    Check(wf.PreparationSummary == "三角波／0.5 Hz／3.2 Vp-p／オフセット-0.4 V／0,5周期／開始位相270°／負荷は本体設定",
        "準備後の表示(周期数は元と同じく地域設定の書式): " + wf.PreparationSummary);
    fake.Commands.Clear();
    wf.PrepareBurst(settings with { });
    Check(fake.Commands.Count == 0, "同じ設定で準備し直した");
    wf.TriggerBurst();
    wf.TriggerBurst();
    Same(fake.Commands, new[] { ":OUTPut:STATe ON", "*TRG", "*TRG" }, "初回だけ出力 ON、毎回 *TRG");
    fake.Commands.Clear();
    wf.SetDcOutput(5);
    Same(fake.Commands, new[] { ":OUTPut:STATe OFF", ":SOURce:BURSt:STATe OFF", ":SOURce:FUNCtion:SHAPe DC",
        ":SOURce:VOLTage:OFFSet 5V", ":OUTPut:STATe ON" }, "WF1973 の DC 出力");
    Fails<ArgumentOutOfRangeException>(() => wf.SetDcOutput(5.1), "DC 出力の範囲");
    fake.Commands.Clear();
    wf.Dispose();
    wf.Dispose();
    Check(fake.Timeouts.SequenceEqual(new[] { 500 }) && fake.Commands.SequenceEqual(new[] { ":OUTPut:STATe OFF" }) &&
          fake.Disposals == 1 && !wf.IsConnected, "Dispose でタイムアウト 500ms・出力 OFF・1 回だけ解放");
    Fails<ObjectDisposedException>(() => wf.QueryIdentity(), "Dispose 後の問い合わせ");

    var noOpc = new FakeInstrument("NF,WF1973,1,1") { Responder = c => c == "*OPC?" ? "0" : "" };
    var wfNoOpc = new Wf1973BurstSession(noOpc);
    Fails<InvalidOperationException>(() => wfNoOpc.PrepareBurst(settings), "設定完了の応答が 1 以外");
    Fails<InvalidOperationException>(wfNoOpc.TriggerBurst, "準備に失敗した後のトリガー");
    var dead = new FakeInstrument("NF,WF1973,1,1") { WriteFailure = _ => new COMException("切断") };
    new Wf1973BurstSession(dead).Dispose();
    Check(dead.Disposals == 1, "出力 OFF が送れなくても解放する");
    Console.WriteLine("PASS Wf1973BurstSession: 準備・トリガー・DC 出力・解放のコマンドが元と同じ");

    // SDG1032X: 設定を読み戻して確かめる
    string burstReply = "C1:BTWV STATE,ON,PRD,2S,STPS,90,TRSR,MAN,TIME,0.5,DLAY,2.4e-07S,GATE_NCYC,NCYC," +
        "CARR,WVTP,RAMP,FRQ,0.5HZ,AMP,3.2V,OFST,-0.4V,SYM,50";
    var sdg = new FakeInstrument("Siglent Technologies,SDG1032X,1,1")
    {
        Responder = c => c switch
        {
            "C1:BTWV?" => burstReply,
            "*OPC?" => "1",
            "C1:OUTP?" => "C1:OUTP OFF,LOAD,HZ,PLRT,NOR",
            _ => "",
        },
    };
    using var sdgSession = new Sdg1032XSession(sdg);
    var noPhase = settings with { StartPhaseDegrees = null };
    sdgSession.PrepareBurst(noPhase);
    Same(sdg.Commands, new[] {
        "C1:OUTP OFF", "C1:BTWV STATE,ON", "C1:BTWV?", "C1:MDWV STATE,OFF", "C1:SWWV STATE,OFF", "C1:BTWV STATE,OFF",
        "C1:BSWV WVTP,RAMP", "C1:BSWV SYM,50", "C1:BSWV FRQ,0.5", "C1:BSWV OFST,-0.4", "C1:BSWV AMP,3.2",
        "C1:BTWV STATE,ON", "C1:BTWV GATE_NCYC,NCYC", "C1:BTWV TRSR,MAN", "C1:BTWV TRMD,OFF", "C1:BTWV TIME,0.5",
        "C1:BTWV CARR,WVTP,RAMP", "C1:BTWV CARR,SYM,50", "C1:BTWV CARR,FRQ,0.5", "C1:BTWV CARR,OFST,-0.4",
        "C1:BTWV CARR,AMP,3.2", "C1:BTWV STPS,90", "C1:BTWV DLAY,0",
        "*OPC?", "C1:BTWV?", "C1:OUTP?", "C1:OUTP?" }, "SDG1032X のバースト準備(開始位相は本体の値を保つ)");
    // 遅延は元と同じく秒 × 1000 をそのまま表示する(2.4e-07 × 1000 は浮動小数点で 0.00023999999999999998)。
    string delayText = (2.4e-07 * 1000).ToString("G", CultureInfo.InvariantCulture);
    Check(sdgSession.PreparationSummary.Contains($"開始位相90°／負荷HZ／極性NOR／設定遅延{delayText} ms"),
        "SDG1032X の準備後の表示: " + sdgSession.PreparationSummary);
    sdg.Commands.Clear();
    sdgSession.TriggerBurst();
    sdgSession.TriggerBurst();
    Same(sdg.Commands, new[] { "C1:OUTP ON", "C1:BTWV MTRIG", "C1:BTWV MTRIG" }, "SDG1032X のトリガー");

    var rounded = new FakeInstrument("Siglent Technologies,SDG1032X,1,1")
    {
        Responder = c => c switch
        {
            "C1:BTWV?" => burstReply.Replace("AMP,3.2V", "AMP,2V"),
            "*OPC?" => "1",
            _ => "C1:OUTP OFF,LOAD,HZ,PLRT,NOR",
        },
    };
    var roundedSession = new Sdg1032XSession(rounded);
    Fails<InvalidOperationException>(() => roundedSession.PrepareBurst(noPhase), "振幅が丸められた");
    Check(rounded.Commands[^1] == "C1:OUTP OFF", "設定の確認に失敗したとき出力 OFF を送っていない");
    rounded.Commands.Clear();
    roundedSession.Dispose();
    Check(rounded.Timeouts.SequenceEqual(new[] { 500 }) && rounded.Commands.SequenceEqual(new[] { "C1:OUTP OFF" }) &&
          rounded.Disposals == 1, "SDG1032X の Dispose");
    Console.WriteLine("PASS Sdg1032XSession: 準備・読み戻しの確認・トリガー・解放のコマンドが元と同じ");
}

// ---------- D2 連続出力(Wf1973ContinuousSession。元 AS100201SwitchController Tests/Compatibility の発振器部分) ----------
{
    var continuous = new GeneratorContinuousSettings(10.2, 0.02, 0);
    var fake = new FakeInstrument("NF,WF1973,FAKE");
    Wf1973ContinuousSession.InitializeContinuousOutput(fake);
    Same(fake.Commands, new[] { ":OUTPut:STATe OFF", ":SOURce:VOLTage:OFFSet 0", ":SOURce:BURSt:STATe OFF" }, "連続出力の接続時初期化");
    fake.Commands.Clear();
    var wf = new Wf1973ContinuousSession(fake);
    Check(await wf.StartWithPhaseAsync(continuous, 15, () => false), "PC 位相で開始できない");
    Check(await wf.StartWithPhaseAsync(continuous, 165, () => false), "CR 位相で開始できない");
    Same(fake.Commands, new[] {
        ":SOURce:VOLTage 10.2;PHASe 15;FREQuency:FIXed 0.02",
        ":SOURce:VOLTage:OFFSet 0", ":SOURce:PHASe:INITiate", ":OUTPut:STATe ON",
        ":SOURce:VOLTage 10.2;PHASe 165;FREQuency:FIXed 0.02",
        ":SOURce:VOLTage:OFFSet 0", ":SOURce:PHASe:INITiate" }, "位相の切替で出力を OFF/ON しない");

    for (int boundary = 1; boundary <= 3; boundary++)
    {
        var interrupted = new FakeInstrument("NF,WF1973,FAKE");
        using var session = new Wf1973ContinuousSession(interrupted);
        int checks = 0;
        Check(!await session.StartWithPhaseAsync(continuous, 15, () => ++checks == boundary), "中断した開始が完了した");
        Check(interrupted.Commands.Count == boundary && !interrupted.Commands.Contains(":OUTPut:STATe ON"), "中断後もコマンドを送った");
        Check(await session.StartWithPhaseAsync(continuous, 165, () => false), "最新の位相で開始できない");
        Check(interrupted.Commands[^1] == ":OUTPut:STATe ON" && interrupted.Commands[^4].Contains("PHASe 165;"), "最新の位相または初回の出力 ON が抜けた");
    }

    int writes = 0;
    var failed = new FakeInstrument("NF,WF1973,FAKE") { WriteFailure = _ => ++writes == 2 ? new IOException("送信失敗") : null };
    using var failureSession = new Wf1973ContinuousSession(failed);
    try { await failureSession.StartWithPhaseAsync(continuous, 15, () => false); throw new Exception("送信失敗が隠れた"); }
    catch (IOException) { }
    Check(failed.Commands.Count == 2, "送信失敗を再試行した");
    await failureSession.StartWithPhaseAsync(continuous, 165, () => false);
    Check(failed.Commands[^1] == ":OUTPut:STATe ON", "失敗した開始を出力 ON 済みとみなした");
    Fails<ArgumentOutOfRangeException>(() => wf.StartWithPhaseAsync(continuous, 360, () => false).GetAwaiter().GetResult(), "位相の範囲");
    wf.Dispose();
    Check(!wf.IsConnected && fake.Disposals == 1, "Dispose で通信を 1 回解放する");
    Fails<ObjectDisposedException>(() => wf.QueryIdentity(), "Dispose 後の問い合わせ");
    Console.WriteLine("PASS Wf1973ContinuousSession: 初期化・位相同期の再開始・中断・送信失敗・解放が元と同じ");

    // 波形の形を指定する初期化(2026-11-04 追加)。出力 OFF のまま三角波にしてから元の初期化をし、*OPC? で確かめる
    var shaped = new FakeInstrument("NF,WF1973,FAKE") { Responder = c => c == "*OPC?" ? "1" : "" };
    Wf1973ContinuousSession.InitializeContinuousOutput(shaped, GeneratorWaveform.Triangle);
    Same(shaped.Commands, new[] {
        ":OUTPut:STATe OFF", ":SOURce:FUNCtion:SHAPe RAMP", ":SOURce:FUNCtion:RAMP:SYMMetry 50PCT",
        ":SOURce:VOLTage:OFFSet 0", ":SOURce:BURSt:STATe OFF", "*OPC?" }, "形を指定した接続時初期化");
    var notDone = new FakeInstrument("NF,WF1973,FAKE") { Responder = c => c == "*OPC?" ? "0" : "" };
    Fails<InvalidOperationException>(() => Wf1973ContinuousSession.InitializeContinuousOutput(notDone, GeneratorWaveform.Triangle), "設定完了の応答が 1 でない");
    Fails<ArgumentOutOfRangeException>(() => Wf1973ContinuousSession.InitializeContinuousOutput(new FakeInstrument("NF,WF1973,FAKE"), (GeneratorWaveform)99), "未対応の波形");
    // 接続したまま形だけ送り直す(出力・振幅・位相は送らない)
    var reshaped = new FakeInstrument("NF,WF1973,FAKE") { Responder = c => c == "*OPC?" ? "1" : "" };
    var reshapeSession = new Wf1973ContinuousSession(reshaped);
    Check(await reshapeSession.StartWithPhaseAsync(continuous, 15, () => false), "形の送り直し前の開始");
    reshaped.Commands.Clear();
    reshapeSession.SetWaveform(GeneratorWaveform.Triangle);
    Same(reshaped.Commands, new[] { ":SOURce:FUNCtion:SHAPe RAMP", ":SOURce:FUNCtion:RAMP:SYMMetry 50PCT", "*OPC?" }, "形だけの送り直し");
    reshaped.Commands.Clear();
    Check(await reshapeSession.StartWithPhaseAsync(continuous, 165, () => false), "形の送り直し後の開始");
    Check(!reshaped.Commands.Contains(":OUTPut:STATe ON") && !reshaped.Commands.Contains(":OUTPut:STATe OFF"), "形の送り直しで出力の状態が変わった");
    reshapeSession.Dispose();
    Fails<ObjectDisposedException>(() => reshapeSession.SetWaveform(GeneratorWaveform.Triangle), "Dispose 後の形の送り直し");
    Console.WriteLine("PASS Wf1973ContinuousSession: 形を指定した初期化(三角波・*OPC? の確認・未対応の波形)、形だけの送り直し");
}

// ---------- D3 DC 出力(Wf1973DcSource) ----------
{
    var fake = new FakeInstrument("NF,WF1973,1,1")
    {
        Responder = c => c switch { ":SOURce:FUNCtion:SHAPe?" => "DC", ":SYSTem:ERRor?" => "+0,\"No error\"", _ => "" },
    };
    Wf1973DcSource.PrepareDc(fake);
    Same(fake.Commands, new[] { ":OUTPut:STATe OFF", ":SOURce:BURSt:STATe OFF", ":SOURce:FUNCtion:SHAPe DC",
        ":SOURce:VOLTage:OFFSet 0V", ":SOURce:FUNCtion:SHAPe?", ":SYSTem:ERRor?" }, "DC 出力の準備");
    fake.Commands.Clear();
    var source = new Wf1973DcSource(fake);
    Check(source.Address == fake.Address && !source.IsOutputOn && source.OffsetVolts == 0, "接続直後の状態");
    source.SetOffsetVolts(1.234567);
    Check(source.OffsetVolts == 1.2346, "0.1mV 単位に丸めていない: " + source.OffsetVolts);
    var started = DateTime.UtcNow;
    source.SetOutput(true);
    Check(source.IsOutputOn && DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(40), "出力 ON の後に待っていない");
    source.SetOutput(false);
    Same(fake.Commands, new[] { ":SOURce:VOLTage:OFFSet 1.2346V", ":SYSTem:ERRor?", ":OUTPut:STATe ON", ":OUTPut:STATe OFF" }, "オフセットと出力の切替");
    Fails<ArgumentOutOfRangeException>(() => source.SetOffsetVolts(10.01), "オフセットの範囲");
    fake.Commands.Clear();
    source.Dispose();
    source.Dispose();
    Check(fake.Timeouts.SequenceEqual(new[] { 500 }) && fake.Commands.SequenceEqual(new[] { ":OUTPut:STATe OFF", ":SOURce:VOLTage:OFFSet 0V" }) &&
          fake.Disposals == 1 && !source.IsOutputOn, "Dispose で出力 OFF・0V にして 1 回だけ解放");
    Fails<ObjectDisposedException>(() => source.SetOutput(true), "Dispose 後の出力");

    var notDc = new FakeInstrument("NF,WF1973,1,1") { Responder = c => c == ":SOURce:FUNCtion:SHAPe?" ? "RAMP" : "+0,\"No error\"" };
    Fails<InvalidOperationException>(() => Wf1973DcSource.PrepareDc(notDc), "DC にならない");
    var rejected = new FakeInstrument("NF,WF1973,1,1") { Responder = c => c == ":SYSTem:ERRor?" ? "-222,\"Data out of range\"" : "DC" };
    Fails<InvalidOperationException>(() => new Wf1973DcSource(rejected).SetOffsetVolts(9), "本体のエラーを見逃した");
    Console.WriteLine("PASS Wf1973DcSource: DC の準備・オフセット・出力の切替・本体エラーの検出・解放が元と同じ");
}

// ---------- ADCMT 7351E ----------
{
    Check(Math.Abs(Adcmt7351ESession.ParseMilliamps("DCI_ +006.027E-03") - 6.027) < 1e-9, "6.027mA");
    Check(Math.Abs(Adcmt7351ESession.ParseMilliamps("DCI_ -000.008E-03") + 0.008) < 1e-9, "-0.008mA");
    foreach (string raw in new[] { "DCV_ +001.000E+00", "DCIO +9.99999E+37", "DCIN +001.000E-03", "DCI_ +9.99999E+37", "DCIE +001.000E-03" })
        Fails<InvalidOperationException>(() => Adcmt7351ESession.ParseMilliamps(raw), raw + " を電流として受け付けた");
    Fails<FormatException>(() => Adcmt7351ESession.ParseMilliamps("TRS0"), "応答を測定値として読んだ");
    Check(string.Join(" ", new Adcmt7351ESettings().ToCommands()) == "F5 R6 PR4 AZ1 SM0 NL0 SC0 DB0 MN0 CO0 H1 TRS3 INIC1", "既定の設定コマンド");
    var custom = new Adcmt7351ESettings
    {
        SamplingRate = Adcmt7351ESamplingRate.Medium,
        AutoZero = Adcmt7351EAutoZero.Once,
        SmoothingCount = 10,
        LineFrequency = Adcmt7351ELineFrequency.Hz50,
        TriggerSource = Adcmt7351ETriggerSource.Immediate,
    };
    Check(string.Join(" ", custom.ToCommands()) == "F5 R6 PR2 AZ2 TI10 SM1 NL0 SC0 DB0 MN0 CO0 H1 LF0 TRS0 INIC1", "変更した設定コマンド");
    var expected = custom.ExpectedResponses().ToDictionary(x => x.Query, x => x.Expected);
    Check(expected["AZ?"] == "AZ0" && expected["TI?"] == "TI10" && expected["LF?"] == "LF0" && expected["TRS?"] == "TRS0", "設定の確認内容(Once は AZ0)");
    Fails<ArgumentException>(() => new Adcmt7351ESettings { SmoothingCount = 1 }.Validate(), "スムージング 1 回");
    Fails<ArgumentException>(() => new Adcmt7351ESettings { TriggerSource = Adcmt7351ETriggerSource.Manual }.Validate(), "トリガ・ソース MANUAL");
    Check(Adcmt7351ESession.SameSetting("F5", "F05") && Adcmt7351ESession.SameSetting("TI10", "TI010"), "数字の桁違いは同じ設定");
    Check(!Adcmt7351ESession.SameSetting("R6", "R0") && !Adcmt7351ESession.SameSetting("PR4", "AZ4"), "違う設定を同じとみなした");
    Check(Adcmt7351ESession.ParseErrorCode("+000,\"No error\"") == 0 && Adcmt7351ESession.ParseErrorCode("-100,\"Command error\"") == -100, "ERR? の番号");
    Check(Adcmt7351ESession.IsMeasurement("DCI_ +006.027E-03") && !Adcmt7351ESession.IsMeasurement("TRS0") &&
          !Adcmt7351ESession.IsMeasurement("+000,\"No error\"") && !Adcmt7351ESession.IsMeasurement("TRD+0.0000E+00"), "測定値と応答の見分け");
    Console.WriteLine("PASS Adcmt7351ESession: 測定値の解釈・設定コマンド・応答の判定が元と同じ");

    // 通信の流れ: 設定ごとに ERR?、問い合わせの前に測定値が返ったら読み捨てる、BUS なら *TRG してから読む
    var defaults = new Adcmt7351ESettings();
    var answers = defaults.ExpectedResponses().ToDictionary(x => x.Query, x => x.Expected);
    var meter = new FakeInstrument("ADC,7351E,1,1")
    {
        Responder = c => c == "ERR?" ? "+000,\"No error\"" : c == "*TRG" ? "DCI_ +006.027E-03" : answers.GetValueOrDefault(c, ""),
    };
    var session = new Adcmt7351ESession(meter);
    meter.Reads.Enqueue("DCI_ +001.000E-03"); // 最初の ERR? の前に溜まっていた測定値
    IReadOnlyList<string> oldErrors = session.ApplySettings(defaults);
    Check(oldErrors.Count == 0 && session.LastRawReading == "DCI_ +001.000E-03", "問い合わせの前の測定値を読み捨てていない");
    var settingCommands = defaults.ToCommands();
    var sent = meter.Commands.Where(c => c != "ERR?").Take(settingCommands.Count).ToList();
    Check(sent.SequenceEqual(settingCommands), "設定コマンドの順番");
    Check(meter.Commands.Count(c => c == "ERR?") == 1 + settingCommands.Count, "設定ごとに ERR? で確かめていない");
    Check(session.TriggerSource == Adcmt7351ETriggerSource.Bus && session.StaleReadingsAfterChange == 0, "BUS トリガの状態");
    meter.Commands.Clear();
    Check(Math.Abs(session.ReadMilliamps() - 6.027) < 1e-9 && meter.Commands.SequenceEqual(new[] { "*TRG" }), "BUS トリガで *TRG してから読む");
    meter.Commands.Clear();
    session.Dispose();
    Check(meter.Commands.SequenceEqual(new[] { "TRS0", "ERR?" }) && meter.Disposals == 1, "Dispose で IMMEDIATE に戻して解放");
    Fails<ObjectDisposedException>(() => session.ReadMilliamps(), "Dispose 後の読み取り");

    var silent = new FakeInstrument("ADC,7351E,1,1") { Responder = _ => "DCI_ +006.027E-03" };
    Fails<InvalidOperationException>(() => new Adcmt7351ESession(silent).Query("F?"), "問い合わせに測定値しか返らない");
    Check(silent.Commands.SequenceEqual(new[] { "F?" }) && silent.ReadCount == 4, "測定値の読み捨ては 3 回まで");
    var timeout = new FakeInstrument("ADC,7351E,1,1") { ReadFailure = () => new COMException("タイムアウト") };
    Fails<InvalidOperationException>(() => new Adcmt7351ESession(timeout).ReadMilliamps(), "読み取りのタイムアウト");
    Console.WriteLine("PASS Adcmt7351ESession: 設定ごとの ERR?・測定値の読み捨て・BUS トリガ・解放の流れが元と同じ");
}

Console.WriteLine("Instruments: すべての検査に合格(機器には何も送っていない)");

// 機器の代わり。送ったコマンドを記録し、読み出しには Reads(先に入れたもの)か Responder(直前のコマンドから)で答える。
sealed class FakeInstrument(string identity) : IInstrumentSession
{
    public string Address { get; } = "USB0::FAKE::INSTR";
    public string Identity { get; } = identity;
    public List<string> Commands { get; } = new();
    public Queue<string> Reads { get; } = new();
    public List<int> Timeouts { get; } = new();
    public Func<string, string> Responder { get; init; } = _ => "";
    public Func<string, Exception?> WriteFailure { get; init; } = _ => null;
    public Func<Exception?> ReadFailure { get; init; } = () => null;
    public int Disposals { get; private set; }
    public int ReadCount { get; private set; }
    private int _timeout = 2000;

    public int TimeoutMilliseconds
    {
        get => _timeout;
        set { _timeout = value; Timeouts.Add(value); }
    }

    public void Write(string command)
    {
        Commands.Add(command);
        if (WriteFailure(command) is { } ex) throw ex;
    }

    public string Read()
    {
        ReadCount++;
        if (ReadFailure() is { } ex) throw ex;
        if (Reads.Count > 0) return Reads.Dequeue();
        return Responder(Commands.Count > 0 ? Commands[^1] : "");
    }

    public string Query(string command) { Write(command); return Read(); }

    public void Dispose() => Disposals++;
}

// VISA の 1 本の接続の代わり(探索の検査用)。
sealed class FakeChannel(string identity) : IVisaChannel
{
    public List<string> Writes { get; } = new();
    public Queue<string> Responses { get; } = new();
    public bool FailIdentity { get; init; }
    public int Closed { get; private set; }
    public int TimeoutMilliseconds { get; set; } = 2000;

    public void Write(string command) => Writes.Add(command);

    public string Read()
    {
        if (Writes.Count > 0 && Writes[^1] == "*IDN?")
        {
            if (FailIdentity) throw new COMException("VI_ERROR_TMO");
            return identity;
        }
        return Responses.Count > 0 ? Responses.Dequeue() : "";
    }

    public void Close() => Closed++;
}
