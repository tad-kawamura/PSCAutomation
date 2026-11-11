// 測定中の取り込みと加工(SPEC_測定中の取り込み.md、2026-11-11 承認)の模擬テスト。機器・LOGGER は使わない。
// グループ: 加工の計算 / 取り込みの流れと失敗の判断 / xdt から計算した値との一致 / CSV の保存と再試行
using System.Diagnostics;
using System.Globalization;
using PSCAutomation.Apps.AS100201SwitchController;
using PSCAutomation.Wrappers.LoggerFiles;
using PSCAutomation.Wrappers.WaveLogger.Automation;

internal static class LiveCaptureChecks
{
    private static void Check(bool ok, string message) => Program.Check(ok, message);
    private static void Pass(string message) => Program.Pass(message);

    public static void Run()
    {
        FeatureChecks();
        CaptureFlowChecks();
        XdtAgreementChecks();
        CsvSaveChecks();
    }

    private static readonly As100201FeatureChannel Command = new(1, "指令", "", 1, 0);
    private static readonly As100201FeatureChannel Flow = new(5, "流量", "L/min", 10, 0);

    private static string Field(IReadOnlyList<string> header, IReadOnlyList<string> row, string name)
    {
        int index = header.ToList().IndexOf(name);
        if (index < 0) throw new Exception("column missing: " + name);
        return row[index];
    }

    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    // 1. 加工の計算(手で作った値)
    private static void FeatureChecks()
    {
        var channels = new[] { Command, Flow };
        var header = As100201LiveFeatures.Header(channels);
        // 10ms 周期。指令は 0→5 を 0.5 V/秒で上り(10 秒)、そのあと 5→0 を下り(10 秒)。流量 = 指令 × 2(上り)、指令 × 2 − 1(下り)。
        const double dt = 0.01;
        var cmd = new List<double>();
        var flow = new List<double>();
        for (int i = 0; i < 2000; i++)
        {
            bool up = i < 1000;
            double c = up ? i * dt * 0.5 : (2000 - i) * dt * 0.5;
            cmd.Add(c);
            flow.Add(up ? 2 * c : 2 * c - 1);
        }
        var values = new IReadOnlyList<double>[] { cmd, flow };

        var row = As100201LiveFeatures.Compute(channels, values, 600, dt);
        Check(row.Count == header.Count, "row/header length");
        Check(Field(header, row, "点数") == "600" && Field(header, row, "データの秒") == "6" && Field(header, row, "指令の向き") == "上り", "count/seconds/direction up");
        Check(Math.Abs(Num(Field(header, row, "CH1 指令 傾き_直近1秒(/秒)")) - 0.5) < 1e-9, "time slope 1s");
        Check(Math.Abs(Num(Field(header, row, "CH5 流量[L/min] 指令に対する傾き_直近5秒")) - 2) < 1e-6, "slope against command");
        Check(Math.Abs(Num(Field(header, row, "CH1 指令 最大")) - cmd[599]) < 1e-9 && Num(Field(header, row, "CH1 指令 最小")) == 0, "max/min");
        Check(Field(header, row, "CH5 流量[L/min] 区間1_下り平均") == "" && Field(header, row, "CH5 流量[L/min] 区間1_上り−下り") == "", "down bins blank before turning");

        // 先頭 0.5 秒: 1 秒・5 秒の傾きと向きはまだ空欄。
        var early = As100201LiveFeatures.Compute(channels, values, 50, dt);
        Check(Field(header, early, "指令の向き") == "" && Field(header, early, "CH1 指令 傾き_直近1秒(/秒)") == "" &&
              Field(header, early, "CH5 流量[L/min] 指令に対する傾き_直近5秒") == "", "blank before windows filled");
        Check(As100201LiveFeatures.Compute(channels, values, 0, dt).Count == header.Count, "zero points row length");

        // 一往復のあと: 下り・上りの区間の差は 1(流量の上りと下りのずれ)。
        var full = As100201LiveFeatures.Compute(channels, values, 2000, dt);
        Check(Field(header, full, "指令の向き") == "下り", "direction down");
        for (int b = 2; b <= 9; b++)
            Check(Math.Abs(Num(Field(header, full, $"CH5 流量[L/min] 区間{b}_上り−下り")) - 1) < 1e-6, "hysteresis bin " + b);
        // 「先頭 n 点」で計算する: 値が多く渡されても、切り出した値と同じ。
        var cut = As100201LiveFeatures.Compute(channels, new IReadOnlyList<double>[] { cmd.Take(1500).ToList(), flow.Take(1500).ToList() }, 1500, dt);
        Check(cut.SequenceEqual(As100201LiveFeatures.Compute(channels, values, 1500, dt)), "prefix differs from cut values");
        // 平らな指令: 向きは「平ら」、指令に対する傾きと区間は空欄。
        var flat = As100201LiveFeatures.Compute(channels, new IReadOnlyList<double>[] { Enumerable.Repeat(1.0, 600).ToList(), flow.Take(600).ToList() }, 600, dt);
        Check(Field(header, flat, "指令の向き") == "平ら" && Field(header, flat, "CH5 流量[L/min] 指令に対する傾き_直近5秒") == "" &&
              Field(header, flat, "CH5 流量[L/min] 区間5_上り平均") == "", "flat command");
        // CSV の引用符
        Check(As100201LiveFeatures.CsvLine(["a,b", "c\"d", "e"]) == "\"a,b\",\"c\"\"d\",e" &&
              As100201LiveFeatures.ParseCsvLine("\"a,b\",\"c\"\"d\",e").SequenceEqual(new[] { "a,b", "c\"d", "e" }), "csv quoting");
        Pass($"features: {header.Count} columns (now/max/min/mean/std/time slopes per CH, slope vs command, 10 up/down bins), blanks before windows, prefix-only, flat command");
    }

