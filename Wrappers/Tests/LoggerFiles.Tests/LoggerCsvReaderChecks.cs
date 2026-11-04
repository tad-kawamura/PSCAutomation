using System.Text;
using PSCAutomation.Wrappers.LoggerFiles;
using static TestSupport;

// LoggerCsvReader(元: AS110AllCheck Conversion/LoggerCsvReader.cs)の検査。モノレポで追加。
// 実機の CSV は使わず、列の自動検出・明示指定・区間判定・文字コードを人工データで確かめる。
internal static class LoggerCsvReaderChecks
{
    public static void Run()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string temp = CreateTempFolder("LoggerFilesCsv");
        try
        {
            string Write(string name, string text, Encoding encoding)
            {
                string path = Path.Combine(temp, name);
                File.WriteAllText(path, text, encoding);
                return path;
            }
            const string body =
                "\"収集データ\",\"NR-X100\"\n" +
                "\"時間\",\"CH1 電流\",\"CH2 圧力\"\n" +
                "0.000,1.5,100.25\n0.010,1.6,100.5\n0.020,1.7,100.75\n0.030,1.8,101\n0.040,1.9,101.25\n" +
                "\"最大\",\"1.9\",\"x\"\n";
            string utf8 = Write("utf8.csv", body, new UTF8Encoding(true));
            var current = new LoggerCsvColumn(1, "電流");
            var pressure = new LoggerCsvColumn(2, "圧力");
            double[] expectedCurrent = { 1.5, 1.6, 1.7, 1.8, 1.9 };
            double[] expectedPressure = { 100.25, 100.5, 100.75, 101, 101.25 };

            var auto = LoggerCsvReader.Read(utf8, new[] { pressure, current }, 5);
            Ensure(auto.Count == 2 && auto[0].SequenceEqual(expectedPressure) && auto[1].SequenceEqual(expectedCurrent),
                "自動検出(columns の順で返す)");
            var manual = LoggerCsvReader.Read(utf8, new[] { current with { ColumnNumber = 2 }, pressure with { ColumnNumber = 3 } }, 5);
            Ensure(manual[0].SequenceEqual(expectedCurrent) && manual[1].SequenceEqual(expectedPressure), "列番号の明示指定");
            // 1 つでも 0 があると全部探し直す(元の動作)。誤った列番号 1 は使われない。
            var partial = LoggerCsvReader.Read(utf8, new[] { current, pressure with { ColumnNumber = 1 } }, 5);
            Ensure(partial[1].SequenceEqual(expectedPressure), "一部だけ指定したときは全部自動検出");
            Throws<InvalidDataException>(() => LoggerCsvReader.Read(utf8, new[] { current, pressure }, 4), "点数違い");
            Throws<InvalidDataException>(() => LoggerCsvReader.Read(utf8,
                new[] { current with { ColumnNumber = 2 }, pressure with { ColumnNumber = 2 } }, 5), "同じ列を 2 つに指定");
            Throws<InvalidDataException>(() => LoggerCsvReader.Read(utf8, new[] { new LoggerCsvColumn(5, "流量") }, 5), "見出しが見つからない");
            Throws<ArgumentException>(() => LoggerCsvReader.Read(utf8, Array.Empty<LoggerCsvColumn>(), 5), "列の指定なし");
            Console.WriteLine("PASS LoggerCsvReader: 自動検出・明示指定・一部指定・点数違い・同列指定・見出しなし");

            string tab = Write("cp932.csv",
                "収集データ(電流と圧力)\n時刻\t1-1\t1-2\n0\t1\t2\n1\t3\t4\n2\t5\t6\n",
                Encoding.GetEncoding(932));
            var byNumber = LoggerCsvReader.Read(tab, new[] { new LoggerCsvColumn(1, ""), new LoggerCsvColumn(2, "") }, 3);
            Ensure(byNumber[0].SequenceEqual(new double[] { 1, 3, 5 }) && byNumber[1].SequenceEqual(new double[] { 2, 4, 6 }),
                "CP932・タブ区切り・1-n 見出し");
            string utf16 = Write("utf16.csv", body, new UnicodeEncoding(false, true));
            Ensure(LoggerCsvReader.Read(utf16, new[] { current, pressure }, 5)[0].SequenceEqual(expectedCurrent), "UTF-16LE");
            // 区間は「指定した全列が数値の行」で切る。末尾の「最大」行は CH1 列だけなら数値なので、1 列だけ読むと 6 点の区間になる(元の動作)。
            Throws<InvalidDataException>(() => LoggerCsvReader.Read(utf16, new[] { current }, 5), "1 列だけでは集計行も区間に入る");
            string twice = Write("twice.csv",
                body + "\"時間\",\"CH1 電流\",\"CH2 圧力\"\n0,1,2\n0,1,2\n0,1,2\n0,1,2\n0,1,2\n", new UTF8Encoding(false));
            Throws<InvalidDataException>(() => LoggerCsvReader.Read(twice, new[] { current, pressure }, 5), "同じ点数の区間が 2 つ");
            Console.WriteLine("PASS LoggerCsvReader: CP932・タブ区切り・UTF-16LE を読み、同じ点数の区間が複数なら例外");
        }
        finally { Directory.Delete(temp, true); }
    }
}
