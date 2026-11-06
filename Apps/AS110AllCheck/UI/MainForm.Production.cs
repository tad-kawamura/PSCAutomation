using System.Diagnostics;
namespace PSCAutomation.Apps.AS110AllCheck;
internal sealed partial class MainForm : Form
{
    private readonly ProductionWorkflow _flow = new();
    private readonly TransferSettings _transferSettings = TransferSettings.Load();
    private readonly ComboBox _modeBox = new() { DropDownStyle=ComboBoxStyle.DropDownList,Width=125 };
    private const string ModeUnsetText="未設定";
    private static readonly Color ModeUnsetColor=Color.Red;
    private bool _modeSelected;

    private Control BuildModeSelectionPanel()
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize=true, Dock=DockStyle.Fill, BackColor=Color.AliceBlue,
            Padding=new Padding(12, 6, 12, 6), Margin=new Padding(0, 4, 0, 8)
        };
        var modeFont = new Font(Font.FontFamily, 16, FontStyle.Bold);
        _modeBox.Font=modeFont;
        _modeBox.Width=230;
        _modeBox.DropDownHeight=180;
        _modeBox.Items.AddRange(["動き検査","データ調整","データ取り"]);
        // 起動直後はまだ選んでいないので、空欄の代わりに赤文字で「未設定」を出す(DropDownList は文字を置けないため自前で描く)。
        _modeBox.DrawMode=DrawMode.OwnerDrawFixed;
        _modeBox.DrawItem+=(_,e)=>
        {
            if(e.Index<0)
            {
                // 未選択の表示欄。フォーカスの青い帯に赤文字が載ると読めないので、白地に赤文字で描く。
                e.Graphics.FillRectangle(SystemBrushes.Window,e.Bounds);
                TextRenderer.DrawText(e.Graphics,ModeUnsetText,e.Font,e.Bounds,ModeUnsetColor,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);
                return;
            }
            e.DrawBackground();
            TextRenderer.DrawText(e.Graphics,_modeBox.Items[e.Index]?.ToString()??"",e.Font,e.Bounds,e.ForeColor,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);
            e.DrawFocusRectangle();
        };
        _modeBox.SelectionChangeCommitted+=(_,_)=>ChangeTestMode();
        panel.Controls.AddRange([new Label
        {
            Text="モードを選択", AutoSize=true, Font=modeFont,
            ForeColor=Color.MidnightBlue, Margin=new Padding(0, 5, 18, 0)
        }, _modeBox]);
        return panel;
    }
    // 型式は参照波形(.xyb)のファイル名から決める(2026-11-06 に「型式」の行と「型式を適用」を廃止。人の判断)。MainForm.Files.cs の FindModelForReference。
    private ProductModel _selectedModel = ProductModel.All[0];
    // 測定開始S/N(今の S/N)。人が入れる唯一の S/N。空のまま始める(起動時・範囲を終えた後)。
    private readonly TextBox _serialBox = new() { Width=110 };
    private readonly Label _serialLabel = new() { Text="測定開始S/N", AutoSize=true };
    // 測定開始S/N が空のときの赤字。欄の右に出す。
    private readonly Label _serialMissingLabel = new() { Text="入力してください", AutoSize=true, ForeColor=Color.Red, Visible=false };
    private readonly TextBox _rootBox = new() { Width=280,ReadOnly=true };
    private readonly Label _flowLabel = new() { AutoSize=true,MaximumSize=new Size(680,0) };
    private readonly Button _transferButton=new(),_saveButton=new(),_leakOutputButton=new(),_leakButton=new(),_completeButton=new(),_remeasureButton=new(),_retryCaptureButton=new();
    private bool _leakOutputReady;
    // 出力OFFを送信できず、発振器の出力が残っている可能性がある状態。
    // 解除は出力OFFの送信成功、または再接続（接続時に出力OFFを送信）だけで行う。
    private bool _generatorOutputUnknown, _generatorOutputAlertShown;
    private readonly string _boardToken=Guid.NewGuid().ToString("N");
    private long _displayEpoch;
    private string _displaySignature="";
    private ExcelWorkSession? _excel;
    private Form? _saveNotice;
    private string? _capturePath;
    private bool _reviewStage,_leakRunning,_closeAfterLeak,_lastExcelPass;
    private string _excelJudgmentStatus="未確認",_excelJudgmentError="";
    private string _saveAvailabilitySignature="";
    private string? _ownedSaveBase;
    // 正式保存で同名ファイルがあって保存しなかったときの案内(「次にすること」に赤字で出す)。転送結果が無効になるか保存できたら消す。
    private string? _saveConflict;
    private CancellationTokenSource? _leakCancel;
    private readonly string _sessionFolder=Path.Combine(Path.GetTempPath(),"AS110BoltCheck","ver3-"+Guid.NewGuid().ToString("N"));

    // モードを選ぶまで全体を非表示にする。型式・Excel転送はデータ調整とデータ取りで、保存先・S/N・保存・漏れ試験・合格はデータ取りを選んだときだけ表示する。
    // 並びは上から 型式 → 保存先 → 開始S/N・最後S/N(フォルダー名から) → 測定開始S/N → 保護解除・転送設定 → ボタン → 状態。表示/非表示を切り替えても並びが変わらないよう、行も列も TableLayoutPanel で位置を固定する。
    private readonly TableLayoutPanel _productionPanel=new() { AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Dock=DockStyle.Fill,ColumnCount=1,Visible=false };
    private TableLayoutPanel _rangePanel=null!,_serialPanel=null!,_savePathPanel=null!,_protectionPanel=null!,_actionPanel=null!;
    private readonly Button _chooseSaveRootButton=new() { Text="保存先選択",AutoSize=true };
    private readonly Button _releaseSerialButton=new() { Text="完了番号の保護と解除",AutoSize=true };
    private readonly Button _transferSettingsButton=new() { Text="転送設定",AutoSize=true };
    // 開始S/N・最後S/N は保存先フォルダー名「SN開始-最後」から入れる表示専用の欄(人は入力しない)。入ったら緑字。
    // 読み取り専用の TextBox は BackColor を自分で決めないと ForeColor が効かず灰色のままになる(WinForms の仕様)ので、BackColor を明示する。
    private readonly TextBox _rangeStartBox = new() { Width=100,ReadOnly=true,TabStop=false,BackColor=SystemColors.Window };
    private readonly Label _rangeStartLabel = new() { Text="開始S/N", AutoSize=true };
    private readonly TextBox _lastSerialBox = new() { Width=100,ReadOnly=true,TabStop=false,BackColor=SystemColors.Window };
    private readonly Label _lastSerialLabel = new() { Text="最後S/N", AutoSize=true };
    // 保存先フォルダー名「SN開始-最後」と S/N が合わないときの赤字。保存先の行の下に出す。
    private readonly Label _saveRootWarningLabel = new() { AutoSize=true,ForeColor=Color.Red,MaximumSize=new Size(680,0),Visible=false };
    // 保存先フォルダー名から開始S/N・最後S/N を入れる(選び直したら毎回入れ替える。範囲が無ければ空)。測定開始S/N は触らない。
    private void ApplySaveRootRange()
    {
        var range=SaveFolder.ParseRange(_rootBox.Text);
        _rangeStartBox.Text=range?.Start??"";_lastSerialBox.Text=range?.End??"";
        _rangeStartBox.ForeColor=_lastSerialBox.ForeColor=range is null?SystemColors.WindowText:Color.Green;
    }
    // 保存先の Excel/PDF/LOGGER にある、完了記録に無い S/N のデータを知らせる(保護は「完了番号の保護と解除」の画面で付ける)。
    private void ReportUnrecordedData()
    {
        if(_rootBox.Text.Length==0) return;
        try
        {
            var store=Store;using var held=store.Lock();
            var (unrecorded,unreadable)=SaveFolder.FindUnrecorded(_rootBox.Text,store.Read());
            if(unrecorded.Count==0 && unreadable==0){AppendLog("保存先確認：完了記録に無いデータはありません。");return;}
            string serials=string.Join(", ",unrecorded.Select(f=>f.Serial));
            string message=(unrecorded.Count>0?$"完了記録に無いデータが {unrecorded.Count} 件あります（S/N {serials}）。\n保護するなら「完了番号の保護と解除」でチェックを付けて確定してください。":"")
                +(unreadable>0?$"{(unrecorded.Count>0?"\n":"")}このアプリの形式でないファイルが {unreadable} 件あります（S/N を読めないので対象外）。":"");
            AppendLog("保存先確認："+message.Replace("\n"," "));
            ErrorNotification.Show(this,message,"保存先の確認",MessageBoxButtons.OK,MessageBoxIcon.Information);
        }
        catch(Exception ex){AppendLog($"保存先確認：エラー / {ex.Message}");ErrorNotification.Show(this,ex.Message,"保存先の確認",MessageBoxButtons.OK,MessageBoxIcon.Error);}
    }
    private static TableLayoutPanel Row(params Control[] controls)
    {
        var row=new TableLayoutPanel { AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,RowCount=1,ColumnCount=controls.Length,Margin=new Padding(0,0,0,6) };
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for(int i=0;i<controls.Length;i++)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            controls[i].Anchor=AnchorStyles.Left;
            row.Controls.Add(controls[i],i,0);
        }
        return row;
    }
    private Control BuildProductionPanel()
    {

        _rootBox.Text=_transferSettings.SaveRoot;
        ApplySaveRootRange();
        _chooseSaveRootButton.Click+=(_,_)=> {
            if(_operationInProgress || _flow.Measuring || _flow.SaveFailed || _leakRunning || _flow.HasSaved) return;
            using var dialog=new FolderBrowserDialog();
            if(dialog.ShowDialog(this)!=DialogResult.OK) return;
            string previousRoot=_rootBox.Text;
            _rootBox.Text=dialog.SelectedPath;
            // 校正を適用済みなら、選んだ保存先へ校正記録を書く。書けない保存先は受け付けず、選ぶ前の保存先に戻す。
            try { WriteCalibrationRecordIfReady(); }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _rootBox.Text=previousRoot;
                AppendLog($"保存先選択：校正記録を書けません / {ex.Message}");
                ErrorNotification.Show(this,"この保存先には校正記録を書けません。別の保存先を選んでください。\n"+CalibrationRecordFile.FolderPath(dialog.SelectedPath)+"\n"+ex.Message,"保存先の選択",MessageBoxButtons.OK,MessageBoxIcon.Error);
                return;
            }
            _transferSettings.SaveRoot=dialog.SelectedPath;_transferSettings.Save();
            ApplySaveRootRange();
            if(_flow.Mode==TestMode.Production && CanChangeSetup) RunOperation("保存先選択",()=>{ConfigureProduct(requireSaveRoot:false);return _rootBox.Text;});
            UpdateControls();
            ReportUnrecordedData();
        };
        _releaseSerialButton.Click+=(_,_)=>ReleaseCompletedSerial();
        _transferSettingsButton.Click+=(_,_)=>EditTransferSettings();
        ConfigureButton(_transferButton,"Excel転送",(_,_)=>TransferLatest());
        ConfigureButton(_saveButton,"保存",(_,_)=>SaveLatest());
        ConfigureButton(_leakOutputButton,"+30mA",(_,_)=>StartLeakOutput());
        ConfigureButton(_leakButton,"漏れ試験実施",async (_,_)=>await LeakCheckAsync());
        ConfigureButton(_completeButton,"合格",(_,_)=>CompleteProduct());
        ConfigureButton(_remeasureButton,"再測定へ戻る",(_,_)=>{
            if(_operationInProgress || _leakRunning || _flow.SaveFailed)return;
            if(_leakOutputReady)
            {
                RunOperation("漏れ試験出力停止",()=>{StopLeakOutput();_reviewStage=false;return "出力を停止して再測定へ戻りました。";});
                return;
            }
            if(!_flow.HasSaved){_saveNotice?.Close();_saveNotice=null;_capturePath=null;_flow.Invalidate();}
            _reviewStage=false;UpdateControls();
        });
        ConfigureButton(_retryCaptureButton,"一時保存を再試行",(_,_)=>RetryCapture());
        // 保存先 → (フォルダー名から)開始S/N・最後S/N → 測定開始S/N の順。保存先を先に選ぶと範囲が決まるので、その下に範囲と測定開始S/N を置く。型式の行は無い(参照波形で決まる)。
        _savePathPanel=Row(_chooseSaveRootButton,_rootBox);
        _saveRootWarningLabel.Margin=new Padding(0,0,0,6);
        _rangePanel=Row(_rangeStartLabel,_rangeStartBox,_lastSerialLabel,_lastSerialBox);
        _serialPanel=Row(_serialLabel,_serialBox,_serialMissingLabel);
        _protectionPanel=Row(_releaseSerialButton,_transferSettingsButton);
        // 型式・測定開始S/N・保存先の入力欄は、上段のボタンより目立つよう大きい文字にする。開始S/N・最後S/N は人が入力しないので小さいまま。
        var inputFont=new Font(Font.FontFamily,12);
        foreach(var row in new[]{_serialPanel,_savePathPanel,_protectionPanel})
            foreach(Control control in row.Controls) control.Font=inputFont;
        _serialBox.Width=140;_rootBox.Width=420;
        _lastSerialLabel.Margin=new Padding(12,_lastSerialLabel.Margin.Top,_lastSerialLabel.Margin.Right,_lastSerialLabel.Margin.Bottom);
        _serialMissingLabel.Margin=new Padding(8,_serialMissingLabel.Margin.Top,_serialMissingLabel.Margin.Right,_serialMissingLabel.Margin.Bottom);
        foreach(var button in new[]{_chooseSaveRootButton,_releaseSerialButton,_transferSettingsButton}) button.MinimumSize=new Size(0,40);
        // 合格は下段の左に置き、上段を 4 つにして横幅を抑える。
        var mainActions=Row(_transferButton,_saveButton,_leakOutputButton,_leakButton);
        // 「設定変更（測定を破棄）」は校正欄の「校正値を変更」の隣(BuildCalibrationPanel)。
        var subActions=Row(_completeButton,_retryCaptureButton);
        _actionPanel=new TableLayoutPanel { AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,RowCount=2,Margin=Padding.Empty };
        _actionPanel.Controls.Add(mainActions,0,0);_actionPanel.Controls.Add(subActions,0,1);
        Control[] rows=[_savePathPanel,_saveRootWarningLabel,_rangePanel,_serialPanel,_protectionPanel,_actionPanel,_flowLabel];
        _productionPanel.RowCount=rows.Length;
        for(int i=0;i<rows.Length;i++){ _productionPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); _productionPanel.Controls.Add(rows[i],0,i); }

        // 入力中もフォルダー名との照合(赤字)と「入力してください」を更新する。
        _serialBox.TextChanged+=(_,_)=>UpdateControls();
        _serialBox.Leave+=(_,_)=>{
            if(!CanEnterSerial)return;
            RunOperation("測定開始S/N設定",()=>{
                // S/N が空のまま残っていた測定結果(データ調整のものなど)は、S/N を入れた時点で破棄される(S/N なしではデータ取りに使えない)。
                bool discarded=_flow.HasCapture && _flow.SerialMissing && _serialBox.Text.Trim().Length>0;
                ConfigureProduct(requireSaveRoot:false);
                return _flow.Serial.Length>0?$"S/N {_flow.Serial} から"+(discarded?"（S/N なしの前の測定結果は破棄しました）":""):"測定開始S/N を入力してください";});
            // 測定開始S/N が入るまで Excel の事前準備を待たせているので、入ったらここで始める。
            if(_flow.Serial.Length>0 && _excel is null && _templateLoaded)RequestExcelPreparation();
        };
        return _productionPanel;
    }
    private bool CanChangeSetup => !_operationInProgress && !_flow.Measuring && !_flow.SaveFailed && !_flow.CaptureFailed && !_leakRunning && !_flow.HasCapture;
    // 測定開始S/N を入れられるとき。測定結果が残っていても、S/N がまだ空なら入れられる(2026-11-06。データ調整の結果が残ったままデータ取りへ移ると、
    // 「S/N を入力してください」と案内しながら欄が灰色のままになり、何もできなくなっていた)。S/N が入った結果を変えるときは「設定変更（測定を破棄）」。
    private bool CanEnterSerial => _flow.Mode==TestMode.Production && !_operationInProgress && !_flow.Measuring && !_flow.SaveFailed && !_flow.CaptureFailed && !_leakRunning && (!_flow.HasCapture || _flow.SerialMissing);
    private ProductModel SelectedModel => _selectedModel;
    private CompletionStore Store => new(_rootBox.Text.Length>0?_rootBox.Text:throw new InvalidOperationException("保存先を選択してください。"));
    private void ConfigureProduct(bool requireSaveRoot=true)
    {
        if (_flow.Mode == TestMode.Motion) return;
        if (_flow.Mode == TestMode.Adjustment) { _flow.ConfigureAdjustment(SelectedModel); return; }
        _flow.SetLastSerial(_lastSerialBox.Text.Trim());
        // 測定開始S/N が空のときは型式だけ持ち、測定開始はできない(赤字「入力してください」で案内)。
        if(_serialBox.Text.Trim().Length==0) { _flow.ConfigureWithoutSerial(SelectedModel); return; }
        _flow.Configure(_serialBox.Text.Trim(),SelectedModel);
        if(_flow.Mode==TestMode.Production && (requireSaveRoot || _rootBox.Text.Length>0)) { var store=Store;using var held=store.Lock();if(store.IsComplete(_flow.Serial))throw new InvalidOperationException("このS/Nは完了済みです。保護解除が必要です。"); }
    }
    private void ChangeTestMode()
    {
        if(_operationInProgress || _flow.Measuring || _flow.SaveFailed || _flow.CaptureFailed || _leakRunning) { _modeBox.SelectedIndex=(int)_flow.Mode;return; }
        TestMode next=(TestMode)_modeBox.SelectedIndex;
        if (_modeBox.SelectedIndex < 0) return;
        if(next==TestMode.Motion && _flow.HasCapture) { _modeBox.SelectedIndex=(int)_flow.Mode;return; }
        bool templatePurposeChanged = (_flow.Mode == TestMode.Motion) != (next == TestMode.Motion);
        CloseJudgmentNotice();
        _flow.Mode=next;
        _modeSelected=true;
        if (templatePurposeChanged) ResetTemplate();
        RunOperation("モード選択",()=> { ConfigureProduct(requireSaveRoot:false); StopLeakOutput();_generator?.PrepareBurst(CurrentBurstSettings);
            // データ調整で校正を済ませてからデータ取りへ移ったときは、前回の保存先が残っていればここで校正記録を書く。
            WriteCalibrationRecordIfReady();return _modeBox.Text; });
        UpdateControls();
        // 接続確認が済んでいれば、モードを選んだ流れで校正画面を開く(まだなら接続確認の終わりで開く)。
        OpenCalibrationIfPending();
    }
    private GeneratorBurstSettings CurrentBurstSettings => As110MotionTest.GetSettings(
        isSdg1032X: _generator is Sdg1032XSession, isMotion: _flow.Mode == TestMode.Motion);
    private void UpdateProductionControls()
    {
        bool idle=!_operationInProgress && !_leakRunning && !_flow.Measuring;
        bool ready=idle && ConnectionsReady && _calibrationApplied && _templateLoaded && LoggerReadyToStart && _arduino.MeasurementInputReady;
        _modeBox.Enabled=idle && !_flow.SaveFailed && !_flow.CaptureFailed && !_flow.HasSaved;

        bool production=_flow.Mode==TestMode.Production;
        bool excelMode=_modeSelected && _flow.Mode!=TestMode.Motion;
        _productionPanel.Visible=_modeSelected;
        _protectionPanel.Visible=_actionPanel.Visible=_remeasureButton.Visible=excelMode;
        _savePathPanel.Visible=_chooseSaveRootButton.Visible=_rootBox.Visible=_releaseSerialButton.Visible=production;
        _saveButton.Visible=_leakOutputButton.Visible=_leakButton.Visible=_completeButton.Visible=production;
        _rangePanel.Visible=_rangeStartLabel.Visible=_rangeStartBox.Visible=_lastSerialLabel.Visible=_lastSerialBox.Visible=production;
        _serialPanel.Visible=_serialLabel.Visible=_serialBox.Visible=production;
        _saveRootWarningLabel.Text=production?SaveFolder.CheckRange(_rootBox.Text,_serialBox.Text.Trim()):"";
        _saveRootWarningLabel.Visible=_saveRootWarningLabel.Text.Length>0;
        _serialMissingLabel.Visible=production && _serialBox.Text.Trim().Length==0;
        _serialBox.Enabled=CanEnterSerial;
        _startButton.Text=_flow.Mode==TestMode.Motion?"測定開始":"データ調整";
        // 型式(参照波形)未選択・データ取りの保存先未選択は、押してからエラーにせず押せない状態にして「次にすること」で案内する(2026-11-06)。
        _startButton.Enabled=ready && !_flow.SaveFailed && !_flow.CaptureFailed && (!_reviewStage) && (_flow.Mode!=TestMode.Motion || MotionContactEnergized) && !_flow.SerialRangeFinished && !_flow.SerialMissing
            && (_flow.Mode==TestMode.Motion || _selectedReferencePath is not null) && (_flow.Mode!=TestMode.Production || _rootBox.Text.Length>0);
        _transferButton.Enabled=idle && _flow.HasCapture && !_flow.CaptureFailed && !_reviewStage;
        string saveBlockReason=GetSaveBlockReason();
        _saveButton.Enabled=saveBlockReason.Length==0;
        string saveAvailability=$"モード={_flow.Mode} / 処理中={_operationInProgress} / 測定中={_flow.Measuring} / 漏れチェック中={_leakRunning} / revision={_flow.Revision} / 転送revision={_flow.TransferredRevision} / 転送済み={_flow.HasTransfer} / ExcelOK={_lastExcelPass} / 保存後工程={_reviewStage} / 保存ボタン={_saveButton.Enabled} / 理由={saveBlockReason}";
        if(saveAvailability!=_saveAvailabilitySignature)
        {
            _saveAvailabilitySignature=saveAvailability;
            AppendLog("保存許可："+saveAvailability);
        }
        _leakOutputButton.Enabled=ready && _reviewStage && _flow.Mode==TestMode.Production && _flow.HasSaved && !_leakOutputReady;
        _leakButton.Enabled=ready && _reviewStage && _flow.Mode==TestMode.Production && _flow.HasSaved && _leakOutputReady;
        _completeButton.Enabled=idle && !_leakOutputReady && _flow.CanComplete && _arduino.MeasurementInputReady && ConnectionsReady;
        _remeasureButton.Text=_flow.HasSaved?"再測定へ戻る":"設定変更（測定を破棄）";
        _remeasureButton.Enabled=idle && (_reviewStage || _flow.HasCapture) && !_flow.SaveFailed;
        _retryCaptureButton.Visible=_flow.CaptureFailed;_retryCaptureButton.Enabled=idle && _flow.CaptureFailed && ConnectionsReady;
        string identity = _flow.Mode == TestMode.Adjustment ? "データ調整（保存・S/N設定なし）" : _flow.SerialMissing ? "測定開始S/N 未入力" : _flow.LastSerial.Length > 0 ? $"S/N {_flow.Serial}（最後 {_flow.LastSerial}）" : $"S/N {_flow.Serial}";
        _flowLabel.Text=$"{identity}　{(_leakRunning?"漏れチェック中":_flow.Measuring?"測定中":_flow.CaptureFailed?"一時保存失敗：再試行してください":_flow.SaveFailed?"保存失敗：同じ番号で再保存してください":_flow.HasSaved?$"一式保存済み／漏れチェック {_flow.LeakChecks}回":_flow.HasTransfer?$"Excel転送済み：{_excelJudgmentStatus}":_flow.HasCapture?"一時保存済み：Excel転送できます":_excelPreparing?"測定待ち（Excel準備中）":"測定待ち")}";
        if (_flow.Mode == TestMode.Motion) _flowLabel.Text = _flow.Measuring ? "動き検査中" : "動き検査：保存・S/N設定なし";
        if (_flow.SerialRangeFinished && !_flow.Measuring && !_flow.HasCapture)
            _flowLabel.Text = $"{identity}　最後のS/N {_flow.LastSerial} を超えています：保存先と測定開始S/Nを確認してください（測定開始できません）";
        if (_flow.SerialMissing && !_flow.Measuring && !_flow.HasCapture)
            _flowLabel.Text = $"{identity}　測定開始S/Nを入力してください（測定開始できません）";
        if(_leakOutputReady && !_leakRunning && !_generatorOutputUnknown)_flowLabel.Text+="／+30mA出力中：漏れ試験実施を押してください";
        if(_generatorOutputUnknown)_flowLabel.Text+="／発振器の出力状態不明：本体で出力OFFを確認し「確認」で再接続してください";
        if(_flow.Mode==TestMode.Production && _flow.HasTransfer && !_reviewStage && saveBlockReason.Length>0)
            _flowLabel.Text+=$"／保存待機：{saveBlockReason}";
        UpdateNextStepGuide(saveBlockReason);
        UpdateDeveloperContactControls();
        SendBoardDisplay();
    }
    // 「次にすること」の表示。データ調整(測定開始)ボタンが押せないときはその理由と次の操作を赤字で、押せるときは次の操作を緑字で出す。
    private void UpdateNextStepGuide(string saveBlockReason)
    {
        string blocked=GetStartBlockReason();
        if(blocked.Length>0)
        {
            _nextStepLabel.ForeColor=Color.Red;
            _nextStepLabel.Text=$"次にすること：{blocked}（「{_startButton.Text}」は押せません）";
            return;
        }
        if(!_flow.HasTransfer || _flow.HasSaved)_saveConflict=null;
        if(_saveConflict is not null)
        {
            _nextStepLabel.ForeColor=Color.Red;
            _nextStepLabel.Text=$"次にすること：{_saveConflict}。今回の測定で置き換えるなら「保存」→「はい」。前のデータを残すなら「設定変更（測定を破棄）」→ 測定開始S/N を次の番号にして測り直す（前のデータを合格扱いにするなら「完了番号の保護と解除」で S/N {_flow.Serial} にチェック）";
            return;
        }
        _nextStepLabel.ForeColor=Color.Green;
        string next;
        string change=_flow.Mode==TestMode.Production?"、S/N や型式を変えるなら「設定変更（測定を破棄）」":"";
        if(_flow.HasCapture && !_flow.HasTransfer) next="測定済みです。「Excel転送」を押してください（測り直すなら「"+_startButton.Text+"」"+change+"）";
        else if(_flow.HasTransfer && _flow.Mode==TestMode.Production && saveBlockReason.Length==0) next="Excel転送済みです。「保存」を押してください（測り直すなら「"+_startButton.Text+"」"+change+"）";
        else if(_flow.HasTransfer && _flow.Mode==TestMode.Production) next=$"Excel転送済み（{_excelJudgmentStatus}）。保存できません：{saveBlockReason}。測り直すなら「{_startButton.Text}」";
        else if(_flow.HasTransfer) next="Excel転送済みです。結果を確認し、測り直すなら「"+_startButton.Text+"」を押してください";
        else next=$"「{_startButton.Text}」を押してください（M5Stack の A ボタンでも始められます）";
        _nextStepLabel.Text="次にすること："+next;
    }
    // データ調整(測定開始)ボタンが押せない理由。空ならボタンは押せる。_startButton.Enabled の条件と同じ順で、最初に引っかかったものを返す。
    private string GetStartBlockReason()
    {
        if(_checkingConnections)return "接続確認中です。終わるまで待ってください";
        if(!ConnectionsReady)
        {
            var missing=new List<string>();
            if(!_arduino.IsConnected)missing.Add("M5Stack");
            if(_generator is null)missing.Add("発振器");
            else if(_generatorOutputUnknown)missing.Add("発振器の出力状態（本体で出力OFFを確認）");
            if(!_logger.IsInitialized || !_hardwareDetected)missing.Add("LOGGER／NR本体");
            return $"{string.Join("・",missing)} が未接続です。機器をつないで「確認」を押してください";
        }
        if(!_modeSelected)return "モードを選んでください";
        if(_excelPreparing)return "Excel を準備中です。終わるまで待ってください";
        if(_operationInProgress)return "処理中です。終わるまで待ってください";
        if(_flow.Measuring)return "測定中です";
        if(_leakRunning)return "漏れチェック中です";
        if(!_calibrationApplied)return CalibrationSupported?"校正値を入力してください（「校正値を入力」）":"雛形を読み込んでください（「雛形を読み込む」）";
        if(!_templateLoaded)return "LOGGER で雛形が開かれていません。「校正値を入力」で読み込み直してください";
        if(!LoggerReadyToStart)return $"LOGGER が{GetLoggerStateText()}です。LOGGER を停止してから「状態確認」を押してください";
        if(_flow.Mode!=TestMode.Motion && _selectedReferencePath is null)return "型式（参照波形）を選んでください";
        if(_flow.Mode==TestMode.Production)
        {
            if(_rootBox.Text.Length==0)return "「保存先選択」で保存先を選んでください";
            if(_flow.SerialMissing)return "測定開始S/N を入力してください";
            if(_flow.SerialRangeFinished)return $"最後の S/N {_flow.LastSerial} を超えています。保存先を選び直し、測定開始S/N を入力してください";
        }
        if(_flow.CaptureFailed)return "一時保存に失敗しています。「一時保存を再試行」を押してください";
        if(_flow.SaveFailed)return "保存に失敗しています。「保存」で同じ番号を再保存してください";
        if(_reviewStage)
        {
            if(_leakOutputReady)return "+30mA 出力中です。「漏れ試験実施」を押してください";
            if(_flow.CanComplete)return "漏れ試験が済みました。「合格」を押してください（もう一度なら「+30mA」）";
            return "一式保存済みです。「+30mA」→「漏れ試験実施」→「合格」の順に進めてください（やり直すなら「再測定へ戻る」）";
        }
        if(!_arduino.MeasurementInputReady)return "M5Stack からの状態通知が途切れています。M5Stack の表示とケーブルを確認してください";
        if(_flow.Mode==TestMode.Motion && !MotionContactEnergized)return "外部接点が ON になっていません";
        return "";
    }
    private string GetSaveBlockReason()
    {
        if(_operationInProgress)return "処理中";
        if(_leakRunning)return "漏れチェック中";
        if(_flow.Measuring)return "測定中";
        if(_flow.Mode!=TestMode.Production)return "データ取り以外";
        if(!_flow.HasTransfer)return "最新測定の転送未完了";
        if(!_lastExcelPass)return _excelJudgmentStatus;
        if(_reviewStage)return "一式保存後の工程";
        return "";
    }
    private string BuildBoardDisplaySignature()
    {
        string a=_reviewStage?"+30mA":_startButton.Text,b=_reviewStage?"漏れ試験実施":_flow.CaptureFailed?"一時保存再試行":"Excel転送",c=_reviewStage?"合格":_flow.Mode==TestMode.Production?"保存":"-";
        bool aa=_reviewStage?_leakOutputButton.Enabled:_startButton.Enabled, bb=_reviewStage?_leakButton.Enabled:_flow.CaptureFailed?_retryCaptureButton.Enabled:_transferButton.Enabled, cc=_reviewStage?_completeButton.Enabled:_saveButton.Enabled;
        if (_flow.Mode == TestMode.Motion) { a="測定開始"; b=c="-"; bb=cc=false; }
        string displaySerial = _flow.Mode == TestMode.Production ? _flow.Serial : "-";
        return $"{(int)_flow.Mode}:{(aa?1:0)}{(bb?1:0)}{(cc?1:0)}:{displaySerial}:{a}:{b}:{c}:{_flowLabel.Text.Replace(':',' ')}";
    }
    private void SendBoardDisplay()
    {
        if(!_arduino.IsConnected)return;
        string signature=BuildBoardDisplaySignature();
        if(signature!=_displaySignature){_displaySignature=signature;_displayEpoch++;}
        try { _arduino.SendDisplay($"UI:{_displayEpoch}:{_boardToken}:{signature}"); }
        catch(Exception ex){AppendLog("M5Stack表示送信失敗："+ex.Message);}
    }
    private void BoardButtonRequested(int button,long epoch,string token)
    {
        long received=Stopwatch.GetTimestamp();
        if(IsDisposed || !IsHandleCreated)return;
        BeginInvoke((Action)(async ()=> {
            if(_closeRequested || _repeatConfirmationShown || _operationInProgress || _leakRunning || !_arduino.MeasurementInputReady || token!=_boardToken || epoch!=_displayEpoch || Stopwatch.GetElapsedTime(received).TotalSeconds>=1)return;
            if (_flow.Mode == TestMode.Motion && button != 1) return;
            AppendLog($"M5Stack操作受付：{(char)('A'+button-1)} / モード={_flow.Mode} / revision={_flow.Revision} / 保存後工程={_reviewStage}");
            if(_reviewStage){if(button==1 && _leakOutputButton.Enabled)StartLeakOutput();else if(button==2 && _leakButton.Enabled)await LeakCheckAsync();else if(button==3 && _completeButton.Enabled)CompleteProduct();}
            else if(button==1 && _startButton.Enabled)ExecuteMeasurement(received,_arduino.MeasurementRequestVersion);
            else if(button==2){if(_flow.CaptureFailed && _retryCaptureButton.Enabled)RetryCapture();else if(_transferButton.Enabled)TransferLatest();}
            else if(button==3 && _saveButton.Enabled)SaveLatest();
        }));
    }
    private void CaptureLatest()
    {
        _flow.CaptureFailed=true;
        // トリガ待ちは停止とみなさず、残データの読み出し完了後だけ保存する。
        if (_logger.GetState() != 1)
            throw new InvalidOperationException("LOGGERの停止完了を確認できません。一時保存を再試行してください。");
        Directory.CreateDirectory(_sessionFolder);
        string extension=Path.GetExtension(GetDetectedTemplatePath());
        string path=Path.Combine(_sessionFolder,"latest"+extension),stage=Path.Combine(_sessionFolder,"capture"+extension);
        if(File.Exists(stage))File.Delete(stage);
        long result=_logger.SaveXdt(stage);
        if(result!=0 || !File.Exists(stage) || new FileInfo(stage).Length==0)throw new IOException($"LOGGERの一時保存に失敗（戻り値{result}）。");
        if(extension.Equals(".ydt",StringComparison.OrdinalIgnoreCase))
        {
            long count=_logger.GetDataCount();
            if(count!=_transferSettings.ExpectedSamples)throw new InvalidDataException($"測定点数が{count}点です。LOGGER設定を確認してください。");
            string csv=stage+".csv";if(File.Exists(csv))File.Delete(csv);
            long csvResult=_logger.SaveCsv(csv);
            if(csvResult!=0 || !File.Exists(csv) || new FileInfo(csv).Length==0)throw new IOException($"NR-Xの標準CSV一時保存に失敗（戻り値{csvResult}）。");
            File.Move(csv,path+".csv",true);
        }
        else
        {
            var info=XdtReader.Inspect(stage);
            if(info.Samples!=_transferSettings.ExpectedSamples)throw new InvalidDataException($"測定点数が{info.Samples}点です。{_transferSettings.ExpectedSamples}点ではありません。LOGGER設定を確認してください。");
        }
        File.Move(stage,path,true);_capturePath=path;_flow.CaptureSucceeded();
    }
    private void RetryCapture()
    {
        if(_operationInProgress || !_flow.CaptureFailed)return;
        RunOperation("一時保存再試行",()=>{RefreshLoggerState();if(!LoggerReadyToStart)throw new InvalidOperationException("LOGGER停止を待ってください。");try { CaptureLatest(); } catch(InvalidDataException) { _flow.Invalidate(); throw; } return "最新結果を一時保存しました。";});
    }
    private async void TransferLatest()
    {
        if(!_transferButton.Enabled)return;
        if(!ConfirmRepeatAfterPass("Excel転送"))return;
        if(!_transferButton.Enabled)return;
        bool transferred=false;
        var transferTimer=Stopwatch.StartNew();
        await RunOperationAsync("Excel転送",async ()=> {
            if(!_flow.HasCapture || _capturePath is null)throw new InvalidOperationException("最新測定がありません。");
            // 旧転送結果を先に無効化し、失敗時に古いExcelで保存できないようにする。
            _saveNotice?.Close(); _saveNotice=null;
            _lastExcelPass=false; _flow.TransferFailed();
            // 初回転送の途中で応答待ちになっても、この所有先を次の再試行へ引き継ぐ。
            _excel ??= new ExcelWorkSession();
            _excel=ExcelWorkSession.Open(ExcelTemplatePath,ExcelWorkPath,_capturePath,_transferSettings,_flow.Model,
                _flow.Mode==TestMode.Production?_flow.Serial:"-",_flow.MeasuredAt,_excel,AppendLog);
            var calculationTimer=Stopwatch.StartNew();
            bool pass=_lastExcelPass=await _excel.IsPassAsync();
            _excelJudgmentError="";
            _excelJudgmentStatus=pass?"両方OK":"判定NG・保存不可";
            AppendLog($"Excel判定：{_excel.JudgmentDescription} / 保存{(pass?"可":"不可")}");
            _flow.TransferSucceeded();
            transferred=true;
            AppendLog($"Excel転送時間：計算・判定={calculationTimer.Elapsed.TotalMilliseconds:F0}ms / 合計={transferTimer.Elapsed.TotalMilliseconds:F0}ms");
            return pass?"E21・L21：両方OK":"E21・L21：不合格（正式保存できません）";
        });
        if (transferred)
        {
            ShowJudgmentNotice();
            AppendLog($"Excel転送時間：表示処理完了まで合計={transferTimer.Elapsed.TotalMilliseconds:F0}ms");
        }
    }
    private void RefreshExcelJudgment()
    {
        if(_operationInProgress || _flow.Measuring || _leakRunning || !_flow.HasTransfer || _reviewStage || _excel is null)return;
        bool previous=_lastExcelPass;
        string previousStatus=_excelJudgmentStatus;
        try
        {
            bool? pass=_excel.ReadCurrentPass();
            _lastExcelPass=pass==true;
            _excelJudgmentStatus=pass is null?"Excel計算中・保存待機":pass.Value?"両方OK":"判定NG・保存不可";
            _excelJudgmentError="";
            if(previousStatus!=_excelJudgmentStatus)
                AppendLog(pass is null?"Excel判定：計算中のため保存待機":$"Excel判定更新：{_excel.JudgmentDescription} / 保存{(_lastExcelPass?"可":"不可")}");
        }
        catch(Exception ex)
        {
            _lastExcelPass=false;
            _excelJudgmentStatus="Excel判定取得不可・Excelを確認／再転送";
            if(_excelJudgmentError!=ex.Message)AppendLog("Excel判定取得失敗："+ex.Message);
            _excelJudgmentError=ex.Message;
        }
        if(previous!=_lastExcelPass || previousStatus!=_excelJudgmentStatus)
        {
            CloseJudgmentNotice();
            if(_excelJudgmentError.Length==0 && _excelJudgmentStatus!="Excel計算中・保存待機")ShowJudgmentNotice();
        }
    }
    private void SaveLatest()
    {
        if(!_saveButton.Enabled)return;
        RunOperation("正式保存",()=> {
            _saveNotice?.Close(); _saveNotice=null;
            _flow.SaveFailed=true;
            if(_excel is null || !_flow.HasTransfer || _capturePath is null)throw new InvalidOperationException("最新結果をExcelへ転送してください。");
            if(!_excel.IsPass()) { _lastExcelPass=false; _flow.SaveFailed=false;throw new InvalidOperationException("E21・L21の両方がOKではありません。"); }
            // 保存先フォルダー名「SN開始-最後」と合わないときは書き込まずに止める(何も書いていないので再保存の扱いにはしない)。
            string rangeProblem=SaveFolder.CheckRange(_rootBox.Text,_flow.Serial);
            if(rangeProblem.Length>0) { _flow.SaveFailed=false;throw new InvalidOperationException(rangeProblem); }
            var store=Store;using var held=store.Lock();
            if(store.IsComplete(_flow.Serial))throw new InvalidOperationException("完了済みのS/Nは上書きできません。");
            string root=Path.GetFullPath(_rootBox.Text),baseName=_flow.BaseName;
            string excel=Path.Combine(root,"Excel",baseName+".xlsx"),pdf=Path.Combine(root,"PDF",baseName+".pdf"),logger=Path.Combine(root,"LOGGER",baseName+Path.GetExtension(_capturePath));
            string ownership=root+"|"+baseName;
            string[] existing=new[]{excel,pdf,logger}.Where(File.Exists).ToArray();
            if(_ownedSaveBase!=ownership && !store.WasReleased(_flow.Serial) && existing.Length>0)
            {
                // 前に同じ S/N を保存したファイル(合格にはしていない)が残っている。まだ何も書いていないので「保存失敗」にはせず、
                // 上書きするかを人に聞く。上書きしないときは「次にすること」に取れる手を出す(2026-11-06、人の指摘: 何をすればいいか分からなかった)。
                _flow.SaveFailed=false;
                _saveConflict=$"S/N {_flow.Serial} の保存ファイルがすでにあります（{string.Join("・",existing.Select(f=>Path.GetFileName(Path.GetDirectoryName(f)!)))}）";
                UseWaitCursor=false;
                DialogResult answer;
                try
                {
                    answer=ErrorNotification.Show(this,
                        $"S/N {_flow.Serial} の保存ファイルがすでにあります（合格にはなっていません）。\n"+string.Join("\n",existing)+
                        "\n\n今回の測定で上書きしますか？\n「はい」：上書きして保存する\n「いいえ」：保存しない。前のデータを残すなら「設定変更（測定を破棄）」→ 測定開始S/N を次の番号にして測り直す",
                        "正式保存：同名ファイル",MessageBoxButtons.YesNo,MessageBoxIcon.Warning);
                }
                finally { UseWaitCursor=true; }
                if(answer!=DialogResult.Yes) return "同名ファイルがあるため保存しませんでした。"+_saveConflict;
                AppendLog("正式保存：同名ファイルを上書きします / "+string.Join(" / ",existing));
            }
            _saveConflict=null;
            _ownedSaveBase=ownership;
            foreach(string file in new[]{excel,pdf,logger})Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            // 生成は一時ファイルへ。個別の置換が失敗してもSavedRevisionは更新しない。
            string stage=Path.Combine(root,"tmp",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
            try
            {
                string x=Path.Combine(stage,"result.xlsx"),p=Path.Combine(stage,"result.pdf"),l=Path.Combine(stage,"result"+Path.GetExtension(logger));
                _excel.SaveCopies(x,p);File.Copy(_capturePath,l);
                foreach(string f in new[]{x,p,l})if(!File.Exists(f)||new FileInfo(f).Length==0)throw new IOException("保存ファイルが生成されませんでした。");
                File.Move(x,excel,true);File.Move(p,pdf,true);File.Move(l,logger,true);
                _flow.SaveSucceeded();_reviewStage=true;
            }
            finally {try{Directory.Delete(stage,true);}catch{}}
            return "Excel・PDF・LOGGERをすべて保存しました。漏れチェックを行ってください。";
        });
    }
    private void StartLeakOutput()
    {
        if(!_leakOutputButton.Enabled)return;
        RunOperation("+30mA",()=> {
            if(!_flow.HasSaved || _logger.GetState()!=1)throw new InvalidOperationException("一式保存とLOGGERの完全停止を確認してください。");
            if(_closeRequested)return "終了要求のため出力を開始しません。";
            try
            {
                _leakOutputReady=true;
                _generator!.SetDcOutput(5);
            }
            catch(Exception ex)
            {
                // 出力開始の失敗に加えて停止も失敗した場合は、両方の内容を表示する。
                try { StopLeakOutput(); }
                catch(Exception stopEx) { throw new InvalidOperationException(ex.Message+"\n"+stopEx.Message,ex); }
                throw;
            }
            return "DC +5V出力を開始しました。漏れ試験実施でLOGGER測定を開始してください。";
        });
    }
    // 出力OFFの送信に成功したときだけ、出力中の表示（_leakOutputReady）を解除する。
    // 失敗時は出力が残っている可能性があるため「出力状態不明」とし、測定・合格等を止める。
    private void StopLeakOutput()
    {
        var generator=_generator;
        if(generator is null)
        {
            if(!_leakOutputReady && !_generatorOutputUnknown)return;
            _generatorOutputUnknown=true;
            throw new InvalidOperationException(GeneratorOutputUnknownMessage("発振器が未接続です"));
        }
        try { generator.StopOutput(); }
        catch(Exception ex)
        {
            _generatorOutputUnknown=true;
            throw new InvalidOperationException(GeneratorOutputUnknownMessage(ex.Message),ex);
        }
        MarkGeneratorOutputOff("出力OFFを送信しました");
    }
    private void MarkGeneratorOutputOff(string detail)
    {
        if(_generatorOutputUnknown)AppendLog($"発振器：{detail}。出力状態不明を解除しました。");
        _leakOutputReady=false;
        _generatorOutputUnknown=false;
        _generatorOutputAlertShown=false;
    }
    private static string GeneratorOutputUnknownMessage(string reason)=>
        $"発振器の出力OFFを確認できません（{reason}）。出力（漏れ試験のDC +5Vを含む）が残っている可能性があります。" +
        "発振器本体で出力をOFFにし、接続を確認して「確認」を押してください。";
    // 処理の外（状態監視等）で出力状態不明になったときの警告。
    // 終了処理中は表示しない。「確認」中は再接続を試した結果をまとめて表示する。
    private void WarnGeneratorOutputUnknown(string message)
    {
        AppendLog(message);
        if(_closeRequested || _checkingConnections || _generatorOutputAlertShown || IsDisposed || !IsHandleCreated)return;
        _generatorOutputAlertShown=true;
        ErrorNotification.Show(this,message,"発振器の出力状態不明",MessageBoxButtons.OK,MessageBoxIcon.Warning);
    }
    private async Task LeakCheckAsync()
    {
        if(!_leakButton.Enabled)return;
        await RunOperationAsync("漏れチェック",async ()=> {
            if(!_flow.HasSaved || !_leakOutputReady)throw new InvalidOperationException("最新データ一式を保存し、+30mAを押してください。");
            if(_logger.GetState()!=1)throw new InvalidOperationException("LOGGERの完全停止を確認してから漏れチェックしてください。");
            _leakRunning=true;_leakCancel=new CancellationTokenSource();UpdateControls();
            try
            {
                TopMost=false;
                try { AppendLog(_logger.BringToFront()?"漏れチェック：LOGGERを最前面に表示しました。":"漏れチェック：Windowsが前面切替を許可しませんでした。LOGGER画面をクリックしてください。"); }
                catch(Exception ex){AppendLog("漏れチェックのLOGGER前面表示："+ex.Message);}
                try
                {
                    await LeakCheckSequence.RunAsync(
                        ()=>{long result=_logger.StartSingleAcquisition();AppendLog($"漏れチェック LOGGER Start：戻り値={result}");return result;},
                        StopLeakOutput,
                        StopLeakLoggerAsync,_leakCancel.Token);
                }
                catch(OperationCanceledException) when(_closeAfterLeak) { return "アプリ終了のため発振器出力とLOGGER収集を停止しました。"; }
                if(!ConnectionsReady || !_arduino.MeasurementInputReady)throw new IOException("通信を確認できません。漏れチェックをやり直してください。");
                _flow.LeakSucceeded();return "出力中のままLOGGER収集を開始し、収集開始から10秒で出力と収集を停止しました。";
            }
            finally
            {
                _leakRunning=false;_leakCancel?.Dispose();_leakCancel=null;
            }
        });
        if(_closeAfterLeak && !IsDisposed) BeginInvoke((Action)Close);
    }
    private async Task StopLeakLoggerAsync()
    {
        _logger.Stop();
        AppendLog("漏れチェック LOGGER Stop：停止を要求しました。");
        var elapsed=Stopwatch.StartNew();
        long state;
        while((state=_logger.GetState())!=1)
        {
            if(elapsed.Elapsed>=TimeSpan.FromSeconds(5))throw new TimeoutException($"漏れチェックのLOGGER停止を確認できません（状態{state}）。再確認してください。");
            await Task.Delay(100);
        }
        _logger.ResetMeasurementStart();
        _measurementState.MeasurementStarted=false;
        _measurementState.State=1;
        AppendLog("漏れチェック LOGGER：完全停止を確認しました。");
    }
    private void CompleteProduct()
    {
        if(!_completeButton.Enabled)return;
        RunOperation("合格確定",()=> {
            if(!_flow.CanComplete || _excel is null)throw new InvalidOperationException("最新結果の保存と漏れチェックが必要です。");
            var store=Store;using var held=store.Lock();if(store.IsComplete(_flow.Serial))throw new IOException("このS/Nは完了済みです。");
            _excel.CloseVerified(); // 閉じる処理が失敗したら記録・加算しない。
            store.Append("COMPLETE",_flow.Serial,_flow.Model.Name,_flow.BaseName);
            string previous=_flow.Serial;_flow.Advance();_serialBox.Text=_flow.Serial;_reviewStage=false;_ownedSaveBase=null;
            _excel.Dispose();_excel=null;_capturePath=null;
            return _flow.SerialRangeFinished ? $"S/N{previous}完了。最後のS/N {_flow.LastSerial} まで終わりました。次の範囲は保存先を選び直し、測定開始S/Nを入力してください。" : $"S/N{previous}完了。次はS/N{_flow.Serial}です。";
        });
        // 製品の付け替え中に、次のS/N用のExcelを準備する。
        if(_excel is null)RequestExcelPreparation();
    }
    private void ReleaseCompletedSerial()
    {
        if(_operationInProgress || _flow.Measuring || _leakRunning)return;
        // 完了記録は保存先の「完了記録.txt」にあるので、保存先が無いと一覧を出せない。
        if(_rootBox.Text.Length==0)
        {
            ErrorNotification.Show(this,"保存先を選択してください。\n完了番号の保護は保存先の「完了記録.txt」で管理しています。","完了番号の保護と解除",MessageBoxButtons.OK,MessageBoxIcon.Warning);
            return;
        }
        // 一覧の 1 行: 記録のある S/N(保護中・解除済み)と、記録は無いがデータのある S/N(記録なし)。
        List<(string Serial,bool Protected,string Model,string BaseName,string Note)> entries;
        try
        {
            var store=Store;using var held=store.Lock();
            entries=store.Entries().Select(e=>(e.Serial,e.Protected,"-","再保護",e.Protected?"":"（解除済み）")).ToList();
            var (unrecorded,_)=SaveFolder.FindUnrecorded(_rootBox.Text,store.Read());
            entries.AddRange(unrecorded.Select(f=>(f.Serial,false,f.Model,f.BaseName,"（記録なし）")));
            entries=entries.OrderBy(e=>long.Parse(e.Serial)).ToList();
        }
        catch(Exception ex) { AppendLog($"保護と解除：エラー / {ex.Message}");ErrorNotification.Show(this,ex.Message,"完了番号の保護と解除",MessageBoxButtons.OK,MessageBoxIcon.Error);return; }
        if(entries.Count==0)
        {
            ErrorNotification.Show(this,$"完了記録のあるS/Nも、保存されたデータもありません。\n保存先: {_rootBox.Text}","完了番号の保護と解除",MessageBoxButtons.OK,MessageBoxIcon.Information);
            return;
        }
        // 番号順に並べ、保護中はチェック付き、解除済み・記録なしはチェックなしで出す。
        // チェックを外せば解除、付ければ保護(記録なしは型式とファイル名を記録に残す)。「確定」を押したときにまとめて反映する(誤クリックはキャンセルで戻せる)。
        using var dialog=new Form {Text="完了番号の保護と解除",ClientSize=new Size(420,380),StartPosition=FormStartPosition.CenterParent,TopMost=true,MinimizeBox=false,MaximizeBox=false};
        var guide=new Label{Left=12,Top=10,Width=396,Height=68,Text=$"保存先: {_rootBox.Text}\nチェックあり＝保護中。チェックを外すと保護を解除し、付けると保護します（記録なしのデータも保護できます）。\n「確定」で反映します。既存ファイルは残します。"};
        var list=new CheckedListBox{Left=12,Top=82,Width=396,Height=240,CheckOnClick=true,Font=new Font(Font.FontFamily,12)};
        foreach(var entry in entries) list.Items.Add("S/N "+entry.Serial+entry.Note,entry.Protected);
        var ok=new Button{Text="確定",Left=208,Top=332,Width=96,Height=36,DialogResult=DialogResult.OK};
        var cancel=new Button{Text="キャンセル",Left=312,Top=332,Width=96,Height=36,DialogResult=DialogResult.Cancel};
        dialog.Controls.AddRange([guide,list,ok,cancel]);dialog.AcceptButton=ok;dialog.CancelButton=cancel;
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        var desired=entries.Select((e,i)=>(e.Serial,Protected:list.GetItemChecked(i),e.Model,e.BaseName)).ToArray();
        if(desired.Select(d=>d.Protected).SequenceEqual(entries.Select(e=>e.Protected))){AppendLog("保護と解除：チェックを変えた S/N が無いので何もしていません。");return;}
        RunOperation("保護と解除",()=> {
            var store=Store;using var held=store.Lock();
            var (released,reprotected)=store.SetProtection(desired);
            var parts=new List<string>();
            if(released.Length>0) parts.Add($"S/N {string.Join(", ",released)} の保護を解除しました（既存ファイルは残しています）");
            if(reprotected.Length>0) parts.Add($"S/N {string.Join(", ",reprotected)} を保護しました");
            return parts.Count==0?"選んだS/Nはすでにその状態でした。":string.Join("。",parts)+"。";
        });
    }
    private void EditTransferSettings()
    {
        if(_operationInProgress || _flow.Measuring || _leakRunning || _flow.HasSaved || _flow.SaveFailed)return;
        using var dialog=new Form {Text="Excel転送設定",ClientSize=new Size(480,300),StartPosition=FormStartPosition.CenterParent,TopMost=true};
        _flow.TransferFailed();
        var grid=new PropertyGrid {Dock=DockStyle.Fill,SelectedObject=_transferSettings,ToolbarVisible=false,PropertySort=PropertySort.NoSort};
        var ok=new Button{Text="設定保存",Dock=DockStyle.Bottom,Height=40,DialogResult=DialogResult.OK};dialog.Controls.AddRange([grid,ok]);
        if(dialog.ShowDialog(this)==DialogResult.OK)RunOperation("転送設定保存",()=>{_transferSettings.Save();_rootBox.Text=_transferSettings.SaveRoot;return "設定しました。";});
        UpdateControls();
    }
}
