using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PSCAutomation.Wrappers.Excel;

// Excel を起動する操作(ExcelApplication / ExcelWorkbook / ExcelSheet / ExcelViewer)の検査。Excel のある PC でだけ動く。
// 検査用のブックは、ここで Excel に作らせる(リポジトリに xlsx を置かない)。
// 既定では Excel を画面に出さない。--visible を付けると、画面に出す操作(人に渡す・前面表示・ExcelViewer)も確かめる。
[SupportedOSPlatform("windows")]
internal static class ExcelComChecks
{
    private const string ShapeTextBefore = "型式 AS110-035 S/N 74233 測定日 2026.10.16";
    private const string ShapeTextAfter = "型式 AS110-116B350 S/N 0001234567 測定日 2026.11.04";

    public static bool IsExcelInstalled => Type.GetTypeFromProgID("Excel.Application") is not null;

    public static async Task RunAsync(string temp, bool visible)
    {
        string path = Path.Combine(temp, "com.xlsx");
        CreateWorkbook(path);

        int pid;
        using (var app = ExcelApplication.Start())
        {
            pid = app.ProcessId;
            Check(pid > 0 && !app.Visible && !app.DisplayAlerts, "起動直後の状態");
            Fails<FileNotFoundException>(() => app.Open(Path.Combine(temp, "なし.xlsx")), "無いファイル");

            var book = app.Open(path);
            Check(book.SheetNames.SequenceEqual(new[] { "データ", "判定" }), "シート名一覧");
            Fails<InvalidDataException>(() => book.Sheet("なし"), "無いシート");

            using (var data = book.Sheet("データ"))
            {
                bool screenUpdating = app.ScreenUpdating;
                data.WriteColumns(new ColumnValues[] { new("B2", new[] { 10d, 20d, 30d }), new("A2", new[] { 1d, 2d, 3d }) });
                data.WriteColumns(new ColumnValues[] { new("E2", new[] { 7d }), new("G3", new[] { 8d, 9d }) });
                Check(Equals(data.GetValue("A4"), 3d) && Equals(data.GetValue("B3"), 20d) && Equals(data.GetValue("E2"), 7d) && Equals(data.GetValue("G4"), 9d), "列への書き込み");
                Check(app.ScreenUpdating == screenUpdating, "画面の描き直しの設定が戻っていない");
                Fails<InvalidDataException>(() => data.WriteColumns(new ColumnValues[] { new("A2", new[] { 99d }), new("C2", new[] { 1d }) }), "数式の列");
                Check(Equals(data.GetValue("A2"), 1d), "数式で止めたのに書き込まれた");
                Fails<ArgumentException>(() => data.WriteColumns(new ColumnValues[] { new("A2", new[] { 1d }), new("A9", new[] { 1d }) }), "同じ列へ 2 回");
                Fails<InvalidDataException>(() => data.WriteColumns(new ColumnValues[] { new("A2", new[] { double.NaN }) }), "非数値");
                Fails<ArgumentException>(() => data.WriteColumns(Array.Empty<ColumnValues>()), "空の指定");
                Console.WriteLine("PASS ExcelSheet.WriteColumns: 隣の列はまとめて・離れた列は別々に書き、数式・重複列・非数値・空を止める");

                data.SetValue("F1", "  abc ");
                data.SetValue(2, 6, 1.5);
                Check(data.GetText("F1") == "abc" && Equals(data.GetValue(2, 6), 1.5) && data.GetText(2, 6) == "1.5", "セルの読み書き");
                var block = data.GetValues("A2:B3");
                Check(block.GetLength(0) == 2 && block.GetLength(1) == 2 && Equals(block[0, 0], 1d) && Equals(block[1, 1], 20d), "範囲の読み取り");
                Check(Equals(data.GetValues("A2")[0, 0], 1d), "1 セルの範囲の読み取り");
                data.SetValues("H1", new object?[,] { { 1d, 2d }, { 3d, "x" } });
                Check(Equals(data.GetValue("I2"), "x") && Equals(data.GetValue("H2"), 3d), "範囲の書き込み");
                data.ClearContents("H1:H2");
                data.ClearContents(1, 9, 9);
                Check(data.GetValue("H1") is null && data.GetValue("I1") is null && Equals(data.GetValue("I2"), "x"), "値の消去");
                Check(data.HasFormula("C2") && !data.HasFormula("A2:A4") && data.HasFormula("A2:C2"), "数式の有無");
                Check(data.LastUsedRow(1) == 4 && data.LastUsedRow(26) == 0, $"最後の行 {data.LastUsedRow(1)} / {data.LastUsedRow(26)}");
                data.CopyRow(2, 10);
                Check(data.HasFormula("C10") && Equals(data.GetValue("A10"), 1d), "行のコピー");
                Console.WriteLine("PASS ExcelSheet: セル・範囲の読み書き、消去、数式の有無、最後の行、行のコピー");

                // 使っている範囲をまとめて読む: 数式のセルは数式の文字、値のセルは値。書き換えると、その 1 セルだけが変わって読める。
                string Entered(ExcelRangeValues used, string address)
                {
                    var (column, row) = TemplateWriter.ParseCell(address);
                    int r = row - used.Row, c = column - used.Column;
                    return r < 0 || c < 0 || r >= used.Values.GetLength(0) || c >= used.Values.GetLength(1)
                        ? "" : Convert.ToString(used.Values[r, c], System.Globalization.CultureInfo.InvariantCulture) ?? "";
                }
                var usedBefore = data.ReadUsedFormulas();
                Check(usedBefore.Sheet == "データ" && Entered(usedBefore, "A2") == "1" && Entered(usedBefore, "C2").StartsWith('=') && Entered(usedBefore, "I2") == "x" && Entered(usedBefore, "H1") == "",
                    $"使っている範囲の読み取り A2={Entered(usedBefore, "A2")} C2={Entered(usedBefore, "C2")}");
                string formulaBefore = Entered(usedBefore, "C2");
                data.SetValue("A2", 5d);
                var usedAfter = data.ReadUsedFormulas();
                Check(Entered(usedAfter, "A2") == "5" && Entered(usedAfter, "C2") == formulaBefore && Entered(usedAfter, "B3") == Entered(usedBefore, "B3"),
                    "値を書き換えると、そのセルだけが変わり、それを使う数式の文字は変わらない");
                data.SetValue("A2", 1d);
                Console.WriteLine("PASS ExcelSheet.ReadUsedFormulas: 数式は数式の文字・値は値で読み、書き換えたセルだけが変わる");
            }

            app.Calculate();
            book.CalculateSheets("データ");
            await app.WaitForCalculationAsync(TimeSpan.FromSeconds(30));
            Check(!app.IsCalculating, "計算中のまま");
            Fails<InvalidDataException>(() => book.CalculateSheets("なし"), "無いシートの計算");
            using (var judge = book.Sheet("判定"))
            {
                Check(judge.GetText("E21") == "OK" && judge.GetText("L21") == "NG", $"判定セル E21={judge.GetText("E21")} L21={judge.GetText("L21")}");
                using (var data = book.Sheet("データ")) Check(Equals(data.GetValue("C2"), 11d), "数式の結果");
                Console.WriteLine("PASS 再計算: ブック全体・シートごとに計算し、計算の終わりを待ち、判定セルを読める");

                var header = book.ReadNamedRange("PP_Header");
                Check(header.Sheet == "判定" && header.Row == 1 && header.Column == 5 && header.Values.GetLength(1) == 3
                    && Equals(header.Values[0, 0], 5d) && Equals(header.Values[0, 2], 15d), "名前定義");
                Fails<InvalidDataException>(() => book.ReadNamedRange("なし"), "無い名前定義");
                Console.WriteLine("PASS ExcelWorkbook.ReadNamedRange: 範囲の位置と値を読み、無い名前は止める");

                var shapes = judge.GetShapeTexts();
                Check(shapes.Count == 1 && shapes[0].Text == ShapeTextBefore, "図形の文字の一覧");
                var replacements = new ShapeTextReplacement[]
                {
                    new(@"(?<value>AS110-[A-Za-z0-9]+)", "AS110-116B350"),
                    new(@"S/N[ \t　]*(?<value>\d+|-)", "0001234567"),
                    new(@"測定日[ \t　]*(?<value>\d{4}[./-]\d{1,2}[./-]\d{1,2})", "2026.11.04"),
                };
                Check(judge.ReplaceShapeText(t => t.Contains("S/N") && t.Contains("測定日"), replacements) == 1, "図形の数");
                Check(judge.GetShapeTexts()[0].Text == ShapeTextAfter, "置き換え後の文字: " + judge.GetShapeTexts()[0].Text);
                Check(judge.ReplaceShapeText(t => t.Contains("S/N"), replacements) == 1 && judge.GetShapeTexts()[0].Text == ShapeTextAfter, "同じ値で 2 回目");
                Check(judge.ReplaceShapeText(t => t.Contains("該当なし"), replacements) == 0, "当てはまらない図形");
                Fails<ArgumentException>(() => judge.ReplaceShapeText(_ => true, new ShapeTextReplacement[] { new(@"S/N \d+", "1") }), "value の無いパターン");
                Console.WriteLine("PASS ExcelSheet.ReplaceShapeText: 図形の中の値だけを置き換え、当てはまる図形の数を返す(書式は保存後に確認)");

                book.Save();
                string copy = Path.Combine(temp, "copy.xlsx"), pdf = Path.Combine(temp, "judge.pdf");
                book.SaveCopyAs(copy);
                judge.ExportAsPdf(pdf);
                Check(File.Exists(copy) && File.ReadAllBytes(pdf).Take(4).SequenceEqual("%PDF"u8.ToArray()), "別名保存・PDF");
                Console.WriteLine("PASS 保存: 上書き保存・別名保存・シートの PDF");
            }

            Check(book.IsStillOpen(), "開いているのに閉じたと判定");
            book.Close();
            Check(book.IsClosed && !book.IsStillOpen() && app.WorkbookCount == 0, "閉じたあとの状態");
            Fails<InvalidOperationException>(() => book.Sheet("データ"), "閉じたブックの操作");
            Console.WriteLine("PASS ExcelWorkbook.Close: 閉じたことを Excel のブック一覧で確かめ、閉じたブックの操作を止める");

            string readOnlyPath = Path.Combine(temp, "copy.xlsx");
            File.SetAttributes(readOnlyPath, FileAttributes.ReadOnly);
            try
            {
                Fails<IOException>(() => app.Open(readOnlyPath), "読み取り専用で開かれたブック");
                Check(app.WorkbookCount == 0, "読み取り専用のブックが開いたまま");
                using (var readOnly = app.Open(readOnlyPath, readOnly: true)) Check(readOnly.IsReadOnly, "読み取り専用で開く");
                Check(app.WorkbookCount == 0, "Dispose したブックが開いたまま");
            }
            finally { File.SetAttributes(readOnlyPath, FileAttributes.Normal); }
            Console.WriteLine("PASS ExcelApplication.Open: 読み取り専用で開かれたら閉じて止める。読み取り専用の指定なら開く");
        }
        Check(Exited(pid), "Dispose のあと Excel が残った");

        using (var app = ExcelApplication.Start())
        {
            pid = app.ProcessId;
            app.Open(path);
        }
        Check(Exited(pid), "ブックを開いたままの Dispose のあと Excel が残った");
        Console.WriteLine("PASS ExcelApplication.Dispose: 開いたブックを閉じ、Excel を終了する");

        using (var app = ExcelApplication.Start())
        {
            pid = app.ProcessId;
            var book = app.Open(path);
            Process.GetProcessById(pid).Kill();
            Check(Exited(pid), "Excel を止められない");
            Check(!app.IsConnected() && !book.IsStillOpen() && book.IsClosed, "Excel が落ちたのに接続中と判定");
        }
        Console.WriteLine("PASS ExcelApplication.IsConnected: Excel が落ちたら false、そのあとの Dispose で止まらない");

        CheckShapeFormat(path);
        Console.WriteLine("PASS 図形の書式: 置き換えた値と前後の文字のフォント・大きさ・太字・下線が元のまま");

        if (!visible)
        {
            Console.WriteLine("SKIP 画面に出す操作(人に渡す・前面表示・ExcelViewer): --visible を付けると確かめる");
            return;
        }
        using (var app = ExcelApplication.Start(visible: true))
        {
            pid = app.ProcessId;
            var book = app.Open(path);
            using (var judge = book.Sheet("データ")) judge.Activate();
            IntPtr handle = book.ShowWindow("判定");
            Check(handle != IntPtr.Zero && app.Visible, "前面表示");
            app.HandOverToUser();
        }
        Check(!Exited(pid, 3000), "人に渡した Excel が閉じた");
        Process.GetProcessById(pid).Kill();
        Console.WriteLine("PASS ExcelApplication.HandOverToUser / ExcelWorkbook.ShowWindow: 窓のハンドルを返し、Dispose しても Excel が残る");

        var before = Process.GetProcessesByName("EXCEL").Select(p => p.Id).ToHashSet();
        var elapsed = await ExcelViewer.OpenForUserAsync(path);
        var opened = Process.GetProcessesByName("EXCEL").Where(p => !before.Contains(p.Id)).ToArray();
        Check(opened.Length == 1 && elapsed > TimeSpan.Zero, "ExcelViewer で Excel が開いていない");
        Thread.Sleep(2000);
        Check(!opened[0].HasExited, "ExcelViewer で開いた Excel が閉じた");
        opened[0].Kill();
        opened[0].WaitForExit(10000);
        before = Process.GetProcessesByName("EXCEL").Select(p => p.Id).ToHashSet();
        try { await ExcelViewer.OpenForUserAsync(Path.Combine(temp, "なし.xlsx")); throw new Exception("無いファイルで ExcelViewer が成功した"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("停止箇所")) { }
        Check(!Process.GetProcessesByName("EXCEL").Any(p => !before.Contains(p.Id)), "ExcelViewer の失敗のあと Excel が残った");
        Console.WriteLine("PASS ExcelViewer: 開いて人に渡し、失敗したら停止箇所を伝えて Excel を残さない");
    }

    private static bool Exited(int pid, int milliseconds = 15000)
    {
        try { return Process.GetProcessById(pid).WaitForExit(milliseconds); }
        catch (ArgumentException) { return true; }
    }

    // 検査用のブックを Excel に作らせる。
    private static void CreateWorkbook(string path) => WithRawExcel(app => CreateWorkbookCore(app, path));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateWorkbookCore(dynamic app, string path)
    {
        {
            app.Visible = false;
            app.DisplayAlerts = false;
            dynamic book = app.Workbooks.Add();
            dynamic data = book.Worksheets[1];
            while ((int)book.Worksheets.Count > 1) book.Worksheets[2].Delete();
            data.Name = "データ";
            dynamic judge = book.Worksheets.Add(After: data);
            judge.Name = "判定";
            data.Range["A1"].Value2 = "電流";
            data.Range["B1"].Value2 = "圧力";
            data.Range["C2"].Formula = "=A2+B2";
            judge.Range["E21"].Formula = "=IF(SUM(データ!A2:A4)>0,\"OK\",\"NG\")";
            judge.Range["L21"].Formula = "=IF(SUM(データ!B2:B4)>1000,\"OK\",\"NG\")";
            judge.Range["E1:G1"].Value2 = new object[,] { { 5d, 10d, 15d } };
            book.Names.Add(Name: "PP_Header", RefersTo: "=判定!$E$1:$G$1");
            dynamic shape = judge.Shapes.AddTextbox(1, 10, 10, 400, 40);
            dynamic range = shape.TextFrame2.TextRange;
            range.Text = ShapeTextBefore;
            range.Font.Size = 11;
            range.Characters(ShapeTextBefore.IndexOf("S/N") + 1, 3).Font.Bold = -1;
            range.Characters(ShapeTextBefore.IndexOf("74233") + 1, 5).Font.Size = 14;
            range.Characters(ShapeTextBefore.IndexOf("74233") + 1, 5).Font.UnderlineStyle = 1;
            book.SaveAs(path, 51);
            book.Close(false);
        }
    }

    // 置き換えたあとの図形の書式を、保存したファイルを開き直して確かめる。
    private static void CheckShapeFormat(string path) => WithRawExcel(app => CheckShapeFormatCore(app, path));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckShapeFormatCore(dynamic app, string path)
    {
        {
            app.Visible = false;
            app.DisplayAlerts = false;
            dynamic book = app.Workbooks.Open(path, ReadOnly: true);
            dynamic range = book.Worksheets["判定"].Shapes.Item(1).TextFrame2.TextRange;
            string text = (string)range.Text;
            Check(text == ShapeTextAfter, "保存後の図形の文字: " + text);
            string Style(int index, int length = 1)
            {
                dynamic font = range.Characters(index + 1, length).Font;
                return $"{font.Size}|{font.Bold}|{font.UnderlineStyle}";
            }
            string plain = Style(text.IndexOf("型式"));
            Check(Style(text.IndexOf("S/N"), 3) == "11|-1|0", "S/N の太字: " + Style(text.IndexOf("S/N"), 3));
            Check(Style(text.IndexOf("0001234567"), 10) == "14|0|1", "置き換えた番号の書式: " + Style(text.IndexOf("0001234567"), 10));
            Check(Style(text.IndexOf("測定日")) == plain && Style(text.IndexOf("2026.11.04"), 10) == plain && plain == "11|0|0", "前後の文字の書式: " + plain);
            book.Close(false);
        }
    }

    // 生の COM で Excel を使う。途中の参照(book・range など)は作業のメソッドを抜けてから GC で返し、Excel を残さない。
    private static void WithRawExcel(Action<object> work)
    {
        int pid = RunRawExcel(work);
        for (int i = 0; i < 2; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Check(Exited(pid), "検査用に起動した Excel が残った");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunRawExcel(Action<object> work)
    {
        object app = Activator.CreateInstance(Type.GetTypeFromProgID("Excel.Application")!)!;
        GetWindowThreadProcessId(new IntPtr((int)((dynamic)app).Hwnd), out uint pid);
        try { work(app); }
        finally
        {
            try { ((dynamic)app).Quit(); } catch { }
            Marshal.FinalReleaseComObject(app);
        }
        return (int)pid;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

    private static void Fails<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"{name}: {typeof(T).Name} が出るべき操作が成功した");
    }
}
