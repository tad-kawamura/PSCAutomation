using System.Text;

namespace PSCAutomation.Apps.AS100201SwitchController;

internal enum As100201LedgerEventKind
{
    Measurement,
    Operation,
    Done,
    Undo,
    // PC_go / CR_go で WF1973 を出し直した(2026-11-12 追加。SPEC_測定中の取り込み.md「押した瞬間の記録」)。
    Pressed
}

// CSV 台帳の 1 行(1 出来事)。列は SPEC_調整操作の記録.md「CSV 台帳」の案のとおり。
// 空の列は空文字のまま出す(列の位置を変えない)。既存の行は書き換えず、追記だけ(取消も「取消の行」を足す)。
internal sealed record As100201LedgerRow(DateTime Time, As100201LedgerEventKind Kind)
{
    public static readonly string[] Columns =
        ["日時", "種類", "側", "位相", "停止のきっかけ", "操作", "方向", "程度", "厚さ", "ファイル", "備考"];

    public string Side { get; init; } = "";
    public string Phase { get; init; } = "";
    public string StopTrigger { get; init; } = "";
    public string Operation { get; init; } = "";
    public string Direction { get; init; } = "";
    public string Degree { get; init; } = "";
    public string Thickness { get; init; } = "";
    public string File { get; init; } = "";
    public string Note { get; init; } = "";

    public string KindText => Kind switch
    {
        As100201LedgerEventKind.Measurement => "測定",
        As100201LedgerEventKind.Operation => "操作",
        As100201LedgerEventKind.Done => "完了",
        As100201LedgerEventKind.Pressed => "押した",
        _ => "取消"
    };

    public string TimeText => Time.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    public string[] ToFields() =>
        [TimeText, KindText, Side, Phase, StopTrigger, Operation, Direction, Degree, Thickness, File, Note];

    public string ToCsvLine() => string.Join(",", ToFields().Select(Quote));

    public static string HeaderLine => string.Join(",", Columns);

    // カンマ・引用符・改行を含む値だけ引用符で囲む(RFC 4180)。
    public static string Quote(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}

// 台帳への追記。ファイル版と、機器・ファイルなしの模擬を差し替えられるようにする。
internal interface IAs100201LearningLedger
{
    // 人に見せる置き場(パス)。
    string Location { get; }
    // 1 行追記する。書けなければ例外(IOException / UnauthorizedAccessException など)。
    void Append(As100201LedgerRow row);
    // 書ける状態か確かめる(開いて閉じるだけ)。書けなければ例外。「再試行」で書けるようになったかを見るのに使う。
    void EnsureWritable();
}

// CSV ファイルの台帳。UTF-8(BOM 付き)で、新しいファイルの先頭にだけヘッダー行を書く。
// BOM は Excel でそのまま開いたときに文字化けしないため(列の確定・BOM の有無は残課題。人が決めたら変える)。
// 追記は共有読み取りを許す(人が Excel で開いていても、Excel が占有していなければ書ける。占有していれば例外)。
internal sealed class As100201CsvLedger : IAs100201LearningLedger
{
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public As100201CsvLedger(string path) => Location = path;

    public string Location { get; }

    public void Append(As100201LedgerRow row)
    {
        using var stream = new FileStream(Location, FileMode.Append, FileAccess.Write, FileShare.Read);
        bool empty = stream.Position == 0;
        var text = new StringBuilder();
        if (empty) text.Append(As100201LedgerRow.HeaderLine).Append("\r\n");
        text.Append(row.ToCsvLine()).Append("\r\n");
        if (empty) stream.Write(Utf8WithBom.GetPreamble());
        stream.Write(Utf8NoBom.GetBytes(text.ToString()));
        stream.Flush(flushToDisk: true);
    }

    public void EnsureWritable()
    {
        string? directory = Path.GetDirectoryName(Location);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            throw new DirectoryNotFoundException("保存先フォルダーがありません: " + directory);
        using var stream = new FileStream(Location, FileMode.Append, FileAccess.Write, FileShare.Read);
    }
}
