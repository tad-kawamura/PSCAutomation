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

        RunOperation("雛形読み込み", () =>
        {
            if (!File.Exists(paths[0])) throw new FileNotFoundException("雛形が見つかりません。", paths[0]);
            // 対応形式は、選択した LOGGER の OpenFile の戻り値で判定する。
            long result = _logger.OpenFile(paths[0]);
            if (result != 0) throw new InvalidOperationException($"WAVE LOGGER が雛形を開けませんでした（戻り値 {result}）。");
            _templatePath = paths[0];
            _templateLoaded = true;
            _measurementState.ReadAfter = 0;
            RefreshLoggerState();
            RefreshReadyLed();
            CompletePreparationDisplay();
            return $"成功 / {Path.GetFileName(paths[0])}";
        });
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
        _measurementState.State = null;
        _measurementState.MeasurementStarted = false;
        _measurementState.ReadAfter = 0;
        RefreshReadyLed();
        UpdateControls();
    }

    private void RefreshReadyLed()
    {
        bool ready = !_closing && _templateLoaded && ConnectionsReady && !_operationInProgress;
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
