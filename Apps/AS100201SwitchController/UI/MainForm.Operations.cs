using System.Runtime.InteropServices;

namespace PSCAutomation.Apps.AS100201SwitchController;

// Operations: 画面・接続・操作を役割ごとに分割する。
internal sealed partial class MainForm : Form
{
    private void RunOperation(string operationName, Func<string> operation)
    {
        using var observation = PcDelayObservation.Begin("UI.Operation:" + operationName);
        try
        {
            _operationInProgress = true;
            UpdateControls();
            UseWaitCursor = true;
            string result = operation();
            AppendLog($"{operationName}：{result}");
        }
        catch (COMException ex)
        {
            if (_closing) return;
            AppendLog($"{operationName}：COMエラー 0x{ex.ErrorCode:X8} / {ex.Message}");
            ErrorNotification.Show(
                this,
                ex.Message,
                $"{operationName}でCOMエラー",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            if (_closing) return;
            AppendLog($"{operationName}：エラー / {ex.Message}");
            ErrorNotification.Show(
                this,
                ex.Message,
                $"{operationName}でエラー",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            if (!_closing) UseWaitCursor = false;
            _operationInProgress = false;
            if (!_closing) RefreshReadyLed();
            UpdateControls();
        }
    }

    private async Task RunOperationAsync(
        string operationName,
        Func<Task<string>> operation)
    {
        using var observation = PcDelayObservation.Begin("UI.AsyncOperation:" + operationName);
        try
        {
            _operationInProgress = true;
            UpdateControls();
            UseWaitCursor = true;
            string result = await operation();
            AppendLog($"{operationName}：{result}");
        }
        catch (COMException ex)
        {
            if (_closing) return;
            AppendLog($"{operationName}：COMエラー 0x{ex.ErrorCode:X8} / {ex.Message}");
            ErrorNotification.Show(
                this,
                ex.Message,
                $"{operationName}でCOMエラー",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            if (_closing) return;
            AppendLog($"{operationName}：エラー / {ex.Message}");
            ErrorNotification.Show(
                this,
                ex.Message,
                $"{operationName}でエラー",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            if (!_closing) UseWaitCursor = false;
            _operationInProgress = false;
            if (!_closing) RefreshReadyLed();
            UpdateControls();
        }
    }

    private void AppendLog(string message)
    {
        if (_closing || IsDisposed) return;
        ArduinoDiagnosticLog.Write("APP", message);
        _logTextBox.AppendText($"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
    }
}
