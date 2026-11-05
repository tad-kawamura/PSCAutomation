using System.Globalization;
using PSCAutomation.Apps.AS110NoiseTest.Analysis;
using PSCAutomation.Wrappers.Excel;
using static PSCAutomation.Apps.AS110NoiseTest.Excel.NoiseWorkbookLayout;

namespace PSCAutomation.Apps.AS110NoiseTest.Excel;

// 雛形から作った結果ブックに測定結果を書く。Excel の起動・開く・セルの読み書き・行のコピー・保存・後始末は
// Wrappers/Excel の部品(ExcelApplication / ExcelWorkbook / ExcelSheet)を使い、ここではどこに何を書くかだけを決める。
// 使い方:
//   using var wb = NoiseResultWorkbook.Open(出力xlsx, 雛形xlsx);   // 無ければ雛形をコピーして作る。あれば追記
//   wb.WriteSettings(judgeSettings);
//   wb.Write(record);  ...  wb.Save();
//   wb.LeaveOpenForUser();  // 終わったあと Excel を開いたままにしたいとき(Dispose で閉じなくなる)。ShowToUser は前にも出す
// 行の決め方: PP値シートで 型式・SN・備忘録 が同じ行があればその行、無ければ SN が空の最初の行(型式は 2026-11-05 追加)。
//   (日付はキーにしない。人の Excel でも「SN60260再調整後」は 2 日にまたがって 1 行だった。別の行にしたいときは備忘録を変える。
//    測定日は行を作ったときの日付を入れ、以後は変えない)
// 列の決め方: 見出し(名前定義 PP_Header)の値 = 指令 mA の列。1〜35 の整数以外は書けない(例外)。
internal sealed class NoiseResultWorkbook : INoiseResultWriter, IDisposable
{
    private ExcelApplication? _app;
    private ExcelWorkbook? _book;
    private ExcelSheet? _pp, _mean, _red, _detail, _setting;
    private Dictionary<int, int>? _maColumns;   // mA → 列番号
    private bool _disposed;

    public string Path { get; }
    // Open で雛形をコピーして新しく作ったとき true(何も書かずにやめたとき、呼び出し側が消せるように)。
    public bool CreatedFromTemplate { get; private set; }

    private NoiseResultWorkbook(string path) => Path = path;

    public static NoiseResultWorkbook Open(string outputPath, string templatePath, bool visible = false)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("出力ファイルを指定してください。", nameof(outputPath));
        string full = System.IO.Path.GetFullPath(outputPath);
        bool created = false;
        if (!File.Exists(full))
        {
            if (!File.Exists(templatePath)) throw new FileNotFoundException("雛形 Excel が見つかりません。", templatePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.Copy(templatePath, full);
            created = true;
        }
        var wb = new NoiseResultWorkbook(full) { CreatedFromTemplate = created };
        try
        {
            wb.Start(visible);
            return wb;
        }
        catch
        {
            wb.Dispose();
            throw;
        }
    }

    private void Start(bool visible)
    {
        // 確認ダイアログは出さない。終わったら(人に渡していなければ)ほかのブックがあっても Excel を終了する(移植前と同じ)。
        _app = ExcelApplication.Start(visible, displayAlerts: false);
        _app.QuitMode = ExcelQuitMode.Always;
        _book = _app.Open(Path);   // 読み取り専用で開かれたら閉じて IOException
        _pp = _book.Sheet(PpSheet);
        _mean = _book.Sheet(MeanSheet);
        _red = _book.Sheet(RedSheet);
        _detail = _book.Sheet(DetailSheet);
        _setting = _book.Sheet(SettingSheet);
        _maColumns = ReadMaColumns();
    }

    private Dictionary<int, int> ReadMaColumns()
    {
        ExcelRangeValues header = _book!.ReadNamedRange(MaHeaderName);
        var map = new Dictionary<int, int>();
        for (int j = 0; j < header.Values.GetLength(1); j++)
        {
            if (header.Values[0, j] is double d && d == Math.Round(d) && d > 0)
                map[(int)d] = header.Column + j;
        }
        if (map.Count == 0) throw new InvalidDataException("見出し行に mA の数値がありません。雛形の形式が違います。");
        return map;
    }

    // 判定のしきい値を設定シートに書く(アプリが実際に使った値を残す)。
    public void WriteSettings(NoiseJudgeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ThrowIfDisposed();
        _setting!.SetValue(SettingYellowRow, SettingValueColumn, settings.YellowPeakToPeakKpa);
        _setting.SetValue(SettingPlateauKpaRow, SettingValueColumn, settings.PlateauThresholdKpa);
        _setting.SetValue(SettingPlateauSamplesRow, SettingValueColumn, settings.PlateauMinSamples);
        _setting.SetValue(SettingLowHighRatioRow, SettingValueColumn, settings.LowHighPowerRatioMin);
        _setting.SetValue(SettingStepKpaRow, SettingValueColumn, settings.StepMinKpa);
        _setting.SetValue(SettingStepTRow, SettingValueColumn, settings.StepMinT);
    }

