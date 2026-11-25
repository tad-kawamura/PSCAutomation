using System.Drawing.Imaging;
using PSCAutomation.Apps.AS110AllCheck;
using PSCAutomation.Wrappers.LoggerFiles;

// 校正画面を、機器なしで開いて見るための道具(2026-11-20、人の指示: 会社にいなくても画面を確かめたい)。
// 計器(発振器・マルチメータ・GC16・NR-500)は模擬で、画面と手順はアプリの実物をそのまま動かす。機器にも LOGGER にも触らない。
// 「適用」を押しても、今日の校正値のファイルは書かない(結果を知らせるだけ)。
//
// 使い方(リポジトリの直下で):
//   dotnet run --project Apps/AS110AllCheck/Tests/CalibrationPreview
//       校正画面が開く。実機と同じ順に押して確かめる(ボールバルブ・レギュレーターの指示は「した」ことにして押す)。
//   dotnet run --project Apps/AS110AllCheck/Tests/CalibrationPreview -- --no-meter
//       マルチメータ(7351E)が見つからないときの画面(「電流を手で入れる」)を見る。
//   dotnet run --project Apps/AS110AllCheck/Tests/CalibrationPreview -- --no-gc16
//       「GC16 で読み取る」の最初の 1 回だけ GC16 が見つからない。「再開」で続けられることと、「中止」を押したあとの止まった画面の
//       「もう一度読み取る」で ③ からやり直せることを見る。
//   dotnet run --project Apps/AS110AllCheck/Tests/CalibrationPreview -- --amp-off
//       最初の ② で電流アンプの電源が切れている(電流が 0 mA)。止まった画面の文と、「再開」で ② から続けられることを見る。
//   dotnet run --project Apps/AS110AllCheck/Tests/CalibrationPreview -- --screenshots <フォルダー>
//       人が押さずに一通り進め、各場面の画像(png)をフォルダーに残す。
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool noMeter = args.Contains("--no-meter");
        bool noGauge = args.Contains("--no-gc16");
        bool ampOff = args.Contains("--amp-off");
        int shots = Array.IndexOf(args, "--screenshots");
        if (shots >= 0)
        {
            if (shots + 1 >= args.Length) { Console.Error.WriteLine("--screenshots のあとに保存先のフォルダーを書いてください。"); return 1; }
            return Screenshots.Run(Path.GetFullPath(args[shots + 1]));
        }
        while (true)
        {
            var rig = new SimulatedRig { MeterMissing = noMeter, GaugeMissingOnce = noGauge, AmplifierOffOnce = ampOff };
            using var dialog = Preview.OpenCalibration(rig);
            DialogResult result = dialog.ShowDialog();
            string message = result == DialogResult.OK
                ? "「適用」が押されました（画面確認用なので、校正値は保存していません）。\r\n\r\n" + dialog.Calibration.Summary + "\r\n読み取り方：" + dialog.Reading.MethodText
                : "適用せずに閉じました。";
            using var owner = new Form { TopMost = true, ShowInTaskbar = false };
            if (MessageBox.Show(owner, message + "\r\n\r\nもう一度、最初から開きますか？", "校正画面の確認（機器なし）",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
                return 0;
        }
    }
}

internal static class Preview
{
    // 雛形の値に近い初期値(実物は「雛形\AS110データ調整・データ取り用.xdt」から読む)。
    public static As110Calibration Template { get; } =
        new(new ScalingPoints(-0.002f, 0.01f, 0.998f, 30.02f), new ScalingPoints(-0.001f, 0.2f, 5.503f, 539.5f));

    public static CalibrationDialog OpenCalibration(SimulatedRig rig)
    {
        var dialog = new CalibrationDialog("NR-500", "今日の校正", Template,
            owner => RunReading(rig, AutoCalibrationKind.Current, null, owner).CurrentResult,
            (model, owner) => RunReading(rig, AutoCalibrationKind.Pressure, model, owner).PressureResult,
            "初期値は雛形の値です。読み取るか、実測した値を入力してください。") { Template = Template };
        dialog.Text += "【画面確認用・機器なし】";
        dialog.StartPosition = FormStartPosition.CenterScreen;
        return dialog;
    }

