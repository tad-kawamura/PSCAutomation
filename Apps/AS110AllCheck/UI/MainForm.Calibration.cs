namespace PSCAutomation.Apps.AS110AllCheck;

// 校正値の入力と適用を担当する(2026-11-06 にスタンド選択を廃止。人の判断)。
// 2026-11-18 から 1 日 1 回の流れ(SPEC「段階 1 の直しと 1 日 1 回の流れ」): 校正はモードを選ぶ前に、その日の最初に 1 回。
//   接続確認のあとは最初の画面(「校正」「測定」。2026-11-19、MainForm.Home.cs)。校正画面は「校正」を押して開く(自動では開かない)。
//   今日の校正値が無いあいだ「測定」は押せない(NR-500 のとき)。「適用」で今日の校正値を exe の隣の「校正値」フォルダーに残す。
//   測定の画面に入ったあとで日付が変わったときは、今までどおり校正画面を開く。
//   その日はアプリを立ち上げ直しても校正画面を出さない。モードを選んだら(データ取りは保存先も)、今日の校正値でそのモードの作業用ファイルを作って LOGGER に読み込む。
//   「校正し直す」でいつでも(測定結果が残っていないとき)校正し直せる。前の日の値は呼び出さない。
// 校正画面の初期値は今日の校正値、無ければデータ調整の雛形の値。
// NR-X(ydt)は校正未対応(保留)なので、校正画面を出さず雛形の値のまま読み込む。
// 校正記録はデータ取りの保存先の「校正記録」フォルダーへ、校正の適用とこの起動での保存先の選択の両方がそろった時点で書く(前回の保存先には書かない)。
// 作業用ファイルは一時フォルダへ置き、元の雛形を保持する。再接続・雛形クローズ後は適用を解除し、今日の校正値で読み込み直す。
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
    // LOGGER に読み込んだ作業用の雛形。漏れ試験の自動判定は、これが xdt のとき(NR-500)だけ圧力の換算係数を読んで行う。
    private string? _workingTemplatePath;
    // 作業用の雛形の元(雛形フォルダーのファイル)。型式で LOGGER 雛形が替わる(AS110-006)ときに、読み直すかを決める。
    private string? _workingTemplateSource;
    // 今の校正記録を書き終えた保存先。同じ保存先には 2 回書かない。
    private string? _calibrationRecordedRoot;
    // 今日の校正値(2026-11-18)。ファイルに書けなかったときも、アプリを閉じるまではこの値で進める。
    private TodayCalibration? _todayCalibration;
    // 今日の校正値のファイルの置き場(画面の検査で差し替える)。
    private TodayCalibrationStore _todayCalibrationStore = TodayCalibrationStore.Default;
    // 今日の校正値のファイルを読もうとした日(同じ日に何度も読みに行かない・読めない知らせを繰り返さない)。
    private DateTime? _todayCalibrationLoadedFor;

    // NR-500(xdt)だけ校正画面を出す。NR-X(ydt)は保留(雛形の値のまま)。
    private bool CalibrationSupported =>
        _hardwareDetected && string.Equals(_detectedHardwareName, "NR-500", StringComparison.OrdinalIgnoreCase);

    // 今日の校正値(日付が変わっていたら無いものとする)。
    private TodayCalibration? ValidTodayCalibration => _todayCalibration is { } today && today.IsFor(DateTime.Now) ? today : null;

    // 校正を済ませないとモードを選べない(NR-500 で接続確認が済んだとき)。
    private bool CalibrationBlocksModeSelection => CalibrationSupported && ConnectionsReady && ValidTodayCalibration is null;

    // データ調整の雛形(校正画面の初期値と、ふつうの値から外れていないかの比べ先)。
    private static string AdjustmentTemplatePath => Path.Combine(TemplateFolderPath, "AS110データ調整・データ取り用.xdt");

    private void BuildCalibrationPanel()
    {
        _calibrationPanel.AutoSize = true;
        _calibrationPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _calibrationPanel.Dock = DockStyle.Fill;
        _calibrationPanel.FlowDirection = FlowDirection.TopDown;
        _calibrationPanel.WrapContents = false;
        _calibrationPanel.Margin = new Padding(0, 0, 0, 10);
        _calibrationPanel.Controls.Add(_calibrationStatusLabel);
        ConfigureButton(_calibrationButton, "校正値を入力", (_, _) => ApplyCalibration(fromButton: true));
        // 「設定変更（測定を破棄）」は「校正値を変更」の隣に置く(2026-11-06、人の指示)。文言・動作の設定は BuildProductionPanel / UpdateProductionControls。
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty
        };
        buttons.Controls.AddRange([_calibrationButton, _remeasureButton]);
        _calibrationPanel.Controls.Add(buttons);
    }

    // 接続確認・モード選択・保存先選択のあとに呼ぶ(2026-11-18、1 日 1 回の流れ)。
    // NR-500 で今日の校正値が無ければ、モードを選ぶ前でも校正画面を開く。今日の校正値があり、モード(データ取りは保存先も)が決まっていて
    // まだ雛形を読み込んでいなければ、校正画面を出さずに今日の校正値で読み込む。NR-X は今までどおり雛形の値のまま読み込む。
    private void OpenCalibrationIfPending()
    {
        if (_closeRequested || !ConnectionsReady || _checkingConnections || _operationInProgress) return;
        // 最初の画面(「校正」「測定」)では校正画面を自動で開かない。人が「校正」を押す(2026-11-19、人の指示)。
        if (!MeasurementScreen) return;
        if (CalibrationSupported)
        {
            LoadTodayCalibrationIfNeeded();
            if (ValidTodayCalibration is null)
            {
                CalibrateToday();
                return;
            }
        }
        if (!_modeSelected || !SaveRootReady || _calibrationApplied) return;
        ApplyCalibration(fromButton: false);
    }

    // その日に 1 回だけ、今日の校正値のファイルを読む。読めなければ知らせて校正画面を開く(古い値・壊れた値では進めない)。
    private void LoadTodayCalibrationIfNeeded()
    {
        DateTime today = DateTime.Now.Date;
        if (ValidTodayCalibration is not null || _todayCalibrationLoadedFor == today) return;
        _todayCalibrationLoadedFor = today;
        try
        {
            if (_todayCalibrationStore.Read(today) is { } loaded)
            {
                _todayCalibration = loaded;
                AppendLog($"今日の校正値を読みました（{loaded.CalibratedAtText} に校正）：{loaded.Calibration.Summary}");
            }
        }
        catch (InvalidDataException ex)
        {
            AppendLog("今日の校正値が読めません：" + ex.Message);
            ErrorNotification.Show(this, "今日の校正値が読めません。校正し直してください。\r\n" + _todayCalibrationStore.PathFor(today) + "\r\n" + ex.Message,
                "今日の校正値", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // 「校正値を入力 / 雛形を読み込む / 校正し直す」と、モード選択などのあとの自動の読み込み。
    private void ApplyCalibration(bool fromButton)
    {
        if (!ConnectionsReady || _operationInProgress || _checkingConnections) return;
        if (CalibrationSupported)
        {
            // 今日の校正値が無いとき、またはボタンで校正し直すとき(雛形を読み込む前の「雛形を読み込む」は除く)は校正画面を開く。
            if (ValidTodayCalibration is null || (fromButton && !ReadyToLoadTodayCalibration))
            {
                CalibrateToday();
                return;
            }
        }
        if (!_modeSelected || !SaveRootReady || _calibrationApplied) return;
        RunOperation("校正値の適用", () =>
        {
            // 未読み込み時はLOGGERの状態取得を要求しない。
            if (_logger.HasActiveFile())
            {
                RefreshLoggerState();
                if (!LoggerReadyToStart)
                    throw new InvalidOperationException("LOGGERが停止してから校正値を適用してください。");
            }
            if (CalibrationSupported)
            {
                LoadTodayCalibrationIntoModeTemplate();
                return "成功 / 型式（参照波形）を選択してください。";
            }
            // NR-X(ydt)は校正未対応。雛形の値のまま作業用ファイルにする(CreateWorkingTemplate)。
            string originalPath = GetDetectedTemplatePath();
            string workingPath = CreateWorkingTemplate(originalPath, null);
            LoadWorkingTemplate(originalPath, workingPath, null, _detectedHardwareName,
                result =>
                {
                    AppendLog("NR-Xの校正は未対応のため、雛形の値のまま読み込みました。");
                    AppendLog("雛形読み込み：" + result);
                },
                afterRecord: RefreshReferenceCandidates);
            return "成功 / 型式（参照波形）を選択してください。";
        });
    }

    // 今日の校正値があり、モード(データ取りは保存先も)が決まっていて、まだ読み込んでいない(ボタンは「雛形を読み込む」)。
    private bool ReadyToLoadTodayCalibration => ValidTodayCalibration is not null && _modeSelected && SaveRootReady && !_calibrationApplied;

    // 今日の校正値で、いまのモードの作業用ファイルを作って LOGGER に読み込む(校正画面は出さない。AI の案 6)。
    private void LoadTodayCalibrationIntoModeTemplate()
    {
        var today = ValidTodayCalibration ?? throw new InvalidOperationException("今日の校正がまだです。「校正値を入力」から校正してください。");
        string originalPath = GetDetectedTemplatePath();
        if (!IsXdt(originalPath)) throw new InvalidOperationException("NR-500 の雛形（xdt）が選ばれていません。接続確認をやり直してください。");
        string workingPath = CreateWorkingTemplate(originalPath, today.Calibration);
        LoadWorkingTemplate(originalPath, workingPath, today.Calibration, _detectedHardwareName,
            result =>
            {
                AppendLog($"校正値適用（今日の校正値。{today.CalibratedAtText} に校正）：" + today.Calibration.Summary);
                AppendLog("雛形読み込み：" + result);
            },
            afterRecord: RefreshReferenceCandidates, readingLines: today.RecordLines);
    }

    // 今日の校正(校正画面)。接続確認のあと(今日の校正値が無いとき)と「校正値を入力 / 校正し直す」から呼ぶ。モードを選ぶ前でもよい。
    // 「適用」で今日の校正値を残し、モードを選んであればそのモードの雛形に入れて読み込む。キャンセルなら前の状態のまま。
    // 読み取りのボタンを押したときは、校正用の雛形で LOGGER に測定させる(閉じたら止めて閉じ、前の作業用ファイルがあれば開き直す)。
    private void CalibrateToday()
    {
        if (!CalibrationSupported || !ConnectionsReady || _operationInProgress || _checkingConnections) return;
        // 読み込み済みのときは、測定中・結果が残っている間・漏れ試験出力中は校正し直さない。
        if (_calibrationApplied && (!CanChangeSetup || _leakOutputReady)) return;
        RunOperation("今日の校正", () =>
        {
            if (_logger.HasActiveFile())
            {
                RefreshLoggerState();
                if (!LoggerReadyToStart)
                    throw new InvalidOperationException("LOGGERが停止してから校正してください。");
            }
            string hardware = _detectedHardwareName;
            if (!File.Exists(AdjustmentTemplatePath))
                throw new FileNotFoundException("校正画面の初期値に使うデータ調整の雛形が「雛形」フォルダーにありません。", AdjustmentTemplatePath);
            var templateCalibration = As110Calibration.FromTemplate(new XdtScalingEditor(AdjustmentTemplatePath));
            var previous = ValidTodayCalibration;
            bool wasApplied = _calibrationApplied;
            string? previousWorking = _workingTemplatePath;
            As110Calibration calibration;
            CalibrationReading reading;
            bool readerWasOpen;
            using (var dialog = new CalibrationDialog(hardware, "今日の校正", previous?.Calibration ?? templateCalibration,
                ReadCurrentWithMeter, ReadPressureWithGauge,
                previous is null ? "初期値は雛形の値です。読み取るか、実測した値を入力してください。"
                    : $"初期値は今日の校正値（{previous.CalibratedAtText} に校正）です。") { Template = templateCalibration })
            {
                UseWaitCursor = false;
                DialogResult result;
                try { result = dialog.ShowDialog(this); }
                finally
                {
                    UseWaitCursor = true;
                    readerWasOpen = CloseCalibrationReader();
                }
                if (result != DialogResult.OK)
                {
                    // 読み取りのために作業用ファイルを閉じていたら、前の作業用ファイルを開き直す(前の校正値のまま)。
                    if (readerWasOpen && wasApplied && previousWorking is not null) ReopenWorkingTemplate(previousWorking);
                    return previous is null
                        ? (MeasurementScreen ? "校正をキャンセルしました。「校正値を入力」から校正してください。" : "校正をキャンセルしました。「校正」から校正してください。")
                        : "校正し直しをキャンセルしました（前の校正値のまま）。";
                }
                calibration = dialog.Calibration;
                reading = dialog.Reading;
            }
            if (_closeRequested || !ConnectionsReady || _detectedHardwareName != hardware)
                throw new InvalidOperationException("接続状態が変わりました。接続確認後に校正し直してください。");
            var today = TodayCalibration.Create(DateTimeOffset.Now, calibration, hardware, reading);
            _todayCalibration = today;
            _todayCalibrationLoadedFor = DateTime.Now.Date;
            AppendLog("今日の校正値：" + calibration.Summary + "（" + reading.MethodText + "）");
            string path = _todayCalibrationStore.PathFor(today.CalibratedAt.LocalDateTime);
            try
            {
                path = _todayCalibrationStore.Write(today);
                AppendLog("今日の校正値を保存：" + path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 書けなくても、アプリを閉じるまではこの値で測定へ進める(2026-11-18、人が承認。AI の案 5 から変えた)。
                AppendLog("今日の校正値を保存できません：" + ex.Message);
                ErrorNotification.Show(this, "今日の校正値をファイルに書けません。アプリを閉じるまではこの値で測定できますが、立ち上げ直したら校正し直してください。\r\n" +
                    path + "\r\n" + ex.Message, "今日の校正値", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            if (_modeSelected && SaveRootReady)
            {
                // ダイアログ中にLOGGERが手動操作された場合も、収集中のファイルを閉じない。
                if (_logger.HasActiveFile())
                {
                    RefreshLoggerState();
                    if (!LoggerReadyToStart)
                        throw new InvalidOperationException("LOGGERが停止してから校正値を適用してください。");
                }
                LoadTodayCalibrationIntoModeTemplate();
                return "成功 / 型式（参照波形）を選択してください。";
            }
            return !MeasurementScreen ? "成功 / 「測定」を押してください。"
                : !_modeSelected ? "成功 / モードを選択してください。" : "成功 / 「保存先選択」で保存先を選んでください。";
        });
    }

    // 読み取りのために閉じた作業用ファイルを開き直す(校正し直しをキャンセルしたとき)。開けなければ雛形待ちに戻す(今日の校正値で読み込み直せる)。
    private void ReopenWorkingTemplate(string workingPath)
    {
        try
        {
            string result = LoadTemplateFile(workingPath);
            RefreshLoggerState();
            AppendLog("作業用の雛形を開き直しました：" + result);
        }
        catch
        {
            ResetTemplate();
            throw;
        }
    }

    // 参照波形で選んだ型式・いまのモードの LOGGER 雛形が、いま読み込んでいる雛形と違うとき(データ取りで AS110-006 とほかの型式の間を替えたとき、
    // AS110-006 でデータ調整とデータ取りを替えたとき)に、
    // いま適用している校正値のまま、その型式の雛形で作業用ファイルを作り直して LOGGER に読み込む(校正画面は出さない。2026-11-12、SPEC「AS110-006 の雛形と測り方」)。
    // 校正記録は新しい雛形の名前で書き直す(同じ保存先にも書く)。失敗したら雛形待ちに戻る(校正からやり直す)。
    // reason はログに出す読み直しの理由(型式で替わる / モードで替わる)。
    private void ReloadTemplateForModel(string originalPath, string reason = "型式で雛形が替わるため")
    {
        if (!_calibrationApplied)
            throw new InvalidOperationException("校正値を適用してから参照波形を選択してください。");
        if (_logger.HasActiveFile())
        {
            RefreshLoggerState();
            if (!LoggerReadyToStart)
                throw new InvalidOperationException("LOGGERが停止してから参照波形・モードを選択してください。");
        }
        As110Calibration? calibration = _appliedCalibration;
        string workingPath = CreateWorkingTemplate(originalPath, calibration);
        LoadWorkingTemplate(originalPath, workingPath, calibration, _detectedHardwareName,
            result => AppendLog($"雛形読み直し（{reason}、同じ校正値で読み込み）：" + result),
            readingLines: _appliedCalibrationRecord?.ReadingLines);
    }

    private static bool IsXdt(string path) =>
        string.Equals(Path.GetExtension(path), ".xdt", StringComparison.OrdinalIgnoreCase);

    // 元の雛形から作業用ファイルを一時フォルダーに作る(元の雛形は変えない)。xdt で校正値があれば換算係数を書き込み、無ければそのまま写す(NR-X の ydt)。
    // editor は校正画面の初期値を読むのに使ったもの(あれば使い回す)。ApplyCalibration と ReloadTemplateForModel の共通部分(2026-11-13)。
    private string CreateWorkingTemplate(string originalPath, As110Calibration? calibration, XdtScalingEditor? editor = null)
    {
        string workingFolder = Path.Combine(_workingTemplateFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingFolder);
        string workingPath = Path.Combine(workingFolder, Path.GetFileName(originalPath));
        if (calibration is not null && IsXdt(originalPath))
        {
            editor ??= new XdtScalingEditor(originalPath);
            calibration.Apply(editor);
            editor.Save(workingPath);
        }
        else
        {
            File.Copy(originalPath, workingPath);
        }
        return workingPath;
    }

    // 作業用ファイルを LOGGER に読み込み、適用中の状態(校正値・雛形・校正記録)をそろえる。ApplyCalibration と ReloadTemplateForModel の共通部分(2026-11-13)。
    // 開いているファイルを閉じてから読み込む。読み込み後のログ(loaded)・校正記録の書き込み・afterRecord のどこで失敗しても雛形待ちに戻す(校正からやり直す)。
    // readingLines は校正記録に書く、今日の校正値の校正した時刻・読み取り方など(2026-11-18。2026-11-13 は読み取り方だけ)。
    private void LoadWorkingTemplate(string originalPath, string workingPath, As110Calibration? calibration, string hardware,
        Action<string> loaded, Action? afterRecord = null, IReadOnlyList<string>? readingLines = null)
    {
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
            _workingTemplatePath = workingPath;
            _workingTemplateSource = originalPath;
            _appliedCalibration = calibration;
            _appliedCalibrationRecord = calibration is null ? null :
                new CalibrationRecord(DateTimeOffset.Now, calibration, _modeBox.Text, hardware, Path.GetFileName(originalPath), ReadingLines: readingLines);
            _calibrationRecordedRoot = null;
            loaded(result);
            try { WriteCalibrationRecordIfReady(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw new IOException("校正記録を保存できないため、試験開始を許可しません。保存先を確認し、校正値を入れ直してください。\r\n" +
                    CalibrationRecordFile.FolderPath(_rootBox.Text) + "\r\n" + ex.Message, ex);
            }
            afterRecord?.Invoke();
        }
        catch
        {
            ResetTemplate();
            throw;
        }
    }

    // 校正の適用と保存先の選択の両方がそろっていて、その保存先にまだ書いていなければ校正記録を書く。
    // データ取り以外(保存先が無い)・NR-X(校正しない)・この起動でまだ保存先を選んでいないときは何もしない。失敗は呼び出し側で扱う。
    private void WriteCalibrationRecordIfReady()
    {
        if (_appliedCalibrationRecord is null || _flow.Mode != TestMode.Production || !_saveRootChosen) return;
        string root = _rootBox.Text;
        if (root.Length == 0 || string.Equals(_calibrationRecordedRoot, root, StringComparison.OrdinalIgnoreCase)) return;
        string path = CalibrationRecordFile.Write(root, _appliedCalibrationRecord);
        _calibrationRecordedRoot = root;
        AppendLog("校正記録：" + path);
    }

    // 校正欄(2026-11-18): NR-500 は接続確認が済めばモードの前から出し、今日の校正値を見せる。
    // ボタンは 今日の校正値が無い →「校正値を入力」、あるが雛形をまだ読み込んでいない →「雛形を読み込む」、それ以外 →「校正し直す」。
    private void UpdateCalibrationControls(bool connectionsVisible)
    {
        bool supported = CalibrationSupported;
        var today = ValidTodayCalibration;
        // 最初の画面(「校正」「測定」)では出さない(2026-11-19)。
        _calibrationPanel.Visible = connectionsVisible && MeasurementScreen && (_modeSelected || supported);
        if (supported)
        {
            if (today is null)
                _calibrationStatusLabel.Text = "今日の校正がまだです。「校正値を入力」から校正してください（済むとモードを選べます）";
            else
                _calibrationStatusLabel.Text = $"今日の校正値（{today.CalibratedAtText} に校正）：{today.Calibration.Summary}\r\n" +
                    (!_modeSelected ? "モードを選んでください" :
                     !SaveRootReady ? "「保存先選択」で保存先を選んでください（選ぶと今日の校正値で雛形を読み込みます）" :
                     !_calibrationApplied ? "今日の校正値で雛形を読み込みます（「雛形を読み込む」）" :
                     $"雛形に入れて読み込み済み（{_appliedCalibrationRecord?.AppliedAt.LocalDateTime:HH:mm:ss}）");
        }
        else if (!_calibrationApplied && !SaveRootReady)
            _calibrationStatusLabel.Text = "「保存先選択」で保存先を選んでください（選ぶと雛形を読み込みます）";
        else if (!_calibrationApplied)
            _calibrationStatusLabel.Text = "雛形を読み込みます（NR-Xの校正は未対応）";
        else
            _calibrationStatusLabel.Text = "NR-X（YDT）の校正は未対応です。雛形の値のまま使います。";
        _calibrationButton.Text = supported
            ? (today is null ? "校正値を入力" : ReadyToLoadTodayCalibration ? "雛形を読み込む" : "校正し直す")
            : "雛形を読み込む";
        _calibrationButton.Visible = supported || !_calibrationApplied;
        bool idle = !_operationInProgress && !_checkingConnections && ConnectionsReady;
        _calibrationButton.Enabled = supported
            ? (_calibrationApplied
                ? CanChangeSetup && idle && _templateLoaded && LoggerReadyToStart && !_leakOutputReady
                : idle)
            : _modeSelected && SaveRootReady && idle;
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
