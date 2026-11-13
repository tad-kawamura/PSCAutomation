using System.Globalization;
using System.IO.Ports;
using System.Text.RegularExpressions;

namespace PSCAutomation.Wrappers.Instruments;

// GC16 精密デジタル圧力計を RS-232C で操作する(2026-11-13 追加。最初に使うのは AS110AllCheck の校正の自動読み取り)。
// 説明書 TY-GC15-004 Ver.2(2021 年版)の「10. 外部インターフェース」による。社内の本体は 2005 年製で、説明書のコマンド・返事の形で動くかは実機で未確認。
//
// - 通信: 既定 9600 bps・8 ビット・パリティなし・ストップ 1・ハードウェアハンドシェイク(RTS/CTS)。本体の設定 S-1 を「rS」にしておく(出荷時は「non」)
// - コマンドは「ヘッダー,パラメータ」+ CR(0DH)だけ。CR+LF にしない。返事を受け取ってから次を送る。返事の終わりは CR とし、続く LF・空行は読み捨てる
// - 前面キーで操作する状態(キー入力状態)と、通信で操作する状態(コマンド入力状態。REMOTE が点灯し、電源キー以外の前面キーが効かない)がある。
//   圧力の読み取り(MP)・ゼロ調整(ZA)などは EnterRemote(RM,1)の後で使う。ExitRemote(RM,0)か電源の入れ直しで戻る
// - Dispose は、この接続で RM,1 にしていれば RM,0 を試してから閉じる(前面キーが効かないまま残さないため)
// - COM ポートは、状態を変えない問い合わせ(既定 RM,Q)への返事で見分ける(Find)。変換ケーブルの種類・識別番号には頼らない
// - 値(先に試す COM・除く COM・待ち時間)はアプリが渡す。この箱は製品の既定値を持たない
public sealed class Gc16Session : IDisposable
{
    public const int DefaultBaudRate = 9600;
    public const string DefaultProbeCommand = "RM,Q";
    private static readonly Regex ErrorReply = new(@"^ER\d$", RegexOptions.CultureInvariant);
    private static readonly Regex RemoteStateReply = new(@"^RM,[01]$", RegexOptions.CultureInvariant);

    private ISerialLineChannel? _channel;
    private Gc16PressureUnit? _unit;

