namespace PSCAutomation.Apps.AS110AllCheck;

// 校正の自動読み取り(段階 1。2026-11-13、SPEC「校正の自動読み取り」)。校正画面の「GC16 で読み取る」から呼ぶ。
// 機器は接続確認でつないだものをそのまま使う(発振器・LOGGER)。GC16 は M5Stack のポートを除いて探す。弁は人がボールバルブを切り替える。
// 読み取りが終わったら(成功・中止・失敗とも)発振器をバーストの設定に戻す(読み取りで DC にしたため)。出力を止められなければ「出力状態不明」にする。
// 途中でアプリが落ちたときは、次の接続確認のあとで後片付けを知らせる(RecoverInterruptedCalibrationAsync)。
internal sealed partial class MainForm : Form
{
    private Gc16GaugeLocator? _gaugeLocator;
    // 校正の読み取りのために LOGGER で雛形を開いた(校正画面をキャンセルしたら閉じる)。
    private bool _templateOpenedForReading;
    // 開いている読み取りの画面(M5Stack の通信が切れたら中止するため。MainForm.Measurement.cs の ArduinoDisconnected)。
    private AutoCalibrationDialog? _autoCalibrationDialog;

    // この起動で GC16 を最初に見つけた時刻を持ち続けるため、1 つを使い回す。
    private Gc16GaugeLocator GaugeLocator => _gaugeLocator ??= new Gc16GaugeLocator(() => _arduino.PortName);

    private AutoCalibrationResult? ReadCalibrationWithGauge(ProductModel model, string templatePath, IWin32Window owner)
    {
        var generator = _generator;
        if (generator is null || !ConnectionsReady)
        {
            ErrorNotification.Show(owner, "発振器・LOGGER の接続を確認できません。接続確認をしてから読み取るか、8 項目を手で入れてください。",
                "GC16 で読み取れません", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        var meter = new LoggerChannelVoltageMeter(_logger, () => templatePath);
        AppendLog($"校正の読み取り：開始 / 型式 {model.Name}（目標 {model.SupplyPressure} kPa）");
        using var dialog = new AutoCalibrationDialog(model,
            calibrationOperator => new AutoCalibrationProcedure(GaugeLocator, new GeneratorCurrentOutput(generator), meter,
                new ManualBallValveSwitcher(calibrationOperator), calibrationOperator, new SystemCalibrationClock()),
            CalibrationInProgressMarker.Default, line => AppendLog("校正の読み取り：" + line));
        _autoCalibrationDialog = dialog;
        try
        {
            dialog.ShowDialog(owner);
        }
        finally
        {
            _autoCalibrationDialog = null;
            if (meter.OpenedTemplate) _templateOpenedForReading = true;
            if (dialog.Started) RestoreGeneratorAfterReading(generator, dialog.OutputStopFailed);
        }
        AppendLog(dialog.Result is { } result
            ? "校正の読み取り：終了 / " + result.Calibration.Summary + " / 0 点 " + result.Zero.Describe()
            : "校正の読み取り：終了（値なし。手入力で校正できます）");
        return dialog.Result;
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

    // 校正画面をキャンセルしたとき、読み取りのために開いた雛形を閉じる(適用していないファイルを LOGGER に残さない)。
    private void CloseTemplateOpenedForReading()
    {
        if (!_templateOpenedForReading) return;
        _templateOpenedForReading = false;
        try
        {
            if (!_calibrationApplied && _logger.HasActiveFile())
            {
                long result = _logger.CloseActiveFile();
                AppendLog($"校正の読み取りのために開いた雛形を閉じました（戻り値 {result}）。");
            }
        }
        catch (Exception ex) { AppendLog("校正の読み取りのために開いた雛形を閉じられません：" + ex.Message); }
    }

    // 前回、校正の読み取りの途中でアプリが終わっていたら(校正中の印が残っている)、接続確認のあとで後片付けをして知らせる(決まったこと 19)。
    // WF1973 の出力を止め、GC16 が見つかれば RM,0 で前面キーが効く状態に戻し、ボールバルブを測定の向きに戻すよう出す。
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
