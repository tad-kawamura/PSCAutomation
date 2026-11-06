namespace PSCAutomation.Apps.AS110AllCheck;

// 接続確認・モード選択後のスタンド選択を担当する。
// 共通の雛形から、選択した設定を適用した作業用ファイルを作る。
// LOGGERで作業用ファイルを開けたときだけ、スケーリング適用済みとする。
// 作業用ファイルは一時フォルダへ置き、元の雛形を保持する。
// 再接続・雛形クローズ後は選択を解除して、再び設定する。
internal sealed partial class MainForm : Form
{
    private readonly FlowLayoutPanel _standSelectionPanel = new();
    private readonly FlowLayoutPanel _standButtonsPanel = new();
    private readonly Label _standStatusLabel = new();
    private readonly List<Button> _standButtons = [];
    private readonly string _workingTemplateFolder = Path.Combine(Path.GetTempPath(),
        "AS110BoltCheck", Guid.NewGuid().ToString("N"));
    private As110StandSettings? _selectedStand;
    private bool _standScalingApplied;
    private readonly Button _calibrationButton = new();
    private readonly Label _calibrationStatusLabel = new();
    private readonly CalibrationHistory _calibrationHistory = new(CalibrationHistory.DefaultPath);
    private CalibrationRecord? _appliedCalibrationRecord;
    private As110Calibration? _appliedCalibration;
    private bool _calibrationSupported;

    private void BuildStandSelection()
    {
        _standSelectionPanel.AutoSize = true;
        _standSelectionPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _standSelectionPanel.Dock = DockStyle.Fill;
        _standSelectionPanel.FlowDirection = FlowDirection.TopDown;
        _standSelectionPanel.WrapContents = false;
        _standSelectionPanel.Margin = new Padding(0, 0, 0, 10);
        _standStatusLabel.AutoSize = true;
        _standStatusLabel.Font = new Font(Font, FontStyle.Bold);
        _standStatusLabel.Margin = new Padding(0, 4, 0, 8);
        _standSelectionPanel.Controls.Add(_standStatusLabel);
        _standButtonsPanel.AutoSize = true;
        _standButtonsPanel.Dock = DockStyle.Fill;
        // 3 つのスタンドを横に並べ、操作欄の高さを抑える(縦並びだと下の型式・保存先の行がログ欄の下に隠れる)。
        _standButtonsPanel.FlowDirection = FlowDirection.LeftToRight;
        _standButtonsPanel.WrapContents = true;
        foreach (As110StandSettings stand in As110StandSettings.All)
        {
            var button = new Button();
            ConfigureButton(button, $"{stand.Name}（{stand.InputVoltage:g} V＝{stand.CurrentMilliAmps:g} mA）",
                (_, _) => SelectStand(stand), new Size(240, 36));
            button.Margin = new Padding(0, 0, 8, 6);
            _standButtons.Add(button);
            _standButtonsPanel.Controls.Add(button);
        }
        _standSelectionPanel.Controls.Add(_standButtonsPanel);
        _calibrationStatusLabel.AutoSize = true;
        _calibrationStatusLabel.MaximumSize = new Size(720, 0);
        _calibrationStatusLabel.Margin = new Padding(0, 4, 0, 8);
        _standSelectionPanel.Controls.Add(_calibrationStatusLabel);
        ConfigureButton(_calibrationButton, "電流・圧力の校正値を変更", (_, _) =>
        {
            if (_selectedStand is not null) SelectStand(_selectedStand, editCalibration: true);
        });
        _standSelectionPanel.Controls.Add(_calibrationButton);
    }