    internal Gc16Session(ISerialLineChannel channel, int commandTimeoutMilliseconds)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        PortName = channel.PortName;
        CommandTimeoutMilliseconds = commandTimeoutMilliseconds;
    }

    public string PortName { get; }
    // この接続で RM,1(コマンド入力状態)にしたか。
    public bool IsRemote { get; private set; }
    public int CommandTimeoutMilliseconds { get; set; }

    // 既定の問い合わせ(RM,Q)への正しい返事か(RM,0 か RM,1)。
    public static bool IsRemoteStateReply(string reply) => RemoteStateReply.IsMatch(reply.Trim());

    // 試す順の COM ポート: 先に試す番号(あれば)→ 残りを番号順(COM2 は COM10 より前)。除くポートは入れない。大文字小文字は区別しない。
    public static IReadOnlyList<string> OrderCandidates(IEnumerable<string> portNames, Gc16SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(portNames);
        ArgumentNullException.ThrowIfNull(options);
        var excluded = new HashSet<string>(options.ExcludedPorts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var ports = portNames.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(p => !excluded.Contains(p))
            .OrderBy(PortNumber).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        string? preferred = options.PreferredPort?.Trim();
        int index = string.IsNullOrEmpty(preferred) ? -1 : ports.FindIndex(p => p.Equals(preferred, StringComparison.OrdinalIgnoreCase));
        if (index > 0)
        {
            string first = ports[index];
            ports.RemoveAt(index);
            ports.Insert(0, first);
        }
        return ports;
    }

    // この PC の COM ポートから GC16 を探して接続する。状態は変えない(RM,1 は送らない)。
    // 見つからなければ InvalidOperationException(試したポートと理由を付ける)。
    public static Gc16Session Find(Gc16SearchOptions? options = null)
    {
        options ??= new Gc16SearchOptions();
        return Find(options, SerialPort.GetPortNames(), (name, timeout) => SerialPortLineChannel.Open(name, options.BaudRate, timeout));
    }

    // 探す流れ本体(テストで通信を模擬に差し替える)。
    internal static Gc16Session Find(Gc16SearchOptions options, IEnumerable<string> portNames, Func<string, int, ISerialLineChannel> open)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var candidates = OrderCandidates(portNames, options);
        var reasons = new List<string>();
        foreach (string name in candidates)
        {
            ISerialLineChannel? channel = null;
            try
            {
                channel = open(name, options.ProbeTimeoutMilliseconds);
                string reply = Exchange(channel, options.ProbeCommand, options.ProbeTimeoutMilliseconds);
                if ((options.IsProbeReply ?? IsRemoteStateReply)(reply))
                {
                    var session = new Gc16Session(channel, options.CommandTimeoutMilliseconds);
                    channel = null;
                    return session;
                }
                reasons.Add($"{name}: 返事が違う({Describe(reply)})");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException or ArgumentException)
            {
                reasons.Add($"{name}: {ex.Message}");
            }
            finally
            {
                channel?.Dispose();
            }
        }
        string detail = candidates.Count == 0 ? "試せる COM ポートがありません。" : string.Join(" / ", reasons);
        throw new InvalidOperationException(
            "GC16 が見つかりません。電源・変換ケーブル・本体の通信設定(S-1 が rS)を確認してください。(" + detail + ")");
    }

    // コマンドを送り、返事を返す(前後の空白を除く)。ER0〜ER3 の返事は Gc16Exception。返事が来なければ TimeoutException。
    public string Query(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Contains('\r') || command.Contains('\n'))
            throw new ArgumentException("GC16 のコマンドは 1 行で、CR・LF を含めないでください。", nameof(command));
        string reply = Exchange(Channel, command, CommandTimeoutMilliseconds);
        if (ErrorReply.IsMatch(reply))
            throw new Gc16Exception($"GC16 がコマンド {command} にエラー {reply}({ErrorMeaning(reply)})を返しました。", reply);
        return reply;
    }

    // 今の入力状態を問い合わせる(RM,Q)。true = コマンド入力状態(REMOTE)。状態は変えない。
    public bool QueryRemoteState()
    {
        string reply = Query("RM,Q");
        if (!IsRemoteStateReply(reply)) throw new Gc16Exception($"GC16 の入力状態の返事を読めません({Describe(reply)})。", reply);
        return reply.EndsWith('1');
    }

    // コマンド入力状態にする(RM,1)。電源キー以外の前面キーが効かなくなる。
    public void EnterRemote()
    {
        ExpectOk("RM,1");
        IsRemote = true;
    }

    // キー入力状態に戻す(RM,0)。
    public void ExitRemote()
    {
        ExpectOk("RM,0");
        IsRemote = false;
    }

    // 圧力表示の値(表示している単位のまま)。MP の返事「MP,値」の値を数にする。
    public double ReadPressure()
    {
        string reply = Query("MP");
        return ParsePressureReply(reply);
    }

    // 表示している圧力の単位(PU,Q)。読んだ単位は ReadPressureKilopascals が使う。
    public Gc16PressureUnit ReadUnit()
    {
        string reply = Query("PU,Q");
        _unit = ParseUnitReply(reply);
        return _unit.Value;
    }

    // 圧力を kPa で返す。単位はこの接続で最初に 1 回だけ問い合わせる(本体の単位を変えたら ReadUnit で読み直す)。
    // ゲージ圧の Pa・kPa・MPa だけ換算する。スケーリング(U1・U2)と絶対圧(kPa abs・MPa abs)は Gc16Exception。
    public double ReadPressureKilopascals()
    {
        var unit = _unit ?? ReadUnit();
        double value = ReadPressure();
        return unit switch
        {
            Gc16PressureUnit.Pascal => value / 1000.0,
            Gc16PressureUnit.Kilopascal => value,
            Gc16PressureUnit.Megapascal => value * 1000.0,
            _ => throw new Gc16Exception($"GC16 の圧力の単位が kPa に換算できない設定です({unit})。本体の単位を kPa か MPa にしてください。", "")
        };
    }

    // 圧力のゼロ調整(ZA)。範囲外(レンジの約 ±2%)は本体が ER3 を返す(Gc16Exception)。
    public void ZeroAdjust() => ExpectOk("ZA");

    internal static double ParsePressureReply(string reply)
    {
        string text = reply.Trim();
        if (!text.StartsWith("MP,", StringComparison.Ordinal))
            throw new Gc16Exception($"GC16 の圧力の返事を読めません({Describe(reply)})。", reply);
        string number = text[3..].Replace(" ", "", StringComparison.Ordinal);
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
            throw new Gc16Exception($"GC16 の圧力の返事を数にできません({Describe(reply)})。レンジを超えている(OVER)かもしれません。", reply);
        return value;
    }

    internal static Gc16PressureUnit ParseUnitReply(string reply)
    {
        string text = reply.Trim();
        if (!text.StartsWith("PU,", StringComparison.Ordinal))
            throw new Gc16Exception($"GC16 の単位の返事を読めません({Describe(reply)})。", reply);
        return text[3..].Trim().ToUpperInvariant() switch
        {
            "PA" => Gc16PressureUnit.Pascal,
            "KP" => Gc16PressureUnit.Kilopascal,
            "MP" => Gc16PressureUnit.Megapascal,
            "U1" => Gc16PressureUnit.Scaling1,
            "U2" => Gc16PressureUnit.Scaling2,
            "AK" => Gc16PressureUnit.KilopascalAbsolute,
            "AM" => Gc16PressureUnit.MegapascalAbsolute,
            _ => throw new Gc16Exception($"GC16 の単位の返事を読めません({Describe(reply)})。", reply)
        };
    }

    private void ExpectOk(string command)
    {
        string reply = Query(command);
        if (!reply.Equals("OK", StringComparison.Ordinal))
            throw new Gc16Exception($"GC16 がコマンド {command} に OK を返しません({Describe(reply)})。", reply);
    }

    // 古い受信を捨ててから送り、返事を 1 行待つ。
    private static string Exchange(ISerialLineChannel channel, string command, int timeoutMilliseconds)
    {
        channel.DiscardInput();
        channel.Write(command + "\r");
        return channel.ReadLine(timeoutMilliseconds).Trim();
    }

    private static string ErrorMeaning(string code) => code switch
    {
        "ER0" => "コマンドの相違",
        "ER1" => "パラメータの相違",
        "ER2" => "モードの相違",
        "ER3" => "設定・調整範囲外",
        _ => "不明"
    };

    private static string Describe(string reply) => reply.Length == 0 ? "空" : "「" + reply + "」";

    private static int PortNumber(string name)
    {
        var match = Regex.Match(name, @"(\d+)$");
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : int.MaxValue;
    }

    private ISerialLineChannel Channel => _channel ?? throw new ObjectDisposedException(nameof(Gc16Session));

    public void Dispose()
    {
        var channel = _channel;
        if (channel is null) return;
        if (IsRemote)
        {
            // 前面キーが効かないまま手放さないよう、RM,0 を試す(通信できなくても閉じる)。
            try { Exchange(channel, "RM,0", Math.Min(CommandTimeoutMilliseconds, 1000)); }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException) { }
            IsRemote = false;
        }
        channel.Dispose();
        _channel = null;
    }
}

