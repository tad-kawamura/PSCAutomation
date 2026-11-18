using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PSCAutomation.Apps.AS110AllCheck;

// 今日の校正値(2026-11-18、SPEC「段階 1 の直しと 1 日 1 回の流れ」。決まったこと 1・5・8、AI の案 4)。
// 校正はその日の最初に 1 回。exe の隣の「校正値」フォルダーに日付ごとのファイルを残し、その日はアプリを立ち上げ直しても校正しなくてよい。
// 前の日の値は呼び出さない(決まったこと 8)。日付の区切りは PC の日付(午前 0 時。夜勤は無い)。
// ReadingLines は読み取り方などの行(校正記録にそのまま写す)。
internal sealed partial record TodayCalibration(DateTimeOffset CalibratedAt, As110Calibration Calibration, string Hardware, IReadOnlyList<string> ReadingLines)
{
    private const string Header = "AS110AllCheck 今日の校正値";
    private const string TimeFormat = "yyyy/MM/dd HH:mm:ss";

    public bool IsFor(DateTime today) => CalibratedAt.LocalDateTime.Date == today.Date;

    public string CalibratedAtText => CalibratedAt.ToString(TimeFormat, CultureInfo.InvariantCulture) + $" ({CalibratedAt:zzz})";

    // 校正記録に足す行(校正した時刻と読み取り方など)。
    public IReadOnlyList<string> RecordLines => ["校正した時刻: " + CalibratedAtText, .. ReadingLines];

    public static TodayCalibration Create(DateTimeOffset calibratedAt, As110Calibration calibration, string hardware, CalibrationReading reading) =>
        new(calibratedAt, calibration, hardware, reading.Lines().ToArray());

    public string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine(Header);
        text.AppendLine("校正した時刻: " + CalibratedAtText);
        text.AppendLine("本体: " + Hardware);
        text.AppendLine("CH1 電流: " + Describe(Calibration.Current, "mA"));
        text.AppendLine("CH2 圧力: " + Describe(Calibration.Pressure, "kPa"));
        foreach (string line in ReadingLines) text.AppendLine(line);
        return text.ToString();
    }

    // ToText の中身を読み戻す。8 項目と時刻が読めなければ InvalidDataException。
    public static TodayCalibration Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).ToList();
        if (lines.Count == 0 || lines[0].TrimStart('﻿') != Header) throw new InvalidDataException("今日の校正値のファイルではありません。");
        string Value(string key) =>
            lines.FirstOrDefault(l => l.StartsWith(key + ": ", StringComparison.Ordinal))?[(key.Length + 2)..]
            ?? throw new InvalidDataException($"「{key}」の行がありません。");
        string timeText = Value("校正した時刻");
        var timeMatch = TimePattern().Match(timeText);
        if (!timeMatch.Success ||
            !DateTimeOffset.TryParseExact(timeMatch.Groups["t"].Value + " " + timeMatch.Groups["z"].Value, TimeFormat + " zzz",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var calibratedAt))
            throw new InvalidDataException($"校正した時刻を読めません（{timeText}）。");
        var calibration = new As110Calibration(ParsePoints(Value("CH1 電流"), "mA"), ParsePoints(Value("CH2 圧力"), "kPa"));
        calibration.Validate();
        int pressureLine = lines.FindIndex(l => l.StartsWith("CH2 圧力: ", StringComparison.Ordinal));
        var reading = lines.Skip(pressureLine + 1).Where(l => l.Length > 0).ToArray();
        return new TodayCalibration(calibratedAt, calibration, Value("本体"), reading);
    }

    private static ScalingPoints ParsePoints(string text, string unit)
    {
        var match = PointsPattern().Match(text);
        if (!match.Success || match.Groups["u1"].Value != unit || match.Groups["u2"].Value != unit)
            throw new InvalidDataException($"校正値を読めません（{text}）。");
        float Number(string group) =>
            float.TryParse(match.Groups[group].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value)
                ? value : throw new InvalidDataException($"校正値の数を読めません（{text}）。");
        return new ScalingPoints(Number("a"), Number("b"), Number("c"), Number("d"));
    }

    private static string Describe(ScalingPoints p, string unit) =>
        $"低点 {F(p.Input1)} V = {F(p.Output1)} {unit}、高点 {F(p.Input2)} V = {F(p.Output2)} {unit}";
    private static string F(float value) => value.ToString("G", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^(?<t>\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}) \((?<z>[+-]\d{2}:\d{2})\)$")]
    private static partial Regex TimePattern();

    [GeneratedRegex(@"^低点 (?<a>\S+) V = (?<b>\S+) (?<u1>\S+)、高点 (?<c>\S+) V = (?<d>\S+) (?<u2>\S+)$")]
    private static partial Regex PointsPattern();
}

// 今日の校正値のファイル(exe の隣の「校正値\校正値_yyyy-MM-dd.txt」。AI の案 4)。やり直したら同じ日のファイルを上書きする。前の日のファイルは消さない。
internal sealed class TodayCalibrationStore(string folder)
{
    public const string FolderName = "校正値";
    private static readonly UTF8Encoding Encoding = new(false, true);

    public static TodayCalibrationStore Default => new(Path.Combine(AppContext.BaseDirectory, FolderName));

    public string Folder => folder;

    public string PathFor(DateTime day) => Path.Combine(folder, $"校正値_{day:yyyy-MM-dd}.txt");

    // 書いたファイルのパスを返す。書けなければ IOException / UnauthorizedAccessException(呼び出し側で知らせ、アプリを閉じるまではその値で進める)。
    public string Write(TodayCalibration calibration)
    {
        Directory.CreateDirectory(folder);
        string path = PathFor(calibration.CalibratedAt.LocalDateTime);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, calibration.ToText(), Encoding);
        File.Move(temporary, path, overwrite: true);
        return path;
    }

    // その日のファイルが無ければ null。あっても読めない・その日のものでなければ InvalidDataException。
    public TodayCalibration? Read(DateTime today)
    {
        string path = PathFor(today);
        if (!File.Exists(path)) return null;
        string text;
        try { text = File.ReadAllText(path, Encoding); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new InvalidDataException("今日の校正値のファイルを読めません：" + ex.Message, ex);
        }
        var calibration = TodayCalibration.Parse(text);
        if (!calibration.IsFor(today)) throw new InvalidDataException($"今日の校正値のファイルの日付が今日ではありません（{calibration.CalibratedAtText}）。");
        return calibration;
    }
}
