namespace PSCAutomation.Wrappers.Instruments;

// 7351E の ADC コマンド(取扱説明書 6.7.3)で設定する項目。数値はコマンドの末尾の数字。
// 元: AS110NoiseTest Devices/Adcmt7351ESettings.cs。namespace と public のみ変更(アプリの検査ツールが ToCommands・ExpectedResponses を使う)。
// 本体のコマンド言語が ADC(MENU → 7 I/F → LANG)である前提。2026-11-03 の実機で全項目の設定・問い合わせを確認済み。

// 測定レンジ(DCI)。R3〜R5 は DCI にはない。
public enum Adcmt7351ERange { Auto = 0, Range200mA = 6, Range2000mA = 7, Range10A = 8 }

// サンプリング・レート。*RST 時は Slow2。周波数測定以外は 4 段階。
public enum Adcmt7351ESamplingRate { Fast = 1, Medium = 2, Slow1 = 3, Slow2 = 4 }

// オート・ゼロ。Once は 1 回実行した後 OFF になる。
public enum Adcmt7351EAutoZero { Off = 0, On = 1, Once = 2 }

// トリガ・ソース。External は 7351E + オプション 03 のみ。
public enum Adcmt7351ETriggerSource { Immediate = 0, Manual = 1, External = 2, Bus = 3 }

// 電源周波数(ノイズ除去の積分時間に関係)。*RST では初期化されない。
public enum Adcmt7351ELineFrequency { Hz50 = 0, Hz60 = 1 }

// 電流読み取り用の設定。ApplySettings で送る。
// 既定は DCI・200mA レンジ固定(35mA を 1µA 分解能で読める)・*RST 時と同じレート/オート・ゼロ・BUS トリガ。
// 演算(NULL・スケーリング・dB・MAX/MIN・コンパレータ)は測定値やサブ・ヘッダを変えるので必ず OFF にする。
public sealed record Adcmt7351ESettings
{
    // Bus:読むたびに *TRG で測定を始めるので、電流を変えた直後の 1 回目から新しい値が読める。
    // Immediate:読むだけで値が返るが、変更前の値が 2 回分まで混じる(docs/7351E実機確認.md)。
    // Manual・External は PC から測定を始められないので使わない。
    public Adcmt7351ETriggerSource TriggerSource { get; init; } = Adcmt7351ETriggerSource.Bus;
    public Adcmt7351ERange Range { get; init; } = Adcmt7351ERange.Range200mA;
    public Adcmt7351ESamplingRate SamplingRate { get; init; } = Adcmt7351ESamplingRate.Slow2;
    public Adcmt7351EAutoZero AutoZero { get; init; } = Adcmt7351EAutoZero.On;
    // スムージング(移動平均)の回数 2〜100。null で OFF。
    public int? SmoothingCount { get; init; }
    // null のときは本体の設定を変えない。
    public Adcmt7351ELineFrequency? LineFrequency { get; init; }

    public void Validate()
    {
        if (!Enum.IsDefined(Range)) throw new ArgumentException($"Range が不正です({Range})。");
        if (!Enum.IsDefined(SamplingRate)) throw new ArgumentException($"SamplingRate が不正です({SamplingRate})。");
        if (!Enum.IsDefined(AutoZero)) throw new ArgumentException($"AutoZero が不正です({AutoZero})。");
        if (SmoothingCount is int n && (n < 2 || n > 100))
            throw new ArgumentException($"SmoothingCount は 2〜100 にしてください({n})。");
        if (LineFrequency is { } lf && !Enum.IsDefined(lf))
            throw new ArgumentException($"LineFrequency が不正です({lf})。");
        if (TriggerSource is not (Adcmt7351ETriggerSource.Immediate or Adcmt7351ETriggerSource.Bus))
            throw new ArgumentException($"TriggerSource は Immediate か Bus にしてください({TriggerSource})。");
    }

    // 送る設定コマンド(送る順)。
    // 測定値の読み取りはトリガ・ソースとコンティニュー(INIC1:トリガごとに測定し続ける)に依存しているので、それも明示する。
    public IReadOnlyList<string> ToCommands()
    {
        var commands = new List<string>
        {
            "F5",                       // DCI
            "R" + (int)Range,
            "PR" + (int)SamplingRate,
            "AZ" + (int)AutoZero,
        };
        if (SmoothingCount is int n) { commands.Add("TI" + n); commands.Add("SM1"); }
        else commands.Add("SM0");
        commands.AddRange(["NL0", "SC0", "DB0", "MN0", "CO0"]);
        commands.Add("H1");             // 測定値にヘッダ(DCI_ 等)を付ける。読み取り側で DCI かを確かめるため
        if (LineFrequency is { } lf) commands.Add("LF" + (int)lf);
        commands.AddRange(["TRS" + (int)TriggerSource, "INIC1"]);
        return commands;
    }

    // 設定後に問い合わせて確かめる項目(問い合わせ, 期待する応答)。
    public IReadOnlyList<(string Query, string Expected)> ExpectedResponses()
    {
        var list = new List<(string, string)>
        {
            ("F?", "F5"),
            ("R?", "R" + (int)Range),
            ("PR?", "PR" + (int)SamplingRate),
            // Once は実行後に OFF へ戻る。
            ("AZ?", AutoZero == Adcmt7351EAutoZero.Once ? "AZ0" : "AZ" + (int)AutoZero),
            ("SM?", SmoothingCount is null ? "SM0" : "SM1"),
        };
        if (SmoothingCount is int n) list.Add(("TI?", "TI" + n));
        list.AddRange([("NL?", "NL0"), ("SC?", "SC0"), ("DB?", "DB0"), ("MN?", "MN0"), ("CO?", "CO0"), ("H?", "H1")]);
        if (LineFrequency is { } lf) list.Add(("LF?", "LF" + (int)lf));
        list.AddRange([("TRS?", "TRS" + (int)TriggerSource), ("INIC?", "INIC1")]);
        return list;
    }
}