// GC16 の探し方。
public sealed class Gc16SearchOptions
{
    // 先に試す COM ポート(前回見つけた番号など)。
    public string? PreferredPort { get; init; }
    // 試さない COM ポート(マイコンなど、ほかの機器がつながっているポート)。このポートには何も送らない。
    public IReadOnlyCollection<string> ExcludedPorts { get; init; } = Array.Empty<string>();
    // 1 ポートの問い合わせを諦めるまでの時間。
    public int ProbeTimeoutMilliseconds { get; init; } = 1000;
    // 見つけたあとのコマンドの返事を待つ時間。
    public int CommandTimeoutMilliseconds { get; init; } = 3000;
    // 本体の設定 S-1 で速度を変えたときだけ指定する。
    public int BaudRate { get; init; } = Gc16Session.DefaultBaudRate;
    // 見分けるための問い合わせ。状態を変えないものにする(既定 RM,Q)。
    public string ProbeCommand { get; init; } = Gc16Session.DefaultProbeCommand;
    // 問い合わせの返事が GC16 のものか(既定は RM,0 / RM,1)。
    public Func<string, bool>? IsProbeReply { get; init; }

    internal void Validate()
    {
        if (ProbeTimeoutMilliseconds <= 0 || CommandTimeoutMilliseconds <= 0 || BaudRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(ProbeTimeoutMilliseconds), "待ち時間と速度は正の数にしてください。");
        if (string.IsNullOrWhiteSpace(ProbeCommand) || ProbeCommand.Contains('\r') || ProbeCommand.Contains('\n'))
            throw new ArgumentException("見分けるための問い合わせは 1 行のコマンドにしてください。", nameof(ProbeCommand));
    }
}

