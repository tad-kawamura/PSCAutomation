using System.Runtime.InteropServices;

namespace PSCAutomation.Apps.AS100201SwitchController;

// Files: 画面・接続・操作を役割ごとに分割する。
internal sealed partial class MainForm : Form
{
    private void TemplateDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = ConnectionsReady && !_operationInProgress && !_templateLoaded &&
            e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths &&
            File.Exists(paths[0]) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void TemplateDragDrop(object? sender, DragEventArgs e)
    {
        if (!ConnectionsReady || _templateLoaded || _operationInProgress ||
            e.Data?.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } paths)
            return;

        RunOperation("雛形読み込み", () => LoadTemplate(paths[0]));
    }

    // 雛形を LOGGER で開いて登録する。ドロップと、学習モードの自動読み込み(MainForm.Learning.cs)で使う。
    private string LoadTemplate(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("雛形が見つかりません。", path);
        // 学習モード: 測定中の取り込みに使うチャンネルと換算係数を、LOGGER で開く前に読む(通常モードでは何もしない)。
        PrepareLiveCaptureTemplate(path);
        // 対応形式は、選択した LOGGER の OpenFile の戻り値で判定する。
        long result = _logger.OpenFile(path);
        if (result != 0) throw new InvalidOperationException($"WAVE LOGGER が雛形を開けませんでした（戻り値 {result}）。");
        _templatePath = path;
        _templateLoaded = true;
        _measurementState.ReadAfter = 0;
        RefreshLoggerState();
        RefreshReadyLed();
        CompletePreparationDisplay();
        return $"成功 / {Path.GetFileName(path)}";
    }

    private void DeleteTemplateClicked(object? sender, EventArgs e)
    {
        RunOperation("雛形削除", () =>
        {
            RefreshLoggerState();
            if (_measurementStarted)
            {
                _logger.Stop();
                _measurementStarted = false;
            }
            _logger.CloseActiveFile();
            ResetTemplate();
            return "登録を解除しました。";
        });
    }

    private void ResetTemplate()
    {
        KeepPreparationInFront();
        _templateLoaded = false;
        _templatePath = null;
        ClearLiveCaptureTemplate();
        _measurementState.State = null;
        _measurementState.MeasurementStarted = false;
        _measurementState.ReadAfter = 0;
        RefreshReadyLed();
        UpdateControls();
    }

    private void RefreshReadyLed()
    {
        bool ready = !_closing && _templateLoaded && ConnectionsReady && !_operationInProgress && LearningReady;
        if (ready == _readyLedOn) return;
        try
        {
            _arduino.SetReadyLed(ready);
            _readyLedOn = ready;
        }
        catch (Exception ex)
        {
            _readyLedOn = false;
            AppendLog("準備完了 LED の通信に失敗: " + ex.Message);
            ArduinoDisconnected();
        }
    }
}