    // 1 つの電流の結果を書く。戻り値は PP値シートの行番号。
    public int Write(NoiseResultRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(record.SerialNumber)) throw new ArgumentException("SN が空です。", nameof(record));
        int column = MaColumn(record.CommandMa);
        string model = record.Model ?? "";
        string memo = record.Memo ?? "";
        (int row, bool isNew) = FindOrAddRow(model, record.SerialNumber, memo);

        var a = record.Analysis;
        var m = a.Metrics;
        if (isNew)
        {
            _pp!.SetValue(row, DateColumn, record.MeasuredAt.Date.ToOADate());
            _pp.SetValue(row, ModelColumn, model);
            _pp.SetValue(row, SerialColumn, record.SerialNumber);
            _pp.SetValue(row, MemoColumn, memo);
        }
        // 人は LOGGER の表示(0.1 kPa 刻み)で読んでいた。判定(NoiseJudge)と同じ丸めで書く。
        _pp!.SetValue(row, column, Math.Round(m.PeakToPeakKpa, 1));
        _mean!.SetValue(row, column, Math.Round(a.ControlPressure.Mean, 2));
        if (m.Grade == NoiseGrade.Red) _red!.SetValue(row, column, RedMark);
        else _red!.ClearContents(row, column, column);      // 測り直しで赤でなくなったら消す
        AppendDetail(record);
        return row;
    }

    // 同じ 型式・SN・備忘録 の行に、もう p-p が書かれている mA の一覧(小さい順)。行が無ければ空。
    // 開始前に「測定済みの mA を上書きしてよいか」を人に確かめるために使う。
    public IReadOnlyList<int> FindMeasuredMilliamps(string model, string serial, string memo)
    {
        ThrowIfDisposed();
        int? row = FindRow(model ?? "", serial, memo ?? "");
        if (row is null) return [];
        int first = _maColumns!.Values.Min(), last = _maColumns.Values.Max();
        object?[,] values = _pp!.GetValues(Address(row.Value, first) + ":" + Address(row.Value, last));
        var measured = new List<int>();
        foreach ((int ma, int col) in _maColumns.OrderBy(kv => kv.Key))
        {
            if (values[0, col - first] is double) measured.Add(ma);
        }
        return measured;
    }

    // PP値シートの行の数式(最大p-p・黄・赤・判定)を読む。Excel の再計算後の値。
    public (double? MaxPp, int? Yellow, int? Red, string Judge) ReadRowSummary(int row)
    {
        ThrowIfDisposed();
        _app!.Calculate();
        return (
            _pp!.GetValue(row, MaxPpColumn) as double?,
            _pp.GetValue(row, YellowCountColumn) is double y ? (int)y : null,
            _pp.GetValue(row, RedCountColumn) is double r ? (int)r : null,
            _pp.GetValue(row, JudgeColumn)?.ToString() ?? "");
    }

    public void Save()
    {
        ThrowIfDisposed();
        _book!.Save();
    }

    // 終了後も Excel を表示したまま残す(Dispose で閉じない)。
    public void LeaveOpenForUser()
    {
        ThrowIfDisposed();
        _app!.HandOverToUser();
    }

    // 人に渡し(LeaveOpenForUser と同じ)、PP値シートを表示する。戻り値はブックの窓のハンドル
    // (Windows の前面に出すときは Wrappers/Common の WindowActivation.TryActivate に渡す)。
    public IntPtr ShowToUser()
    {
        ThrowIfDisposed();
        _app!.HandOverToUser();
        return _book!.ShowWindow(PpSheet);
    }

    private int MaColumn(double commandMa)
    {
        if (commandMa != Math.Round(commandMa) || !_maColumns!.TryGetValue((int)Math.Round(commandMa), out int col))
            throw new InvalidOperationException(
                $"{commandMa.ToString(CultureInfo.InvariantCulture)} mA の列が雛形にありません(列は {_maColumns!.Keys.Min()}〜{_maColumns.Keys.Max()} mA の整数)。");
        return col;
    }

    private (int Row, bool IsNew) FindOrAddRow(string model, string serial, string memo)
    {
        (int? found, int? firstEmpty) = SearchRows(model, serial, memo);
        if (found is int row) return (row, false);
        if (firstEmpty is int newRow)
        {
            EnsureRowPrepared(newRow);
            return (newRow, true);
        }
        throw new InvalidOperationException($"PP値シートの {FirstDataRow}〜{LastSearchRow} 行に空きがありません。");
    }

    private int? FindRow(string model, string serial, string memo) => SearchRows(model, serial, memo).Found;

    // 上から見て、型式・SN・備忘録が同じ行(Found)か、SN が空の最初の行(FirstEmpty)で止まる。
    // 空の行より下は見ない(追記は上から詰めて書くため)。
    private (int? Found, int? FirstEmpty) SearchRows(string model, string serial, string memo)
    {
        object?[,] keys = _pp!.GetValues(Address(FirstDataRow, ModelColumn) + ":" + Address(LastSearchRow, MemoColumn));
        int count = keys.GetLength(0);
        static string Text(object? value) => value?.ToString()?.Trim() ?? "";
        for (int i = 0; i < count; i++)
        {
            string sn = Text(keys[i, SerialColumn - ModelColumn]);
            if (sn.Length == 0) return (null, FirstDataRow + i);
            if (!string.Equals(sn, serial.Trim(), StringComparison.Ordinal)) continue;
            if (Text(keys[i, 0]) != model.Trim()) continue;
            if (Text(keys[i, MemoColumn - ModelColumn]) == memo.Trim()) return (FirstDataRow + i, null);
        }
        return (null, null);
    }

    // 雛形で用意した行(罫線・数式入り)を超えたら、上の行をコピーして同じ形にする。
    private void EnsureRowPrepared(int row)
    {
        if (row == FirstDataRow) return;
        if (_pp!.HasFormula(Address(row, MaxPpColumn))) return;
        foreach (ExcelSheet sheet in new[] { _pp, _mean!, _red! })
            sheet.CopyRow(row - 1, row);
        // コピーした値は消す(数式の列はそのまま)。PP値は A〜最後の mA 列、平均値・赤判定は mA の列だけ。
        int lastMa = _maColumns!.Values.Max();
        _pp.ClearContents(row, DateColumn, lastMa);
        int firstMa = _maColumns.Values.Min();
        _mean!.ClearContents(row, firstMa, lastMa);
        _red!.ClearContents(row, firstMa, lastMa);
        _pp.ClearContents(row, NoteColumn, NoteColumn);
    }

    private void AppendDetail(NoiseResultRecord r)
    {
        var a = r.Analysis;
        var m = a.Metrics;
        string grade = m.Grade switch { NoiseGrade.Red => "赤", NoiseGrade.Yellow => "黄", _ => "" };
        var reasons = new List<string>();
        if (m.PlateauHit) reasons.Add("A");
        if (m.LowFrequencyHit) reasons.Add("B");
        if (m.StepHit) reasons.Add("C");
        object?[] values =
        [
            r.MeasuredAt.ToOADate(), r.Model ?? "", r.SerialNumber, r.Memo ?? "", r.CommandMa, r.MeasuredMa,
            a.Current.Mean, a.Current.PeakToPeak,
            a.SupplyPressure.Mean, a.SupplyPressure.PeakToPeak,
            a.ControlPressure.Mean, m.PeakToPeakKpa, a.ControlPressure.Min, a.ControlPressure.Max,
            a.Extra?.Mean, a.Extra?.PeakToPeak,
            grade, string.Join("/", reasons),
            m.PlateauRunSamples, m.LowHighPowerRatio, m.StepKpa, m.StepT,
            r.XdtFileName,
        ];
        if (values.Length != DetailColumnCount) throw new InvalidOperationException("詳細シートの列数が合いません。");
        var block = new object?[1, values.Length];
        for (int i = 0; i < values.Length; i++) block[0, i] = values[i];

        // A 列の最後の行の次(見出し行しかなければ 2 行目)。
        int row = Math.Max(DetailFirstRow, _detail!.LastUsedRow(1) + 1);
        _detail.SetValues(Address(row, 1), block);
    }

    private static string Address(int row, int column)
    {
        string letters = "";
        for (int c = column; c > 0; c = (c - 1) / 26)
            letters = (char)('A' + (c - 1) % 26) + letters;
        return letters + row.ToString(CultureInfo.InvariantCulture);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed || _book is null) throw new ObjectDisposedException(nameof(NoiseResultWorkbook));
    }

    // 人に渡していなければ、保存せずにブックを閉じて Excel を終了する。渡したあとは参照だけ返す。
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _setting?.Dispose(); _detail?.Dispose(); _red?.Dispose(); _mean?.Dispose(); _pp?.Dispose();
        _book?.Dispose();
        _app?.Dispose();
        _setting = _detail = _red = _mean = _pp = null;
        _book = null;
        _app = null;
    }
}