// GC16 が表示している圧力の単位(PU,Q の返事)。
public enum Gc16PressureUnit
{
    Pascal,
    Kilopascal,
    Megapascal,
    Scaling1,
    Scaling2,
    KilopascalAbsolute,
    MegapascalAbsolute,
}

// GC16 のエラーの返事(ER0〜ER3)や、読めない返事。
public sealed class Gc16Exception(string message, string reply) : IOException(message)
{
    public string Reply { get; } = reply;
}

// 1 行ずつ送受信する通信(GC16 の探索・操作をテストで模擬に差し替えるため)。
internal interface ISerialLineChannel : IDisposable
{
    string PortName { get; }
    void DiscardInput();
    // text はそのまま送る(終わりの CR は呼び出し側が付ける)。
    void Write(string text);
    // CR までを 1 行として返す(LF・空行は読み捨てる)。来なければ TimeoutException。
    string ReadLine(int timeoutMilliseconds);
}

internal sealed class SerialPortLineChannel : ISerialLineChannel
{
    private readonly SerialPort _port;

    private SerialPortLineChannel(SerialPort port) => _port = port;

    public string PortName => _port.PortName;

    public static SerialPortLineChannel Open(string portName, int baudRate, int timeoutMilliseconds)
    {
        var port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.RequestToSend,
            NewLine = "\r",
            Encoding = System.Text.Encoding.ASCII,
            ReadTimeout = timeoutMilliseconds,
            // CTS が来ない(ケーブル違い・相手がいない)と送信が止まるので、送信にも期限を付ける。
            WriteTimeout = timeoutMilliseconds,
        };
        try
        {
            port.Open();
            return new SerialPortLineChannel(port);
        }
        catch
        {
            port.Dispose();
            throw;
        }
    }

    public void DiscardInput() => _port.DiscardInBuffer();

    public void Write(string text) => _port.Write(text);

    public string ReadLine(int timeoutMilliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (true)
        {
            int remaining = (int)Math.Ceiling((deadline - DateTime.UtcNow).TotalMilliseconds);
            if (remaining <= 0) throw new TimeoutException("GC16 から返事がありません。");
            _port.ReadTimeout = remaining;
            string line = _port.ReadLine().Trim('\n', '\r', ' ', '\0');
            if (line.Length > 0) return line;
        }
    }

    public void Dispose() => _port.Dispose();
}
