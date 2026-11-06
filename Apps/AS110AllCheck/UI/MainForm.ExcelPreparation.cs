namespace PSCAutomation.Apps.AS110AllCheck;

// 初回のExcel転送で起動・作業ブック作成を待たないよう、測定前に作業ブックを開いておく。
// 失敗しても転送時に従来どおり作り直すため、ログだけを残す。
internal sealed partial class MainForm : Form
{
    private System.Windows.Forms.Timer? _excelPreparationTimer;
    private bool _excelPreparing;

    private string ExcelTemplatePath
    {
        get
        {
            string specific = Path.Combine(TemplateFolderPath, _flow.Model.Name + ".xlsx");
            return File.Exists(specific) ? specific : Path.Combine(TemplateFolderPath, "雛形.xlsx");
        }
    }

    private string ExcelWorkPath => Path.Combine(_sessionFolder, "work.xlsx");

    private void RequestExcelPreparation()
    {
        if (_closeRequested || IsDisposed || _flow.Mode == TestMode.Motion) return;
        if (_excelPreparationTimer is null)
        {
            // 呼び出し元の操作が終わってから開始する。
            _excelPreparationTimer = new System.Windows.Forms.Timer { Interval = 500 };
            _excelPreparationTimer.Tick += async (_, _) => await PrepareExcelWhenIdleAsync();
        }
        _excelPreparationTimer.Start();
    }

    private async Task PrepareExcelWhenIdleAsync()
    {
        // 測定結果がある間は、その転送・確認用のブックを閉じない。
        // 測定開始S/N が空のまま準備すると見出しの「S/N 数字」が空になり、後で置き換えられない。入るまで待つ(入力後に再要求する)。
        if (_closeRequested || IsDisposed || _flow.Mode == TestMode.Motion || _flow.HasCapture || _reviewStage || _flow.SerialMissing)
        {
            _excelPreparationTimer?.Stop();
            return;
        }
        // 測定・他の操作中はUIを止めないよう、操作が終わるまで待つ。
        if (_excelPreparing || _operationInProgress || _flow.Measuring || _leakRunning) return;
        _excelPreparationTimer?.Stop();
        _excelPreparing = _operationInProgress = true;
        UpdateControls();
        try
        {
            _excel ??= new ExcelWorkSession();
            _excel = await ExcelWorkSession.PrepareAsync(ExcelTemplatePath, ExcelWorkPath, _transferSettings, _flow.Model,
                _flow.Mode == TestMode.Production ? _flow.Serial : "-", _excel, AppendLog);
        }
        catch (Exception ex) when (!_closeRequested)
        {
            AppendLog($"Excel事前準備：失敗 / {ex.Message} / Excel転送時に通常どおり作成します。");
        }
        catch (Exception ex) { RecordShutdownError("Excel事前準備", ex); }
        finally
        {
            _excelPreparing = _operationInProgress = false;
            ContinueRequestedClose();
        }
    }
}
