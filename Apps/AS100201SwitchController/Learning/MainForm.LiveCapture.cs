using System.Diagnostics;

namespace PSCAutomation.Apps.AS100201SwitchController;

// 測定中の取り込みと加工(SPEC_測定中の取り込み.md、2026-11-11 承認)の画面への組み込み。学習モードだけ。
// 通常モードでは _capture が null で、ここは何もしない(タイマーも作らない)。
//   - 雛形を開く前に雛形の xdt からチャンネルと換算係数を読む(読めなければ F8 を受け付けない)
//   - F8 で測定を始めたら、0.2 秒ごとに LOGGER から続きの点を取り出して加工する(画面には出さない)
//   - 取り込みに失敗したら、LOGGER を止めて測定の行(停止のきっかけ = 取り込み異常)と xdt・そこまでの CSV を残し、最前面の窓で知らせる
//   - LOGGER が停止に戻ったら、残りの点を取り出して、xdt の後に同じ名前の CSV を書く
internal sealed partial class MainForm : Form
{
    private As100201LiveCapture? _capture;
    private As100201CaptureTemplate? _captureTemplate;
    private string? _captureTemplateError;
    private System.Windows.Forms.Timer? _captureTimer;
    private bool _captureTickBusy;

    private void InitializeLiveCapture()
    {
        if (!IsLearningMode) return;
        _capture = new As100201LiveCapture(_logger.ReadRawData);
        _captureTimer = new System.Windows.Forms.Timer { Interval = As100201LiveCapture.IntervalMilliseconds };
        _captureTimer.Tick += CaptureTick;
        _captureTimer.Start();
        AppendLog($"学習モード：測定中は {As100201LiveCapture.IntervalMilliseconds}ms ごとに LOGGER から値を取り込み、加工した値を xdt と同じ名前の CSV に残します（画面には出しません）。");
    }

    private void DisposeLiveCapture()
    {
        if (_captureTimer is null) return;
        _captureTimer.Stop();
        _captureTimer.Tick -= CaptureTick;
        _captureTimer.Dispose();
        _captureTimer = null;
    }

    // 雛形を LOGGER で開く前に、雛形の xdt からチャンネルと換算係数を読む(開いた後は LOGGER がファイルを使っているため)。
    private void PrepareLiveCaptureTemplate(string path)
    {
        if (_capture is null) return;
        _captureTemplate = null;
        _captureTemplateError = null;
        try
        {
            _captureTemplate = As100201CaptureTemplate.Load(path);
            AppendLog($"測定中の取り込み：雛形のチャンネル {string.Join("・", _captureTemplate.Channels.Select(c => c.Label))}、周期 {_captureTemplate.IntervalSeconds * 1000:0.###}ms");
        }
        catch (Exception ex)
        {
            _captureTemplateError = ex.Message;
            AppendLog("測定中の取り込み：雛形からチャンネルと換算係数を読めません：" + ex.Message + "（この雛形では測定を始められません）");
        }
    }

    private void ClearLiveCaptureTemplate()
    {
        if (_capture is null) return;
        _captureTemplate = null;
        _captureTemplateError = null;
        if (_capture.Active)
        {
            _capture.Abandon();
            AppendLog("測定中の取り込み：雛形の登録を解除したので、この測定の取り込みは残しません。");
        }
    }

    // F8: 測定を始める前。雛形の情報が無ければ始めない(学習モードだけ)。
    private void EnsureLiveCaptureReady()
    {
        if (_capture is null) return;
        if (_captureTemplate is null)
            throw new InvalidOperationException("雛形からチャンネルと換算係数を読めないため、測定中の取り込みができません。測定を始めません。" +
                (_captureTemplateError is null ? "" : "（" + _captureTemplateError + "）") + " 雛形を登録し直してください。");
    }