    private static AutoCalibrationDialog RunReading(SimulatedRig rig, AutoCalibrationKind kind, ProductModel? model, IWin32Window owner)
    {
        var marker = new CalibrationInProgressMarker(Path.Combine(Path.GetTempPath(), "AS110AllCheck-preview", "校正中.txt"));
        using var dialog = new AutoCalibrationDialog(kind, model,
            calibrationOperator => new AutoCalibrationProcedure(rig, rig, rig, rig,
                new SimulatedValves(new ManualBallValveSwitcher(calibrationOperator), rig, model), calibrationOperator, new SystemCalibrationClock()),
            marker, _ => { });
        dialog.Text += "【画面確認用・機器なし】";
        dialog.ShowDialog(owner);
        return dialog;
    }
}

// 模擬の計器。発振器の電圧 → 電流(1 V ≒ 6 mA、少しずれている)→ NR-500 CH1 の電圧。ボールバルブの向き → GC16 の圧力 → CH2 の電圧。
// 値はそれらしく動くだけで、実機の動きを確かめるものではない。
internal sealed class SimulatedRig : IPressureGaugeLocator, ICurrentCommandOutput, ICurrentMeterLocator, IChannelVoltageMeter
{
    private readonly Random _noise = new(1);
    private double _commandVolts;
    private bool _outputOn;
    private double _pressureFrom, _pressureTarget = 539 * 0.99;
    private DateTime _pressureChangedAt = DateTime.MinValue;
    private double _zeroOffset;

    public bool MeterMissing { get; init; }
    // 最初の 1 回だけ GC16 が見つからない(USB の挿し忘れ。挿して「もう一度読み取る」を押した形)。
    public bool GaugeMissingOnce { get; set; }
    // 電流アンプの電源が切れている(電流が流れない)。② で止まって出力が止まったら入れたことにする(「再開」で続けられる形)。2026-11-25
    public bool AmplifierOffOnce { get; set; }
    public DateTimeOffset? FirstConfirmedAt { get; private set; }

    private double Milliamps => (_outputOn && !AmplifierOffOnce ? _commandVolts * 5.97 + 0.14 : 0) + Noise(0.004);
    // GC16 の中の圧力(ゼロ調整の前の値)。向きを替えると 1 秒ほどで落ち着く。
    private double RawKilopascals
    {
        get
        {
            double seconds = (DateTime.Now - _pressureChangedAt).TotalSeconds;
            return _pressureTarget + (_pressureFrom - _pressureTarget) * Math.Exp(-seconds / 0.5) + Noise(0.02);
        }
    }
    private double Noise(double size) => (_noise.NextDouble() - 0.5) * 2 * size;

    public void SetPressureTarget(double kilopascals)
    {
        _pressureFrom = RawKilopascals;
        _pressureTarget = kilopascals;
        _pressureChangedAt = DateTime.Now;
    }

    // 発振器
    public void SetVolts(double volts) { _commandVolts = volts; _outputOn = true; }
    public bool CanAdjust => true;
    public void AdjustVolts(double volts) => _commandVolts = volts;
    public void Stop()
    {
        if (_outputOn && _commandVolts > 1) AmplifierOffOnce = false;
        _outputOn = false;
    }

    // マルチメータ
    async Task<ICurrentMeter> ICurrentMeterLocator.FindAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(800, cancellationToken);
        if (MeterMissing) throw new InvalidOperationException("7351E が USB に見つかりません（模擬）。");
        return new Meter(this);
    }

    private sealed class Meter(SimulatedRig rig) : ICurrentMeter
    {
        public string Name => "7351E（模擬）";
        public async Task<double> ReadMilliampsAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(300, cancellationToken);
            return Math.Round(rig.Milliamps, 4);
        }
        public void Dispose() { }
    }

    // GC16
    async Task<IPressureGauge> IPressureGaugeLocator.FindAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(800, cancellationToken);
        if (GaugeMissingOnce)
        {
            GaugeMissingOnce = false;
            throw new InvalidOperationException("GC16 が見つかりません。電源・変換ケーブル・本体の通信設定(S-1 が rS)を確認してください。（模擬）");
        }
        FirstConfirmedAt ??= DateTimeOffset.Now.AddMinutes(-12);
        return new Gauge(this);
    }

    private sealed class Gauge(SimulatedRig rig) : IPressureGauge
    {
        public string PortName => "COM7（模擬）";
        public async Task<double> ReadKilopascalsAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(100, cancellationToken);
            return Math.Round(rig.RawKilopascals - rig._zeroOffset, 2);
        }
        public async Task ZeroAdjustAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(300, cancellationToken);
            rig._zeroOffset = rig._pressureTarget;
        }
        public void Dispose() { }
    }

    // NR-500(LOGGER)
    public Task PrepareAsync(CancellationToken cancellationToken) => Task.Delay(500, cancellationToken);
    public async Task<double> ReadVoltsAsync(int channel, CancellationToken cancellationToken)
    {
        await Task.Delay(1300, cancellationToken);
        return channel == 1 ? Milliamps / 30.06 - 0.002 : RawKilopascals / 98.04 + 0.001;
    }
}

