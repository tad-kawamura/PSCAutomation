namespace PSCAutomation.Apps.AS110AllCheck;

// 校正の自動読み取り(段階 1。2026-11-13、SPEC「校正の自動読み取り」。2026-11-18 に 2 つの読み取りに分けた)。
// 校正画面の「マルチメータで読み取る」(①②)と「GC16 で読み取る」(③④)から呼ぶ。
// 機器は接続確認でつないだものをそのまま使う(発振器・LOGGER)。GC16 は M5Stack のポートを除いて探す。7351E は押したときに探す。弁は人がボールバルブを切り替える。
// NR-500 の電圧は、校正用の雛形(雛形\AS110校正用.xdt)で LOGGER に測定させて読む。最初の読み取りで開き、校正画面を閉じたら止めて閉じる(CloseCalibrationReader)。
// 読み取りが終わったら(成功・中止・失敗とも)発振器をバーストの設定に戻す(読み取りで DC にしたため)。出力を止められなければ「出力状態不明」にする。
// 途中でアプリが落ちたときは、次の接続確認のあとで後片付けを知らせる(RecoverInterruptedCalibrationAsync)。
internal sealed partial class MainForm : Form
{
    private Gc16GaugeLocator? _gaugeLocator;
    private readonly Adcmt7351ELocator _currentMeterLocator = new();
    // 校正画面のあいだだけ使う、校正用の雛形での LOGGER の測定。
    private CalibrationLoggerReader? _calibrationReader;
    // 開いている読み取りの画面(M5Stack の通信が切れたら中止するため。MainForm.Measurement.cs の ArduinoDisconnected)。
    private AutoCalibrationDialog? _autoCalibrationDialog;

    // この起動で GC16 を最初に見つけた時刻を持ち続けるため、1 つを使い回す。
    private Gc16GaugeLocator GaugeLocator => _gaugeLocator ??= new Gc16GaugeLocator(() => _arduino.PortName);

    private static string CalibrationTemplatePath => Path.Combine(TemplateFolderPath, CalibrationLoggerReader.TemplateFileName);

    private CurrentReadingResult? ReadCurrentWithMeter(IWin32Window owner) =>
        RunAutoReading(AutoCalibrationKind.Current, null, owner).Current;

    private PressureReadingResult? ReadPressureWithGauge(ProductModel model, IWin32Window owner) =>
        RunAutoReading(AutoCalibrationKind.Pressure, model, owner).Pressure;

