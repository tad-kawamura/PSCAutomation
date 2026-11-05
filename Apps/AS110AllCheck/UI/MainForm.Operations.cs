using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PSCAutomation.Apps.AS110AllCheck;

// MainFormを役割別に分割。すべて同じFormの一部です。
// MainFormの各操作に共通する、実行中表示とエラー処理を担当する。
// 操作中は処理中の状態にし、ボタンと待機カーソルを更新する。
// 操作結果をログへ記録し、失敗時はエラーポップアップを表示する。
// 成功・失敗にかかわらず、最後に処理中の状態を解除する。
// 通常の処理と、完了を待つ非同期処理の両方に対応する。
internal sealed partial class MainForm : Form
{
    private bool _operationInProgress;

    private void RunOperation(string operationName, Func<string> operation)
    {
        if (_closeRequested) return;
        using var observation = PcDelayObservation.Begin("UI.Operation:" + operationName);
        try
        {
            _operationInProgress = true;
            UpdateControls();
            UseWaitCursor = true;
            string result = operation();
            AppendLog($"{operationName}：{result}");
        }
        catch (Exception ex) when (_closeRequested) { RecordShutdownError(operationName, ex); }
        catch (COMException ex)
        {
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
            UseWaitCursor = false;
            _operationInProgress = false;
            ContinueRequestedClose();
        }
    }

    private async Task RunOperationAsync(
        string operationName,
        Func<Task<string>> operation)
    {
        if (_closeRequested) return;
        using var observation = PcDelayObservation.Begin("UI.AsyncOperation:" + operationName);
        try
        {
            _operationInProgress = true;
            UpdateControls();
            UseWaitCursor = true;
            string result = await operation();
            AppendLog($"{operationName}：{result}");
        }
        catch (Exception ex) when (_closeRequested) { RecordShutdownError(operationName, ex); }
        catch (COMException ex) when (operationName == "Excel転送" &&
            ex.ErrorCode is unchecked((int)0x80010001) or unchecked((int)0x8001010A) or unchecked((int)0x800AC472))
        {
            AppendLog($"{operationName}：Excel操作受付待ち 0x{ex.ErrorCode:X8} / {ex.Message}");
            ErrorNotification.Show(
                this,
                "Excelの入力をEscで解除してから、もう一度Excel転送してください",
                "Excel転送できません",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        catch (COMException ex)
        {
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
            UseWaitCursor = false;
            _operationInProgress = false;
            ContinueRequestedClose();
        }
    }
}