// ボールバルブは実物と同じく人に切り替えてもらう(指示と OK は実物の画面)。OK のあと、模擬の圧力をその向きの値へ動かす。
internal sealed class SimulatedValves(IValveSwitcher manual, SimulatedRig rig, ProductModel? model) : IValveSwitcher
{
    public async Task SetAtmosphereAsync(CancellationToken cancellationToken)
    {
        await manual.SetAtmosphereAsync(cancellationToken);
        rig.SetPressureTarget(0.35);
    }

    public async Task SetSupplyAsync(CancellationToken cancellationToken)
    {
        await manual.SetSupplyAsync(cancellationToken);
        rig.SetPressureTarget((model?.SupplyPressure ?? 539) * 0.996);
    }

    public Task RestoreForMeasurementAsync() => manual.RestoreForMeasurementAsync();
}

// 人が押さずに一通り進めて、各場面の画像を残す。読み取りの画面はモーダルで開くので、タイマーから押す。
internal static class Screenshots
{
    private static string _folder = "";
    private static int _count;
    private static readonly HashSet<string> Seen = [];
    private static DateTime _autoShownAt;
    private static Form? _autoDialog;
    private static bool _abortAtResume;

    public static int Run(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
        foreach (string old in Directory.GetFiles(folder, "*.png")) File.Delete(old);
        using var timer = new System.Windows.Forms.Timer { Interval = 150 };
        timer.Tick += (_, _) => DriveReadingDialog();
        timer.Start();

        var rig = new SimulatedRig();
        using (var dialog = Preview.OpenCalibration(rig))
        {
            ShowOffScreen(dialog);
            Capture(dialog, "校正画面_最初（手順1が次）");
            Button("マルチメータで読み取る", dialog).PerformClick();
            Capture(dialog, "校正画面_電流のあと（型式を選ぶ）");
            Descendants(dialog).OfType<ComboBox>().Single().SelectedIndex = 1;
            Application.DoEvents();
            Capture(dialog, "校正画面_型式を選んだあと（GC16が次）");
            Button("GC16 で読み取る", dialog).PerformClick();
            Capture(dialog, "校正画面_圧力のあと（適用が次）");
        }
        using (var dialog = Preview.OpenCalibration(new SimulatedRig { MeterMissing = true }))
        {
            ShowOffScreen(dialog);
            Seen.Clear();
            Button("マルチメータで読み取る", dialog).PerformClick();
        }
        // GC16 の USB を挿し忘れて「再開・中止」が出たところで「中止」を押し、止まった画面の「もう一度読み取る」で続ける(2026-11-24)。
        using (var dialog = Preview.OpenCalibration(new SimulatedRig { GaugeMissingOnce = true }))
        {
            ShowOffScreen(dialog);
            Seen.Clear();
            _abortAtResume = true;
            Descendants(dialog).OfType<ComboBox>().Single().SelectedIndex = 1;
            Application.DoEvents();
            Button("GC16 で読み取る", dialog).PerformClick();
            Capture(dialog, "校正画面_GC16をやり直したあと");
        }
        // ② で電流アンプの電源が切れていて止まる(「再開・中止」の文を残し、「再開」で続ける。2026-11-25)。
        using (var dialog = Preview.OpenCalibration(new SimulatedRig { AmplifierOffOnce = true }))
        {
            ShowOffScreen(dialog);
            Seen.Clear();
            Button("マルチメータで読み取る", dialog).PerformClick();
            Capture(dialog, "校正画面_アンプの電源を入れて再開したあと");
        }
        Console.WriteLine($"{_count} 枚の画像を残しました：{folder}");
        return 0;
    }

