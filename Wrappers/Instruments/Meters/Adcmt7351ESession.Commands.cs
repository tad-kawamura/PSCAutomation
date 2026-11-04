using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PSCAutomation.Wrappers.Instruments;

// 7351E の設定・問い合わせ(ADC コマンド、取扱説明書 6.7.3)。
// USB 使用時の取説の注意:
//   ・設定コマンドを続けて送らず、1 つごとにクエリ(ERR? 等)を送って応答を受け取る
//   ・クエリは直前のコマンドから 20ms 以上空ける
//   ・機器の設定状態はクエリの後の読み出しで出力される
// 実機ではクエリに応答しないコマンドを送ると、その後の読み出しで測定値が返った(docs/7351E実機確認.md)。
// そのため応答の代わりに測定値が返った場合は読み直し、それでも来なければ例外にする。
// 本体の LANG=ADC で全クエリに応答することを 2026-11-03 に実機確認済み。
// 元: AS110NoiseTest Devices/Adcmt7351ESession.Commands.cs。通信を IInstrumentSession 経由にし、判定関数を public にした。
public sealed partial class Adcmt7351ESession
{
    // 取説は 20ms 以上。余裕を持たせる。
    private const int CommandGapMilliseconds = 30;
    // クエリの応答を待つ間に返ってきた測定値を読み捨てる上限。
    private const int MaxMeasurementsBeforeResponse = 3;
    // エラー・ログは最大 20 件(21 件目以降は Queue overflow 1 件に置き換わる)。
    private const int MaxErrorLogEntries = 21;

    private readonly Stopwatch _sinceLastWrite = Stopwatch.StartNew();

    // 状態表示用の問い合わせ一覧(名前, クエリ)。
    private static readonly (string Name, string Query)[] ConfigurationQueries =
    [
        ("ファンクション", "F?"),
        ("レンジ", "R?"),
        ("サンプリング・レート", "PR?"),
        ("表示桁数", "RE?"),
        ("オート・ゼロ", "AZ?"),
        ("スムージング", "SM?"),
        ("スムージング回数", "TI?"),
        ("NULL 演算", "NL?"),
        ("スケーリング演算", "SC?"),
        ("dB 演算", "DB?"),
        ("MAX・MIN 演算", "MN?"),
        ("コンパレータ演算", "CO?"),
        ("ヘッダ", "H?"),
        ("デリミタ", "DL?"),
        ("電源周波数", "LF?"),
        ("トリガ・ソース", "TRS?"),
        ("コンティニュー", "INIC?"),
        ("トリガ・ディレイ", "TRD?"),
    ];

    // 設定コマンドを 1 つ送り、ERR? でエラーがないことを確かめる。
    public void Command(string command)
    {
        Write(command);
        string response = Query("ERR?");
        int code = ParseErrorCode(response);
        if (code != 0)
            throw new InvalidOperationException($"7351E が「{command}」でエラーを返しました(応答:{response})。");
    }

    // クエリを送って応答を 1 行返す。
    public string Query(string query)
    {
        Write(query);
        var instrument = Instrument;
        for (int i = 0; i <= MaxMeasurementsBeforeResponse; i++)
        {
            string response;
            try { response = instrument.Read(); }
            catch (COMException ex)
            {
                throw new InvalidOperationException($"7351E から「{query}」の応答を読めません(通信タイムアウト等)。", ex);
            }
            if (!IsMeasurement(response)) return response;
            LastRawReading = response;
        }
        throw new InvalidOperationException(
            $"7351E が「{query}」に応答せず、測定値だけが返りました。" +
            "本体のコマンド言語が ADC か確認してください(MENU → 7 I/F → LANG)。");
    }

    // 電流読み取り用に設定し、問い合わせで反映を確かめる。
    // 設定前にエラー・ログに残っていた古いエラーを読み出して返す(表示・記録用)。
    // IMMEDIATE では設定前の測定が混じりうるので読み捨てる(BUS では不要)。
    public IReadOnlyList<string> ApplySettings(Adcmt7351ESettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        IReadOnlyList<string> oldErrors = DrainErrors();

        foreach (string command in settings.ToCommands())
            Command(command);
        TriggerSource = settings.TriggerSource;

        var mismatches = new List<string>();
        foreach (var (query, expected) in settings.ExpectedResponses())
        {
            string actual = Query(query);
            if (!SameSetting(expected, actual)) mismatches.Add($"{query} → {actual}(期待 {expected})");
        }
        if (mismatches.Count > 0)
            throw new InvalidOperationException("7351E の設定が反映されていません:" + string.Join("、", mismatches));

        DiscardStaleReadings();
        return oldErrors;
    }

