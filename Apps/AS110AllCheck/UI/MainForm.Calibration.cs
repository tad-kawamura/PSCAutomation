namespace PSCAutomation.Apps.AS110AllCheck;

// モード選択後の校正値の入力と適用を担当する(2026-11-06 にスタンド選択を廃止。人の判断)。
// 接続確認とモード選択の両方が済んだら校正画面を自動で開き、入力した校正値を雛形へ反映した作業用ファイルを LOGGER に読み込む。
// 校正画面の初期値は雛形の値(前回値は呼び出さない)。キャンセル後・入れ直しは「校正値を入力／変更」ボタンから。
// NR-X(ydt)は校正未対応(保留)なので、校正画面を出さず雛形の値のまま読み込む。
// 校正記録はデータ取りの保存先の「校正記録」フォルダーへ、校正の適用と保存先の両方がそろった時点で書く。
// 作業用ファイルは一時フォルダへ置き、元の雛形を保持する。再接続・雛形クローズ後は適用を解除して、再び入力する。
internal sealed partial class MainForm : Form
{
    private readonly FlowLayoutPanel _calibrationPanel = new();
    private readonly Label _calibrationStatusLabel = new();
    private readonly Button _calibrationButton = new();
    private readonly string _workingTemplateFolder = Path.Combine(Path.GetTempPath(),
        "AS110AllCheck", Guid.NewGuid().ToString("N"));
    private bool _calibrationApplied;
    private As110Calibration? _appliedCalibration;
    private CalibrationRecord? _appliedCalibrationRecord;
    // 今の校正記録を書き終えた保存先。同じ保存先には 2 回書かない。
    private string? _calibrationRecordedRoot;

    // NR-500(xdt)だけ校正画面を出す。NR-X(ydt)は保留(雛形の値のまま)。
    private bool CalibrationSupported =>
        _hardwareDetected && string.Equals(_detectedHardwareName, "NR-500", StringComparison.OrdinalIgnoreCase);

    private void BuildCalibrationPanel()
    {
        _calibrationPanel.AutoSize = true;
        _calibrationPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _calibrationPanel.Dock = DockStyle.Fill;
        _calibrationPanel.FlowDirection = FlowDirection.TopDown;
        _calibrationPanel.WrapContents = false;
        _calibrationPanel.Margin = new Padding(0, 0, 0, 10);
        _calibrationStatusLabel.AutoSize = true;
        _calibrationStatusLabel.MaximumSize = new Size(720, 0);
        _calibrationStatusLabel.Font = new Font(Font, FontStyle.Bold);
        _calibrationStatusLabel.Margin = new Padding(0, 4, 0, 8);
        _calibrationPanel.Controls.Add(_calibrationStatusLabel);
        ConfigureButton(_calibrationButton, "校正値を入力", (_, _) => ApplyCalibration(fromButton: true));
        _calibrationPanel.Controls.Add(_calibrationButton);
    }

    // 接続確認とモード選択の両方が済み、まだ適用していなければ校正画面を開く(モード選択後・接続確認後の両方から呼ぶ)。
    private void OpenCalibrationIfPending()
    {
        if (_closeRequested || !_modeSelected || _calibrationApplied || !ConnectionsReady || _checkingConnections || _operationInProgress) return;
        ApplyCalibration(fromButton: false);
    }

