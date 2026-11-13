using System.Globalization;
using System.Text;

namespace PSCAutomation.Apps.AS110AllCheck;

// 適用した校正値の記録。データ取りの保存先の「校正記録」フォルダーへ 1 件 1 ファイルで残す(2026-11-06、人の判断)。
// 校正はモードを選んだ直後(保存先を選ぶ前)に行うので、校正の適用と保存先の選択の両方がそろった時点で書く。
// exe の隣や %LOCALAPPDATA% には置かない。
// Reading は読み取り方など(2026-11-13 追加、SPEC「校正の自動読み取り」)。無ければ前と同じ中身。
internal sealed record CalibrationRecord(DateTimeOffset AppliedAt, As110Calibration Calibration,
    string Mode, string Hardware, string TemplateFileName, CalibrationReading? Reading = null)
{
    public void Validate()
    {
        if (AppliedAt == default || Calibration?.Current is null || Calibration.Pressure is null)
            throw new InvalidDataException("校正記録の適用日時または電流・圧力の値がありません。");
        Calibration.Validate();
    }

    // 人が読める日本語テキスト。校正値は入力した桁をそのまま残す(丸めない)。
    public string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine("AS110AllCheck 校正記録");
        text.AppendLine($"適用日時: {AppliedAt:yyyy/MM/dd HH:mm:ss} ({AppliedAt:zzz})");
        text.AppendLine($"モード: {Mode}");
        text.AppendLine($"本体: {Hardware}");
        text.AppendLine($"雛形: {TemplateFileName}");
        text.AppendLine("CH1 電流: " + Describe(Calibration.Current, "mA"));
        text.AppendLine("CH2 圧力: " + Describe(Calibration.Pressure, "kPa"));
        Reading?.AppendTo(text);
        return text.ToString();
    }

    private static string Describe(ScalingPoints p, string unit) =>
        $"低点 {F(p.Input1)} V = {F(p.Output1)} {unit}、高点 {F(p.Input2)} V = {F(p.Output2)} {unit}";
    private static string F(float value) => value.ToString("G", CultureInfo.InvariantCulture);
}

// 校正値の読み取り方(2026-11-13、人が承認)。校正記録に、読み取り方・目標の供給圧力・GC16 との通信確認からの経過時間・0 点の確かめの結果を書く。
// 手入力のときは経過時間・0 点の結果は書かない(GaugePort・SinceGaugeConfirmed・Zero が無い)。
// EditedAfterRead: GC16 で読み取ったあと、適用の前に人が 8 欄のどれかを手で直した。
internal sealed record CalibrationReading(bool ReadWithGauge, ProductModel? Model, string? GaugePort = null,
    TimeSpan? SinceGaugeConfirmed = null, string? Zero = null, bool EditedAfterRead = false)
{
    public static CalibrationReading Manual(ProductModel? model) => new(false, model);

    public string MethodText => !ReadWithGauge ? "手入力" :
        $"GC16 で読み取り（{GaugePort}）" + (EditedAfterRead ? "。そのあと手で直した" : "");

    public void AppendTo(StringBuilder text)
    {
        text.AppendLine("読み取り方: " + MethodText);
        text.AppendLine("目標の供給圧力: " + (Model is null ? "型式を選んでいない" : $"{Model.SupplyPressure} kPa（{Model.Name}）"));
        if (!ReadWithGauge) return;
        text.AppendLine("GC16 との通信確認からゼロ調整までの経過時間: " +
            (SinceGaugeConfirmed is { } elapsed ? $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒" : "不明"));
        text.AppendLine("0 点の確かめ: " + (Zero ?? "不明"));
    }
}

internal static class CalibrationRecordFile
{
    public const string FolderName = "校正記録";
    private static readonly UTF8Encoding Encoding = new(false, true);

    public static string FolderPath(string saveRoot) => Path.Combine(saveRoot, FolderName);

    // 保存先の「校正記録」フォルダーへ書き、書いたファイルのパスを返す。既存のファイルは上書きしない(同じ秒なら _2, _3 … を付ける)。
    // 書けないときは IOException / UnauthorizedAccessException をそのまま投げる(呼び出し側で設定完了にしない)。
    public static string Write(string saveRoot, CalibrationRecord record)
    {
        if (string.IsNullOrWhiteSpace(saveRoot)) throw new ArgumentException("保存先が選ばれていません。", nameof(saveRoot));
        record.Validate();
        string folder = FolderPath(saveRoot);
        Directory.CreateDirectory(folder);
        string baseName = $"校正_{record.AppliedAt.LocalDateTime:yyyy-MM-dd_HH-mm-ss}";
        byte[] bytes = Encoding.GetBytes(record.ToText());
        for (int suffix = 1; ; suffix++)
        {
            string path = Path.Combine(folder, suffix == 1 ? baseName + ".txt" : $"{baseName}_{suffix}.txt");
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                return path;
            }
            catch (IOException) when (File.Exists(path) && suffix < 100) { /* 同じ秒に 2 回適用したときは次の番号で残す。 */ }
        }
    }
}