    private void SelectStand(As110StandSettings stand, bool editCalibration = false)
    {
        if (!_modeSelected || !ConnectionsReady || _operationInProgress || _checkingConnections ||
            (_standScalingApplied && !editCalibration)) return;
        if (editCalibration && (!CanChangeSetup || !_calibrationSupported || !_standScalingApplied || _leakOutputReady)) return;
        RunOperation("スタンド設定", () =>
        {
            // 未読み込み時はLOGGERの状態取得を要求しない。
            if (_logger.HasActiveFile())
            {
                RefreshLoggerState();
                if (!LoggerReadyToStart)
                    throw new InvalidOperationException("LOGGERが停止してからスタンドを選択してください。");
            }
            string originalPath = GetDetectedTemplatePath();
            var editor = new XdtScalingEditor(originalPath);
            bool supportsCalibration = string.Equals(Path.GetExtension(originalPath), ".xdt", StringComparison.OrdinalIgnoreCase);
            As110Calibration? calibration = null;
            string hardware = _detectedHardwareName;
            if (supportsCalibration)
            {
                // 校正値の変更時は、いま適用している値から入力し直せるようにする。
                using var dialog = new CalibrationDialog(stand.Name, hardware,
                    editCalibration && _appliedCalibration is not null ? _appliedCalibration : As110Calibration.FromTemplate(stand, editor));
                UseWaitCursor = false;
                try
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return "校正値の適用をキャンセルしました。";
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
            }
            else stand.Apply(editor);
            string workingFolder = Path.Combine(_workingTemplateFolder, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workingFolder);
            string workingPath = Path.Combine(workingFolder, Path.GetFileName(originalPath));
            editor.Save(workingPath);
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
                CalibrationRecord? record = null;
                if (calibration is not null)
                {
                    record = new(DateTimeOffset.Now, calibration);
                    // 記録できなければ設定完了にせず、エラーを通知して測定開始を禁止する。
                    try { _calibrationHistory.Append(record); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        throw new IOException("校正記録を保存できないため、試験開始を許可しません。保存先を確認し、スタンド選択からやり直してください。\r\n" +
                            _calibrationHistory.FilePath + "\r\n" + ex.Message, ex);
                    }
                }
                _selectedStand = stand;
                _standScalingApplied = true;
                _calibrationSupported = supportsCalibration;
                _appliedCalibration = calibration;
                _appliedCalibrationRecord = record;
                if (calibration is not null)
                {
                    AppendLog("校正値適用：" + calibration.Summary);
                    AppendLog($"校正記録：{_calibrationHistory.FilePath} / 適用日時={record!.AppliedAt:O}");
                }
                AppendLog("スケーリング適用：" + (calibration is null ? stand.Summary : stand.Name));
                AppendLog("雛形読み込み：" + result);
                RefreshReferenceCandidates();
                return "成功 / 参照波形を選択してください。";
            }
            catch
            {
                ResetTemplate();
                throw;
            }
        });
    }

    private void UpdateStandControls(bool connectionsVisible)
    {
        _standSelectionPanel.Visible = connectionsVisible && _modeSelected;
        _standStatusLabel.Text = _standScalingApplied && _selectedStand is not null
            ? "使用スタンド：" + _selectedStand.Name
            : "使用するスタンドを選択してください";
        _calibrationStatusLabel.Visible = _standScalingApplied;
        _calibrationStatusLabel.Text = _appliedCalibration is not null
            ? $"校正値の適用日時：{_appliedCalibrationRecord?.AppliedAt.LocalDateTime:yyyy/MM/dd HH:mm:ss}\r\n" +
                "適用済み校正値：" + _appliedCalibration.Summary
            : "スタンド公称値を適用。NR-X（YDT）の電流・圧力のGUI校正は未対応です。";
        _calibrationButton.Visible = _standScalingApplied && _calibrationSupported;
        _calibrationButton.Enabled = CanChangeSetup && !_checkingConnections && ConnectionsReady &&
            _templateLoaded && LoggerReadyToStart && !_leakOutputReady;
        _standButtonsPanel.Visible = !_standScalingApplied;
        foreach (Button button in _standButtons)
            button.Enabled = _modeSelected && !_operationInProgress && !_checkingConnections && ConnectionsReady;
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