    private void ApplyCalibration(bool fromButton)
    {
        if (!_modeSelected || !ConnectionsReady || _operationInProgress || _checkingConnections) return;
        // 適用後の入れ直しはボタンからだけ。測定中・結果が残っている間・漏れ試験出力中は変えない。
        if (_calibrationApplied && (!fromButton || !CanChangeSetup || !CalibrationSupported || _leakOutputReady)) return;
        RunOperation("校正値の適用", () =>
        {
            // 未読み込み時はLOGGERの状態取得を要求しない。
            if (_logger.HasActiveFile())
            {
                RefreshLoggerState();
                if (!LoggerReadyToStart)
                    throw new InvalidOperationException("LOGGERが停止してから校正値を適用してください。");
            }
            string originalPath = GetDetectedTemplatePath();
            bool supportsCalibration = string.Equals(Path.GetExtension(originalPath), ".xdt", StringComparison.OrdinalIgnoreCase);
            string hardware = _detectedHardwareName;
            string templateName = Path.GetFileName(originalPath);
            string workingFolder = Path.Combine(_workingTemplateFolder, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workingFolder);
            string workingPath = Path.Combine(workingFolder, templateName);
            As110Calibration? calibration = null;
            if (supportsCalibration)
            {
                var editor = new XdtScalingEditor(originalPath);
                // 初期値は雛形の値。入れ直しのときだけ、いま適用している値から直せるようにする。
                using var dialog = new CalibrationDialog(hardware, templateName,
                    _calibrationApplied && _appliedCalibration is not null ? _appliedCalibration : As110Calibration.FromTemplate(editor));
                UseWaitCursor = false;
                try
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK)
                        return _calibrationApplied ? "校正値の変更をキャンセルしました。" : "校正値の入力をキャンセルしました。「校正値を入力」から入力してください。";
                    calibration = dialog.Calibration;
                }
                finally { UseWaitCursor = true; }
                if (_closeRequested || !ConnectionsReady || _detectedHardwareName != hardware)
                    throw new InvalidOperationException("接続状態が変わりました。接続確認後に校正値を適用してください。");
                // ダイアログ中にLOGGERが手動操作された場合も、収集中のファイルを閉じない。
                if (_logger.HasActiveFile())
                {
                    RefreshLoggerState();
                    if (!LoggerReadyToStart)
                        throw new InvalidOperationException("LOGGERが停止してから校正値を適用してください。");
                }
                calibration.Apply(editor);
                editor.Save(workingPath);
            }
            else
            {
                // NR-X(ydt)は校正未対応。雛形の値のまま作業用ファイルにする。
                File.Copy(originalPath, workingPath);
            }
            // 手動で開かれた未適用のファイルも、設定済みの雛形に置き換える。
            if (_logger.HasActiveFile())
            {
                long closeResult = _logger.CloseActiveFile();
                if (closeResult != 0)
                    throw new InvalidOperationException($"LOGGERのファイルを閉じられません（戻り値 {closeResult}）。");
            }
            ResetTemplate();
            try
            {
                string result = LoadTemplateFile(workingPath);
                RefreshLoggerState();
                _calibrationApplied = true;
                _appliedCalibration = calibration;
                _appliedCalibrationRecord = calibration is null ? null :
                    new CalibrationRecord(DateTimeOffset.Now, calibration, _modeBox.Text, hardware, templateName);
                _calibrationRecordedRoot = null;
                if (calibration is not null) AppendLog("校正値適用：" + calibration.Summary);
                else AppendLog("NR-Xの校正は未対応のため、雛形の値のまま読み込みました。");
                AppendLog("雛形読み込み：" + result);
                // 保存先が決まっていれば(データ取りで前回の保存先が残っているとき)ここで記録する。書けなければ設定完了にしない。
                try { WriteCalibrationRecordIfReady(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    throw new IOException("校正記録を保存できないため、試験開始を許可しません。保存先を確認し、校正値を入れ直してください。\r\n" +
                        CalibrationRecordFile.FolderPath(_rootBox.Text) + "\r\n" + ex.Message, ex);
                }
                RefreshReferenceCandidates();
                return "成功 / 型式（参照波形）を選択してください。";
            }
            catch
            {
                ResetTemplate();
                throw;
            }
        });
    }

    // 校正の適用と保存先の両方がそろっていて、その保存先にまだ書いていなければ校正記録を書く。
    // データ取り以外(保存先が無い)・NR-X(校正しない)では何もしない。失敗は呼び出し側で扱う。
    private void WriteCalibrationRecordIfReady()
    {
        if (_appliedCalibrationRecord is null || _flow.Mode != TestMode.Production) return;
        string root = _rootBox.Text;
        if (root.Length == 0 || string.Equals(_calibrationRecordedRoot, root, StringComparison.OrdinalIgnoreCase)) return;
        string path = CalibrationRecordFile.Write(root, _appliedCalibrationRecord);
        _calibrationRecordedRoot = root;
        AppendLog("校正記録：" + path);
    }

    private void UpdateCalibrationControls(bool connectionsVisible)
    {
        _calibrationPanel.Visible = connectionsVisible && _modeSelected;
        bool supported = CalibrationSupported;
        if (!_calibrationApplied)
            _calibrationStatusLabel.Text = supported ? "校正値を入力してください" : "雛形を読み込みます（NR-Xの校正は未対応）";
        else if (_appliedCalibration is not null)
            _calibrationStatusLabel.Text = $"校正値の適用日時：{_appliedCalibrationRecord?.AppliedAt.LocalDateTime:yyyy/MM/dd HH:mm:ss}\r\n" +
                "適用済み校正値：" + _appliedCalibration.Summary;
        else
            _calibrationStatusLabel.Text = "NR-X（YDT）の校正は未対応です。雛形の値のまま使います。";
        _calibrationButton.Text = !_calibrationApplied ? (supported ? "校正値を入力" : "雛形を読み込む") : "校正値を変更";
        _calibrationButton.Visible = !_calibrationApplied || supported;
        _calibrationButton.Enabled = _calibrationApplied
            ? CanChangeSetup && !_checkingConnections && ConnectionsReady && _templateLoaded && LoggerReadyToStart && !_leakOutputReady
            : _modeSelected && !_operationInProgress && !_checkingConnections && ConnectionsReady;
    }

    private void CleanupWorkingTemplate()
    {
        try
        {
            if (Directory.Exists(_workingTemplateFolder)) Directory.Delete(_workingTemplateFolder, recursive: true);
        }
        catch { /* 終了時は一時ファイル削除のエラーを表示しない。 */ }
    }
}
