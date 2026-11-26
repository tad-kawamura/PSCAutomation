using System.Text;

namespace PSCAutomation.Apps.AS100201SwitchController;

internal enum As100201LedgerEventKind
{
    Measurement,
    Operation,
    Done,
    Undo,
    // PC_go / CR_go で WF1973 を出し直した(2026-11-12 追加。SPEC_測定中の取り込み.md「押した瞬間の記録」)。
    Pressed,
    // PC の画面でシリアル番号を決めた(2026-11-26 追加。SPEC_調整操作の記録.md「シリアル番号で管理する」)。
    SerialSet
}

// CSV 台帳の 1 行(1 出来事)。列は SPEC_調整操作の記録.md「CSV 台帳」の案のとおり。
// 空の列は空文字のまま出す(列の位置を変えない)。既存の行は書き換えず、追記だけ(取消も「取消の行」を足す)。
// 2026-11-26: 「日時」の次に「シリアル番号」の列を足した(全部の行に、その時点の番号が入る)。
internal sealed record As100201LedgerRow(DateTime Time, As100201LedgerEventKind Kind)
{
    public static readonly string[] Columns =
        ["日時", "シリアル番号", "種類", "側", "位相", "停止のきっかけ", "操作", "方向", "程度", "厚さ", "ファイル", "備考"];

    public string SerialNumber { get; init; } = "";
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
        As100201LedgerEventKind.SerialSet => "番号",
        _ => "取消"
    };

    public string TimeText => Time.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    public string[] ToFields() =>
        [TimeText, SerialNumber, KindText, Side, Phase, StopTrigger, Operation, Direction, Degree, Thickness, File, Note];

    public string ToCsvLine() => string.Join(",", ToFields().Select(Quote));

    public static string HeaderLine => string.Join(",", Columns);

    // 台帳の 1 行(ヘッダー以外)を読み戻す。列の数が合わない・日時や種類が読めない行は null。
    public static As100201LedgerRow? FromFields(IReadOnlyList<string> fields)
    {
        if (fields.Count != Columns.Length) return null;
        if (!DateTime.TryParseExact(fields[0], "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out DateTime time)) return null;
        As100201LedgerEventKind? kind = fields[2] switch
        {
            "測定" => As100201LedgerEventKind.Measurement,
            "操作" => As100201LedgerEventKind.Operation,
            "完了" => As100201LedgerEventKind.Done,
            "取消" => As100201LedgerEventKind.Undo,
            "押した" => As100201LedgerEventKind.Pressed,
            "番号" => As100201LedgerEventKind.SerialSet,
            _ => null
        };
        if (kind is not As100201LedgerEventKind k) return null;
        return new As100201LedgerRow(time, k)
        {
            SerialNumber = fields[1],
            Side = fields[3],
            Phase = fields[4],
            StopTrigger = fields[5],
            Operation = fields[6],
            Direction = fields[7],
            Degree = fields[8],
            Thickness = fields[9],
            File = fields[10],
            Note = fields[11]
        };
    }

    // CSV の文字列を行と欄に分ける(RFC 4180。引用符の中のカンマ・改行・"" を扱う)。
    public static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        bool any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; any = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); any = true; break;
                case '\r': break;
                case '\n':
                    row.Add(field.ToString()); field.Clear();
                    rows.Add(row); row = new List<string>(); any = false;
                    break;
                default: field.Append(c); any = true; break;
            }
        }
        if (any || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }

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
    // 今までの行を読み戻す(シリアル番号の続きを数え直すため、2026-11-26)。台帳が無ければ空。
    // 読めない・列の形が違うときは例外(IOException など)。
    IReadOnlyList<As100201LedgerRow> ReadRows();
}

// 台帳の列の形が今の作りと違う(列を足す前の台帳など)。形の違う行を混ぜないため、書き足さずに止める(2026-11-26)。
internal sealed class As100201LedgerFormatException(string message) : IOException(message);

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
        CheckHeader();
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
        CheckHeader();
        using var stream = new FileStream(Location, FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    public IReadOnlyList<As100201LedgerRow> ReadRows()
    {
        if (!File.Exists(Location)) return [];
        string text = ReadAllText();
        var lines = As100201LedgerRow.ParseCsv(text);
        if (lines.Count == 0) return [];
        CheckHeaderText(string.Join(",", lines[0]));
        var rows = new List<As100201LedgerRow>();
        for (int i = 1; i < lines.Count; i++)
        {
            if (lines[i].Count == 1 && lines[i][0].Length == 0) continue;
            rows.Add(As100201LedgerRow.FromFields(lines[i])
                ?? throw new As100201LedgerFormatException($"台帳の {i + 1} 行目を読めません（列の数か日時・種類の形が違います）：{Location}"));
        }
        return rows;
    }

    // 人が Excel で開いていても読めるよう、書き込みを許して開く。
    private string ReadAllText()
    {
        using var stream = new FileStream(Location, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    // 中身のある台帳は、先頭のヘッダー行が今の列と同じときだけ書き足す。
    private void CheckHeader()
    {
        if (!File.Exists(Location)) return;
        string first;
        using (var stream = new FileStream(Location, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true))
            first = reader.ReadLine() ?? "";
        if (first.Length == 0) return;
        CheckHeaderText(first);
    }

    private void CheckHeaderText(string header)
    {
        if (header.TrimStart('﻿') != As100201LedgerRow.HeaderLine)
            throw new As100201LedgerFormatException(
                "台帳の列の形が今の作りと違います（シリアル番号の列を足す前の台帳など）。台帳を消すか別の場所へ移してください：" + Location);
    }
}