    // 現在の設定を問い合わせる(変更はしない)。応答できない項目は「取得失敗」として返す。
    public IReadOnlyList<(string Name, string Query, string Response)> ReadConfiguration()
    {
        var result = new List<(string, string, string)>();
        foreach (var (name, query) in ConfigurationQueries)
        {
            string response;
            try { response = Query(query); }
            catch (InvalidOperationException ex) { response = "(取得失敗:" + ex.Message + ")"; }
            result.Add((name, query, response));
        }
        return result;
    }

    // エラー・ログを空になるまで読み出す(FIFO)。"+000,..." 以外の応答を古い順に返す。
    public IReadOnlyList<string> DrainErrors()
    {
        var errors = new List<string>();
        for (int i = 0; i < MaxErrorLogEntries; i++)
        {
            string response = Query("ERR?");
            if (ParseErrorCode(response) == 0) return errors;
            errors.Add(response);
        }
        throw new InvalidOperationException("7351E のエラー・ログを読み切れません:" + string.Join("、", errors));
    }

    // トリガ・ソースだけを変える。Manual・External は PC から測定を始められないので受け付けない。
    public void SetTriggerSource(Adcmt7351ETriggerSource source)
    {
        if (source is not (Adcmt7351ETriggerSource.Immediate or Adcmt7351ETriggerSource.Bus))
            throw new ArgumentOutOfRangeException(nameof(source), source, "Immediate か Bus にしてください。");
        Command("TRS" + (int)source);
        TriggerSource = source;
    }

    // 測定値を 1 回読んで捨てる(内容は問わない)。生の応答は LastRawReading に残る。
    public void DiscardReading() => ReadMeasurement();

    // 電流や設定を変えた後、変更前の値が混じりうる回数だけ読み捨てる。
    public void DiscardStaleReadings()
    {
        for (int i = 0; i < StaleReadingsAfterChange; i++) ReadMeasurement();
    }

    private IInstrumentSession Instrument => _instrument ?? throw new ObjectDisposedException(nameof(Adcmt7351ESession));

    private void Write(string command)
    {
        var instrument = Instrument;
        int wait = CommandGapMilliseconds - (int)_sinceLastWrite.ElapsedMilliseconds;
        if (wait > 0) Thread.Sleep(wait);
        try { instrument.Write(command); }
        catch (COMException ex)
        {
            throw new InvalidOperationException($"7351E へ「{command}」を送れません。", ex);
        }
        finally { _sinceLastWrite.Restart(); }
    }

    // ERR? の応答 "+000,"No error"" からエラー番号を取り出す。
    public static int ParseErrorCode(string response)
    {
        string text = response.Trim();
        int comma = text.IndexOf(',');
        string number = comma >= 0 ? text[..comma] : text;
        if (!int.TryParse(number, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int code))
            throw new InvalidOperationException($"7351E のエラー確認の応答を解釈できません(応答:{response})。");
        return code;
    }

    // "F5" と "F05" のように、英字部分が同じで数字の値が同じなら同じ設定とみなす。
    public static bool SameSetting(string expected, string actual)
    {
        Match e = SettingPattern().Match(expected.Trim());
        Match a = SettingPattern().Match(actual.Trim());
        if (e.Success && a.Success)
            return e.Groups["name"].Value.Equals(a.Groups["name"].Value, StringComparison.OrdinalIgnoreCase) &&
                   int.Parse(e.Groups["n"].Value, CultureInfo.InvariantCulture) ==
                   int.Parse(a.Groups["n"].Value, CultureInfo.InvariantCulture);
        return expected.Trim().Equals(actual.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsMeasurement(string response) => ReadingPattern().IsMatch(response.Trim());

    [GeneratedRegex(@"^(?<name>[A-Za-z]+)\s*(?<n>\d+)$")]
    private static partial Regex SettingPattern();
}