    private sealed class FakeLoggerData
    {
        public List<float[]> Raw = new();   // チャンネルごとの換算前の値(全部)
        public long Available;              // PC に読み出されている点数
        public Exception? Throw;
        public int Calls;
        public LoggerRawDataBlock Read(int unit, IReadOnlyList<int> channels, long start)
        {
            Calls++;
            if (Throw is not null) throw Throw;
            int count = (int)Math.Max(0, Available - start);
            return new LoggerRawDataBlock(Available, start, channels.Select((_, i) => Raw[i].Skip((int)start).Take(count).ToArray()).ToList());
        }
    }

    private static long At(double seconds) => (long)(seconds * Stopwatch.Frequency) + 1_000_000;

    // 2. 取り込みの流れと失敗の判断
    private static void CaptureFlowChecks()
    {
        var template = new As100201CaptureTemplate([Command, Flow], 0.01);
        var data = new FakeLoggerData
        {
            Raw = { Enumerable.Range(0, 500).Select(i => (float)(i * 0.005)).ToArray(), Enumerable.Range(0, 500).Select(i => (float)(i * 0.001)).ToArray() }
        };
        var capture = new As100201LiveCapture(data.Read);
        Check(!capture.Active && capture.Tick(4, At(0), "PC", "0") is null && data.Calls == 0, "tick before begin read data");
        capture.Begin(template, At(0));
        // 開始待ち(状態 2)は点が増えなくても失敗にしない。
        Check(capture.Tick(2, At(3), "PC", "0") is null && capture.RowCount == 0, "start wait counted as stall");
        data.Available = 20;
        Check(capture.Tick(4, At(3.2), "PC", "0") is null && capture.RowCount == 1 && capture.PointCount == 20, "first block");
        // 増えない回は行を足さない。測定中に 2 秒増えなければ失敗。
        Check(capture.Tick(4, At(4.0), "PC", "0") is null && capture.RowCount == 1, "row added without new points");
        string? stall = capture.Tick(4, At(5.3), "PC", "0");
        Check(stall?.Contains("2 秒増えません") == true && capture.Failed && capture.Active, "stall not detected");
        int calls = data.Calls;
        Check(capture.Tick(4, At(5.5), "PC", "0") is null && data.Calls == calls, "read after failure");
        // 失敗のあとの Finish は取り出さずに、それまでの行を返す。
        var table = capture.Finish(At(6), "PC", "0", out string? problem);
        Check(table is not null && table.Rows.Count == 1 && problem is null && data.Calls == calls && !capture.Active, "finish after failure");

        // ほかの操作の処理中(Touch)は 2 秒を数えない。止まったあと(状態 1・10)の増えない回も数えない。
        capture.Begin(template, At(10));
        data.Available = 20;
        capture.Tick(4, At(10.2), "CR", "140");
        capture.Touch(At(12));
        Check(capture.Tick(4, At(12.5), "CR", "140") is null, "touch did not reset stall timer");
        Check(capture.Tick(10, At(20), "CR", "140") is null && capture.Tick(1, At(25), "CR", "140") is null && !capture.Failed, "stopped state counted as stall");
        data.Available = 30;
        table = capture.Finish(At(25.2), "CR", "140", out problem);
        Check(table is not null && problem is null && table.Rows.Count == 2 && table.LastPointCount == 30 && table.Rows[^1][3] == "CR" && table.Rows[^1][4] == "140",
            "finish reads remaining points and adds the last row");
        Check(table!.Header.Take(5).SequenceEqual(As100201LiveCapture.InfoHeader) && table.Rows.All(r => r.Length == table.Header.Count), "table shape");

        // 点数が減った・例外・無効値は失敗。
        capture.Begin(template, At(30));
        data.Available = 20;
        capture.Tick(4, At(30.2), "PC", "0");
        data.Available = 10;
        Check(capture.Tick(4, At(30.4), "PC", "0")?.Contains("点数が減りました") == true, "decrease not detected");
        capture.Begin(template, At(40));
        data.Throw = new System.Runtime.InteropServices.COMException("fake COM");
        Check(capture.Tick(4, At(40.2), "PC", "0")?.Contains("fake COM") == true, "exception not detected");
        data.Throw = null;
        capture.Begin(template, At(50));
        data.Available = 40;
        data.Raw[1][35] = 3.4e38f;
        Check(capture.Tick(4, At(50.2), "PC", "0")?.Contains("36 点目が無効値") == true && capture.PointCount == 0, "invalid value not detected");
        // 記録しない終わり方(雛形の削除など)は捨てる。
        capture.Begin(template, At(60));
        capture.Abandon();
        Check(capture.Finish(At(61), "PC", "0", out _) is null, "abandoned capture returned a table");
        Pass("capture: reads only new points, rows only when points increase, stall 2 s only while measuring (state 4/5), touch during operations, decrease/exception/invalid value -> failure and no more reads, finish adds last row");
    }