    // 読み取りの画面が開いていたら、場面ごとに画像を残して先へ進める(指示が出たら最初のボタン、電流の入力には値を入れて OK)。
    private static void DriveReadingDialog()
    {
        var dialog = Application.OpenForms.OfType<AutoCalibrationDialog>().FirstOrDefault();
        if (dialog is null) { _autoDialog = null; return; }
        if (!ReferenceEquals(dialog, _autoDialog))
        {
            _autoDialog = dialog;
            _autoShownAt = DateTime.Now;
            dialog.Location = new Point(-10000, -10000);
            return;
        }
        string kind = dialog.Text.StartsWith("マルチメータ") ? "電流" : "圧力";
        var choices = Descendants(dialog).OfType<Button>()
            .Where(b => b.Visible && b.Enabled && b.Parent is FlowLayoutPanel { BorderStyle: BorderStyle.None } && b.Parent.Parent is FlowLayoutPanel { BorderStyle: BorderStyle.FixedSingle })
            .ToArray();
        if (choices.Length == 0)
        {
            var stop = Descendants(dialog).OfType<Button>().FirstOrDefault(b => b.Text == "閉じて手入力に戻る");
            if (stop is not null)
            {
                if (Seen.Add(kind + "/止まった")) Capture(dialog, $"読み取り_{kind}_止まったとき");
                stop.PerformClick();
            }
            else if (DateTime.Now - _autoShownAt > TimeSpan.FromSeconds(1.2) && Seen.Add(kind + "/自動"))
                Capture(dialog, $"読み取り_{kind}_自動で進んでいるとき");
            return;
        }
        string texts = string.Join("・", choices.Select(b => b.Text));
        // 「再開・中止」で中止を押す場面(止まった画面を出すため。1 回だけ)。
        if (_abortAtResume && choices.Any(b => b.Text == "再開"))
        {
            _abortAtResume = false;
            Capture(dialog, $"読み取り_{kind}_再開・中止（中止を押す）");
            choices.First(b => b.Text == "中止").PerformClick();
            return;
        }
        // 止まった画面の「もう一度読み取る」は 1 回だけ押す(2 回目も止まったら閉じる。押し続けて終わらなくならないように)。
        if (texts == AutoCalibrationDialog.RetryLabel && !Seen.Add(kind + "/やり直した"))
        {
            Capture(dialog, $"読み取り_{kind}_やり直しても止まった");
            Descendants(dialog).OfType<Button>().First(b => b.Text == "閉じて手入力に戻る").PerformClick();
            return;
        }
        var input = Descendants(dialog).OfType<TextBox>().FirstOrDefault(t => !t.ReadOnly && t.Visible);
        if (Seen.Add(kind + "/" + texts + "/" + (input is not null)))
            Capture(dialog, $"読み取り_{kind}_{(input is not null ? "電流を手で入れる" : texts)}");
        if (input is not null) input.Text = Seen.Contains("入力 1 回目") ? "29.95" : "0.02";
        if (input is not null) Seen.Add("入力 1 回目");
        // マルチメータが見つからないときは「電流を手で入れる」へ進む。
        (choices.FirstOrDefault(b => b.Text == "電流を手で入れる") ?? choices[0]).PerformClick();
    }

    private static void ShowOffScreen(Form dialog)
    {
        dialog.StartPosition = FormStartPosition.Manual;
        dialog.Location = new Point(-10000, -10000);
        dialog.Show();
        Application.DoEvents();
    }

    private static Button Button(string text, Control parent) => Descendants(parent).OfType<Button>().Single(b => b.Text == text);

    private static void Capture(Form form, string name)
    {
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        string path = Path.Combine(_folder, $"{++_count:00}_{name}.png");
        bitmap.Save(path, ImageFormat.Png);
        Console.WriteLine(path);
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