    // F8: LOGGER の測定を始めた直後。
    private void BeginLiveCapture()
    {
        if (_capture is null || _captureTemplate is null) return;
        _capture.Begin(_captureTemplate, Stopwatch.GetTimestamp());
    }

    private void CaptureTick(object? sender, EventArgs e)
    {
        if (_capture is not { Active: true } capture || _closing || IsDisposed || _captureTickBusy) return;
        long now = Stopwatch.GetTimestamp();
        if (capture.Failed) return;
        if (_operationInProgress || !_logger.IsInitialized || !_templateLoaded)
        {
            capture.Touch(now);
            return;
        }
        if (!_measurementStarted)
        {
            // 停止に戻ったのに LearningMeasurementCompleted を通らなかった(確認ボタンなど、記録しない止め方)。
            capture.Abandon();
            AppendLog("測定中の取り込み：記録しない形で測定が終わったので、取り込みは残しません。");
            return;
        }
        _captureTickBusy = true;
        string? failure;
        try
        {
            using var observation = PcDelayObservation.Begin("UI.LiveCapture");
            long state;
            try { state = _logger.GetState(); }
            catch (Exception ex)
            {
                CaptureFailed("LOGGER の状態を読めません：" + ex.Message);
                return;
            }
            var (side, _, phase) = DescribeCurrentWaveform();
            failure = capture.Tick(state, now, side, phase);
        }
        finally { _captureTickBusy = false; }
        if (failure is not null) CaptureFailed(failure);
    }

    // 取り込みに失敗した。測定も止めて知らせる(人の決定)。そこまでの行と xdt は残す。
    private void CaptureFailed(string reason)
    {
        AppendLog("測定中の取り込みに失敗：" + reason);
        ArduinoDiagnosticLog.Write(As100201Application.LiveCaptureFailedIncident, reason);
        try
        {
            if (_measurementStarted && _logger.IsInitialized)
            {
                _logger.Stop();
                _measurementState.ReadAfter = 0;
                AppendLog("取り込み異常のため WAVE LOGGER へ停止を要求しました。");
                LearningMeasurementStopped(As100201LearningSession.StopByCapture);
                RefreshLoggerState();
            }
        }
        catch (Exception ex)
        {
            AppendLog("取り込み異常のあとの停止に失敗：" + ex.Message);
        }
        RefreshReadyLed();
        UpdateControls();
        if (IsHandleCreated && !_closing && !IsDisposed)
        {
            BeginInvoke(new Action(() =>
            {
                if (_closing || IsDisposed) return;
                ErrorNotification.Show(this,
                    "測定中の取り込みに失敗したので、測定を止めました。\r\n\r\n理由：" + reason +
                    "\r\n\r\nここまでの波形（xdt）と台帳の行（停止のきっかけ：" + As100201LearningSession.StopByCapture + "）、取り込みの CSV は残します。" +
                    "\r\n次の F8 でまた取り込みを試します。続けて起きるときは、LOGGER と NR 本体の USB を確かめてください。",
                    "学習モード：測定中の取り込みに失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }));
        }
    }

    // LOGGER が停止に戻った。残りの点を取り出して表を返す(取り込んでいなければ null)。
    private As100201FeatureTable? FinishLiveCapture(string side, string phase)
    {
        if (_capture is not { Active: true } capture) return null;
        As100201FeatureTable? table;
        string? problem;
        using (PcDelayObservation.Begin("UI.LiveCaptureFinish"))
            table = capture.Finish(Stopwatch.GetTimestamp(), side, phase, out problem);
        if (problem is not null) AppendLog("測定中の取り込み：停止後の残りを取り出せません：" + problem + "（そこまでの行を残します）");
        if (table is not null)
            AppendLog($"測定中の取り込み：{capture.TickCount} 回、{capture.PointCount} 点、{table.Rows.Count} 行" +
                (capture.FailureReason is null ? "" : "（途中で失敗：" + capture.FailureReason + "）"));
        return table;
    }
}
