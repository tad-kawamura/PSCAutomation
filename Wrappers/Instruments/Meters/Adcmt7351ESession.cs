using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PSCAutomation.Wrappers.Instruments;

// ADCMT 7351E(DMM)から実電流を読む。USBTMC として VISA で通信する。
// 返り値は "DCI_ +006.027E-03" 形式(単位 A)。設定・問い合わせは ADC コマンド(SCPI は不可)。
// ApplySettings(Adcmt7351ESession.Commands.cs)で DCI・BUS トリガ等に設定してから読む。
//   BUS トリガ:ReadMilliamps のたびに *TRG で測定を始めて読む(約 400ms/回、SLOW2)。変更前の値は混じらない。
//   IMMEDIATE :読むだけで値が返るが、本体に溜まった値と測定途中の値の 2 回分は変更前の値が混じる。
// DCI 以外の値は電流として扱わず例外にする。詳細は docs/7351E実機確認.md。
// 元: AS110NoiseTest Devices/Adcmt7351ESession.cs。
// モノレポで通信を IInstrumentSession 経由にし、アプリ側の interface(ICurrentMeter)の実装を外した(アプリ側で包む)。
// 送るコマンドと順番は元のまま。ParseMilliamps などの判定関数は public にした(元は internal。アプリの検査ツールが使う)。
public sealed partial class Adcmt7351ESession : IDisposable
{
    // ベンダー ID 0x1334(ADC)/製品 ID 0x0204(7351E)。シリアル番号は問わない。
    public const string DefaultResourcePattern = "USB?*::0x1334::0x0204::?*::INSTR";

    private IInstrumentSession? _instrument;

    // 接続済みの機器を受け取る(通常は Connect を使う。模擬の差し込みにも使う)。
    public Adcmt7351ESession(IInstrumentSession instrument)
    {
        _instrument = instrument ?? throw new ArgumentNullException(nameof(instrument));
        Address = instrument.Address;
        Identity = instrument.Identity;
    }

    public string Address { get; }
    public string Identity { get; }
    // 直前に読んだ応答そのもの(記録・不具合調査用)。
    public string LastRawReading { get; private set; } = "";
    // 本体のトリガ・ソース(このセッションで設定したもの)。接続直後は前面パネルの通常状態 IMMEDIATE とみなす。
    public Adcmt7351ETriggerSource TriggerSource { get; private set; } = Adcmt7351ETriggerSource.Immediate;

    // 電流を変えた後、変更前の値が混じりうる読み取り回数(2026-11-03 実機確認)。
    // IMMEDIATE は本体に溜まった 1 回分 + 変更をまたいで測定中だった 1 回分。
    public int StaleReadingsAfterChange => TriggerSource == Adcmt7351ETriggerSource.Bus ? 0 : 2;

    // resource を省略すると USB 接続の 7351E を探す。
    public static Adcmt7351ESession Connect(string? resource = null, int timeoutMs = 5000) =>
        new(VisaInstrumentSession.Connect(
            identity => identity.Contains("7351E", StringComparison.OrdinalIgnoreCase),
            _ => { },
            "7351E を確認できません。電源、USB、VISA を確認してください。",
            new VisaSearchOptions
            {
                ResourcePattern = DefaultResourcePattern,
                Resource = resource,
                TimeoutMilliseconds = timeoutMs,
                ReportCommunicationErrors = true,
                RejectedIdentityName = "7351E",
            }));

    public double ReadMilliamps() => ParseMilliamps(ReadMeasurement());

    // 測定値を 1 回読む(内容は確かめない)。BUS トリガなら *TRG で測定を始めてから読む。
    private string ReadMeasurement()
    {
        var instrument = Instrument;
        if (TriggerSource == Adcmt7351ETriggerSource.Bus) Write("*TRG");
        try { LastRawReading = instrument.Read(); }
        catch (COMException ex)
        {
            throw new InvalidOperationException(
                "7351E から測定値を読めません(通信タイムアウト等)。" +
                "前の実行が異常終了した場合、本体のトリガ・ソースが BUS のままの可能性があります(ApplySettings で設定し直す)。", ex);
        }
        return LastRawReading;
    }

    // "DCI_ +006.027E-03" → 6.027(mA)。
    // 形式は取説 6.7.2:メイン・ヘッダ 3 文字 + サブ・ヘッダ 1 文字 + スペース + 値。
    // サブ・ヘッダ "_" が通常の測定値。O=レンジ・オーバー、E=演算エラー、それ以外は演算結果なので電流として使わない。
    // オーバーロード時の値は ±9.99999E+37(演算エラーは E+36・E+35)。
    public static double ParseMilliamps(string raw)
    {
        Match match = ReadingPattern().Match(raw.Trim());
        if (!match.Success)
            throw new FormatException($"7351E の応答を解釈できません(応答:{raw})。");
        string function = match.Groups["func"].Value;
        if (!function.Equals("DCI", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"7351E が DCI(直流電流)になっていません(応答:{raw})。前面パネルで DCI に設定してください。");
        string sub = match.Groups["sub"].Value.ToUpperInvariant();
        if (sub == "O")
            throw new InvalidOperationException($"7351E がレンジ・オーバーです(応答:{raw})。レンジを確認してください。");
        if (sub == "E")
            throw new InvalidOperationException($"7351E が演算エラーを返しました(応答:{raw})。");
        if (sub != "_")
            throw new InvalidOperationException(
                $"7351E の演算機能が有効です(サブ・ヘッダ {sub}、応答:{raw})。演算を OFF にしてください。");
        double amps = double.Parse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (Math.Abs(amps) >= 9.9e34)
            throw new InvalidOperationException($"7351E がオーバーロードです(応答:{raw})。");
        return amps * 1000.0;
    }

    [GeneratedRegex(@"^(?<func>[A-Za-z][A-Za-z0-9]{2})(?<sub>[A-Za-z_])\s*(?<value>[+-]?\d+(?:\.\d+)?E[+-]\d+)$")]
    private static partial Regex ReadingPattern();

    public void Dispose()
    {
        if (_instrument is null) return;
        // BUS のままだと前面パネルの表示が止まるので、IMMEDIATE に戻してから閉じる。
        if (TriggerSource != Adcmt7351ETriggerSource.Immediate)
        {
            try { SetTriggerSource(Adcmt7351ETriggerSource.Immediate); }
            catch (InvalidOperationException) { }
        }
        _instrument.Dispose();
        _instrument = null;
    }
}