    // 3. 保存した xdt から計算した値と、測定中に取り込んで計算した値が同じ(雛形の xdt の値を少しずつ渡す)
    private static void XdtAgreementChecks()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, MainForm.TemplateFolderName);
        string? path = MainForm.FindLearningTemplate(folder, out string reason) ?? throw new Exception("template xdt for test: " + reason);
        var xdt = XdtFile.Load(path);
        var template = As100201CaptureTemplate.FromXdt(xdt);
        Check(template.Channels.Select(c => c.Number).SequenceEqual(new[] { 1, 2, 3, 5, 6 }) && Math.Abs(template.IntervalSeconds - 0.01) < 1e-12 &&
              template.Channels.Select(c => c.ApiChannel).SequenceEqual(new[] { 0, 1, 2, 4, 5 }), "template channels/interval");
        var raw = xdt.Channels.Select(c => c.ReadRawValues().Select(v => (float)v).ToArray()).ToList();
        var data = new FakeLoggerData { Raw = raw };
        var capture = new As100201LiveCapture(data.Read);
        capture.Begin(template, At(0));
        int n = raw[0].Length;
        for (int step = 1; data.Available < n; step++)
        {
            data.Available = Math.Min(n, data.Available + 7);
            Check(capture.Tick(4, At(step * 0.2), "PC", "0") is null, "tick failed");
        }
        var table = capture.Finish(At(100), "PC", "0", out _)!;
        var mismatch = As100201FeatureComparison.Compare(xdt, table.Header, table.Rows);
        Check(mismatch.Count == 0, "features differ from xdt: " + string.Join(" / ", mismatch.Take(3)));
        // 換算は XdtChannel.ReadScaledValues と同じ。
        var scaled = xdt.Channels.Select(c => c.ReadScaledValues()).ToList();
        for (int c = 0; c < template.Channels.Count; c++)
            Check(raw[c].Select(v => template.Channels[c].Scale(v)).SequenceEqual(scaled[c]), "scaling differs: CH" + template.Channels[c].Number);
        Pass($"agreement: features computed while capturing in chunks equal features recomputed from the xdt ({n} points, {table.Rows.Count} rows); scaling equals ReadScaledValues");
    }

    // 4. CSV の保存: xdt の後に同じ名前で。書けなければ止めて、再試行で書く。
    private static void CsvSaveChecks()
    {
        string root = Path.Combine(Path.GetTempPath(), "AS100201_LiveCapture_" + Guid.NewGuid().ToString("N"));
        try
        {
            string data = Path.Combine(root, "学習データ");
            var order = new List<string>();
            var session = new As100201LearningSession(new As100201CsvLedger(Path.Combine(data, "調整記録.csv")),
                path => { order.Add("xdt"); File.WriteAllText(path, "x"); return 0; }, () => new DateTime(2026, 11, 11, 10, 15, 30))
            { SaveDirectory = data };
            var table = new As100201FeatureTable(["取り込み回", "点数"], [["1", "20"], ["2", "a,b"]]);
            session.MeasurementStopped("PC", "PC", "0", As100201LearningSession.StopByCapture);
            session.MeasurementCompleted("PC", "PC", "0", table);
            string csv = Path.Combine(data, "20261111", "20261111_101530_PC.csv");
            Check(File.Exists(csv) && order.SequenceEqual(new[] { "xdt" }) && !session.Blocked, "csv not saved next to xdt");
            byte[] bytes = File.ReadAllBytes(csv);
            Check(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "csv without BOM");
            Check(File.ReadAllText(csv) == "取り込み回,点数\r\n1,20\r\n2,\"a,b\"\r\n", "csv content");
            string ledger = File.ReadAllText(Path.Combine(data, "調整記録.csv"));
            Check(ledger.Contains(",取り込み異常,") && ledger.Contains(@"20261111\20261111_101530_PC.xdt"), "ledger stop trigger for capture failure");
            // 測定の行が無い取り込み(取り込まなかった)なら CSV は作らない。
            session.MeasurementCompleted("CR", "CR", "140");
            Check(!File.Exists(Path.Combine(data, "20261111", "20261111_101530_CR.csv")), "csv written without capture");

            // CSV を書けない(同じ名前のフォルダーがある)→ 止まって再試行で書く。
            string blocked = Path.Combine(data, "20261111", "20261111_101530_PC_2.csv");
            Directory.CreateDirectory(blocked);
            session.MeasurementStopped("PC", "PC", "0", As100201LearningSession.StopBySwitch);
            session.MeasurementCompleted("PC", "PC", "0", table);
            Check(session.Blocked && session.PendingCount == 1 && session.BlockReason?.Contains("取り込みの CSV") == true, "csv failure not blocking");
            Directory.Delete(blocked);
            Check(session.Retry() && File.Exists(blocked) && !session.Blocked, "csv not written on retry");
            Check(As100201LearningSession.FeaturesPath(@"C:\x\20261111\a_PC2.xdt") == @"C:\x\20261111\a_PC2.csv", "features path");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        Pass("csv: written after the xdt with the same name (UTF-8 BOM, header, quoting), ledger stop trigger 取り込み異常, none without capture, write failure blocks and retry writes it");
    }
}
