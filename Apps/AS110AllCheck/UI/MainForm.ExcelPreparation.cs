namespace PSCAutomation.Apps.AS110AllCheck;

// 初回のExcel転送で起動・作業ブック作成を待たないよう、測定前に作業ブックを開いておく。
// 失敗しても転送時に従来どおり作り直すため、ログだけを残す。
internal sealed partial class MainForm : Form
{
    private System.Windows.Forms.Timer? _excelPreparationTimer;
    private bool _excelPreparing;

    // Excel 雛形は型式ごとに「<型式名>雛形.xlsx」(例: AS110-035雛形.xlsx)。無い型式はほかの型式の判定式で判定しないよう止める
    // (2026-11-12、人の指示。それまでは「<型式名>.xlsx」が無ければ AS110-035 用の「雛形.xlsx」を使っていた)。
    private static string ExcelTemplateFileName(ProductModel model) => model.Name + "雛形.xlsx";

    private static string ExcelTemplatePathFor(ProductModel model)
    {
        string path = Path.Combine(TemplateFolderPath, ExcelTemplateFileName(model));
        if (!File.Exists(path))
            throw new FileNotFoundException($"{ExcelTemplateFileName(model)} がありません。雛形フォルダーに置いてください。", path);
        return path;
    }

    // いま雛形フォルダーにある Excel 雛形(転送設定のシート名を確かめるのに使う)。出荷データの型式は専用の雛形を持たないので、あるものだけ。
    private static string[] ExistingExcelTemplates() =>
        ProductModel.All.Select(m => Path.Combine(TemplateFolderPath, ExcelTemplateFileName(m))).Where(File.Exists).ToArray();

    // 出荷データ(AS110-116B350)は判定した型式(AS110-116B)の雛形に転送する(TemplateModel)。
    private string ExcelTemplatePath => ExcelTemplatePathFor(_flow.TemplateModel);

    private string ExcelWorkPath => Path.Combine(_sessionFolder, "work.xlsx");

    // このアプリが起動した Excel を一時フォルダーに覚える(異常終了で Excel が残ったら、次の起動で終了する。SessionFolders)。覚えられなくても動きは変えない。
    private int _recordedExcelProcessId;
    private void RecordExcelProcess()
    {
        try
        {
            if (_excel?.ExcelProcessId is not { } id || id == _recordedExcelProcessId) return;
            SessionFolders.WriteExcelProcess(_sessionFolder, id);
            _recordedExcelProcessId = id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    // 使っている一時フォルダーの印(「使用中.lock」を開いたまま持つ)。終了のときに手放してからフォルダーを消す。
    private IDisposable? _sessionFolderLock, _workingTemplateFolderLock;
    private void HoldTemporaryFolders()
    {
        _sessionFolderLock = SessionFolders.Hold(_sessionFolder);
        _workingTemplateFolderLock = SessionFolders.Hold(_workingTemplateFolder);
    }
    private void ReleaseTemporaryFolderLocks()
    {
        _sessionFolderLock?.Dispose(); _workingTemplateFolderLock?.Dispose();
        _sessionFolderLock = _workingTemplateFolderLock = null;
    }

    // 前の起動が異常終了で残した一時フォルダーと、画面に出ていない Excel を片づける(起動を待たせないよう裏で行い、したことをログに出す)。
    private async Task CleanupLeftoversAsync()
    {
        try
        {
            var now = DateTime.Now;
            var notes = await Task.Run(() => new[] { _sessionFolder, _workingTemplateFolder }
                .SelectMany(own => SessionFolders.Cleanup(Path.GetDirectoryName(own)!, [own], now)).ToList());
            if (IsDisposed || _closeRequested) return;
            foreach (string note in notes) AppendLog("起動時の後片付け：" + note);
        }
        catch (Exception ex) { try { if (!IsDisposed && !_closeRequested) AppendLog("起動時の後片付けに失敗：" + ex.Message); } catch { } }
    }

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
            _excel = await ExcelWorkSession.PrepareAsync(ExcelTemplatePath, ExcelWorkPath, ModelTransferSettings, _flow.Model,
                _flow.Mode == TestMode.Production ? _flow.Serial : "-", _excel, AppendLog);
        }
        catch (Exception ex) when (!_closeRequested)
        {
            AppendLog($"Excel事前準備：失敗 / {ex.Message} / Excel転送時に通常どおり作成します。");
        }
        catch (Exception ex) { RecordShutdownError("Excel事前準備", ex); }
        finally
        {
            RecordExcelProcess();
            _excelPreparing = _operationInProgress = false;
            ContinueRequestedClose();
        }
    }
}