    private (CurrentReadingResult? Current, PressureReadingResult? Pressure) RunAutoReading(AutoCalibrationKind kind, ProductModel? model, IWin32Window owner)
    {
        var generator = _generator;
        string name = kind == AutoCalibrationKind.Current ? "マルチメータで" : "GC16 で";
        if (generator is null || !ConnectionsReady)
        {
            ErrorNotification.Show(owner, "発振器・LOGGER の接続を確認できません。接続確認をしてから読み取るか、値を手で入れてください。",
                name + "読み取れません", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return (null, null);
        }
        if (!File.Exists(CalibrationTemplatePath))
        {
            ErrorNotification.Show(owner, $"NR-500 の電圧を読むための校正用の LOGGER 雛形「{CalibrationLoggerReader.TemplateFileName}」が「雛形」フォルダーにありません。\r\n" +
                "値を手で入れて「適用」してください。\r\n" + CalibrationTemplatePath, name + "読み取れません", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return (null, null);
        }
        var reader = _calibrationReader ??= new CalibrationLoggerReader(new WaveLoggerCalibrationSource(_logger), CalibrationTemplatePath,
            new SystemCalibrationClock(), line =>
            {
                if (_autoCalibrationDialog is { } shown) shown.ShowStatus(line);
                else AppendLog("校正の読み取り：" + line);
            });
        AppendLog("校正の読み取り：開始 / " + (kind == AutoCalibrationKind.Current ? "電流（マルチメータ）" : $"圧力（GC16）・型式 {model!.Name}（目標 {model.SupplyPressure} kPa）"));
        using var dialog = new AutoCalibrationDialog(kind, model,
            calibrationOperator => new AutoCalibrationProcedure(GaugeLocator, new GeneratorCurrentOutput(generator), _currentMeterLocator, reader,
                new ManualBallValveSwitcher(calibrationOperator), calibrationOperator, new SystemCalibrationClock()),
            CalibrationInProgressMarker.Default, line => AppendLog("校正の読み取り：" + line),
            // M5Stack が切れた・発振器の出力状態が分からないときは、読み取りの画面でやり直させない(「確認」でつなぎ直す)。
            canRetry: () => ConnectionsReady && ReferenceEquals(_generator, generator));
        _autoCalibrationDialog = dialog;
        try
        {
            dialog.ShowDialog(owner);
        }
        finally
        {
            _autoCalibrationDialog = null;
            if (dialog.Started) RestoreGeneratorAfterReading(generator, dialog.OutputStopFailed);
        }
        AppendLog(dialog.CurrentResult is { } current ? "校正の読み取り：終了 / 電流 " + As110Calibration.DescribePoints(current.Current, "mA") + "（" + current.MethodText + "）"
            : dialog.PressureResult is { } pressure ? "校正の読み取り：終了 / 圧力 " + As110Calibration.DescribePoints(pressure.Pressure, "kPa") + " / 0 点 " + pressure.Zero.Describe()
            : "校正の読み取り：終了（値なし。手入力で校正できます）");
        return (dialog.CurrentResult, dialog.PressureResult);
    }

    // 校正画面を閉じたとき(適用・キャンセル)、校正用の雛形の測定を止めて閉じる。開いていたら true(呼び出し側で作業用ファイルを読み直す)。
    private bool CloseCalibrationReader()
    {
        var reader = _calibrationReader;
        _calibrationReader = null;
        if (reader is null || !reader.Opened) return false;
        reader.Close();
        return true;
    }

    // 読み取りで DC 出力にした発振器を、出力 OFF・バーストの設定に戻す(モード選択のときと同じ)。
    private void RestoreGeneratorAfterReading(IFunctionGeneratorSession generator, bool stopFailed)
    {
        if (stopFailed)
        {
            _generatorOutputUnknown = true;
            WarnGeneratorOutputUnknown(GeneratorOutputUnknownMessage("校正の読み取りの後で出力OFFを送れませんでした"));
            return;
        }
        try
        {
            generator.PrepareBurst(CurrentBurstSettings);
            AppendLog($"発振器設定：{GetGeneratorModelName()} / {generator.PreparationSummary}（校正の読み取りの後）");
        }
        catch (Exception ex)
        {
            _generatorOutputUnknown = true;
            WarnGeneratorOutputUnknown(GeneratorOutputUnknownMessage("校正の読み取りの後の発振器設定に失敗：" + ex.Message));
        }
    }

    // 前回、校正の読み取りの途中でアプリが終わっていたら(校正中の印が残っている)、接続確認のあとで後片付けをして知らせる(決まったこと 19)。
    // WF1973 の出力を止め、LOGGER が校正用の雛形で測定していれば止めて閉じ、GC16 が見つかれば RM,0 で前面キーが効く状態に戻し、ボールバルブを測定の向きに戻すよう出す。
    private async Task RecoverInterruptedCalibrationAsync()
    {
        var marker = CalibrationInProgressMarker.Default;
        if (_closeRequested || !marker.Exists || !ConnectionsReady) return;
        await RunOperationAsync("校正の後片付け", async () =>
        {
            var notes = new List<string>();
            try
            {
                _generator!.StopOutput();
                _generator.PrepareBurst(CurrentBurstSettings);
                notes.Add("WF1973 の出力を止めました。");
            }
            catch (Exception ex)
            {
                _generatorOutputUnknown = true;
                notes.Add("WF1973 の出力を止められません（" + ex.Message + "）。本体で出力を OFF にしてください。");
            }
            try
            {
                // 校正の読み取りは雛形を読み込む前(今日の校正)に行うので、作業用の雛形が読み込まれていなければ、開いているのは校正用の雛形。
                if (!_calibrationApplied && _logger.HasActiveFile())
                {
                    long state = _logger.GetState();
                    if (state is not (1 or 11)) _logger.Stop();
                    _logger.CloseActiveFile();
                    notes.Add("LOGGER の測定を止め、開いていたファイルを閉じました。");
                }
            }
            catch (Exception ex)
            {
                notes.Add("LOGGER の測定を止められません（" + ex.Message + "）。LOGGER で測定を止めてください。");
            }
            string? excluded = _arduino.PortName;
            try
            {
                string port = await Task.Run(() =>
                {
                    using var gc16 = Gc16Session.Find(new Gc16SearchOptions
                    {
                        PreferredPort = Gc16PortMemory.Read(),
                        ExcludedPorts = excluded is null ? Array.Empty<string>() : new[] { excluded },
                    });
                    gc16.ExitRemote();
                    return gc16.PortName;
                });
                notes.Add($"GC16（{port}）を前面キーが効く状態に戻しました。");
            }
            catch (Exception ex)
            {
                notes.Add("GC16 を前面キーが効く状態に戻せませんでした（" + ex.Message + "）。GC16 に REMOTE と出ていれば、電源を入れ直してください。");
            }
            marker.Clear();
            ErrorNotification.Show(this,
                "前回、校正の読み取りの途中でアプリが終わっていました。\r\n\r\n" + string.Join("\r\n", notes) + "\r\n\r\n" +
                ManualBallValveSwitcher.RestoreInstruction,
                "校正の後片付け", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return string.Join(" ", notes);
        });
    }
}
