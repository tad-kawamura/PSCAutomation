using System.Runtime.InteropServices;

namespace PSCAutomation.Apps.AS100201SwitchController;

// Measurement: 画面・接続・操作を役割ごとに分割する。
internal sealed partial class MainForm : Form
{
    private void RefreshLoggerState()
    {
        bool wasMeasuring = _measurementStarted;
        if (_measurementState.Refresh(_logger.GetState, AppendLog))
            AppendLog("WAVE LOGGER 測定終了：次の測定を開始できます。");
        // 学習モード: 測定中だった LOGGER が停止(エラー停止を含む)に戻ったら xdt を保存する。通常モードでは何もしない。
        if (wasMeasuring && _measurementState.ReadyToStart) LearningMeasurementCompleted();
    }

    private void RefreshTemplateState()
    {
        if (_templateLoaded && !_logger.HasActiveFile())
        {
            ResetTemplate();
            AppendLog("LOGGER側でファイルが閉じられました。雛形を再登録してください。");
        }
    }

    private void StartMeasurement()
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        long result = _logger.Start();
        if (result != 0)
            throw new InvalidOperationException($"WAVE LOGGER の測定開始に失敗（戻り値 {result}）。");
        _measurementStarted = true;
        _measurementState.State = 2;
        _measurementState.ReadAfter = Environment.TickCount64 + 500;
        AppendLog("WAVE LOGGER 測定開始");
        AppendLog($"処理時間：LOGGER測定開始 = {timer.Elapsed.TotalMilliseconds:F1} ms");
    }

    private void StartClicked(object? sender, EventArgs e)
    {
        ExecuteSwitch(As100201Switch.Parse("F8"));
    }

    private void GetStateClicked(object? sender, EventArgs e)
    {
        RunOperation("状態確認", () =>
        {
            RefreshLoggerState();
            return $"GetState() = {_measurementState.State} / {_measurementState.GetDisplayText(describeHardwareStop: false)}";
        });
    }
}
