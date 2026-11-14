using System.Diagnostics;
using System.Globalization;
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
    // 測定開始S/N に、保存ファイルがすでにある番号(合格にはなっていない)を入れたときの知らせ。S/N の行の下に出す。止めはしない(測り直しのことがある)。
    // 2026-11-14、全体見直し・区切り 5(人の指摘: 保存の確認まで進んでから気づくのでは遅い。番号を入れた時点で分かるようにする)。
    private readonly Label _serialSavedLabel = new() { AutoSize=true,ForeColor=Color.DarkOrange,MaximumSize=new Size(680,0),Visible=false,Margin=new Padding(0,0,0,6) };
    // 知らせを作ったときの保存先・S/N と文(作ったあとで保存先・S/N が変わったら出さない)。
    private (string Root,string Serial,string Text)? _savedSerialNotice;
    // この起動で自分が保存した S/N か(測り直しで自分のファイルを知らせない)。
    private bool OwnsSavedSerial(string root) => _ownedSaveBase is { } owned && owned.StartsWith($"{root}|{_flow.Model.Name}_SN{_flow.Serial}_",StringComparison.OrdinalIgnoreCase);
    // 保存先に、いまの測定開始S/N の保存ファイルがあるかを見直す(完了記録を読み直すとき・合格で次の番号へ進んだときに呼ぶ)。
    // 完了済み(保護中)の番号は専用の案内が出るので対象外。出荷データの保存先がまだ無いときに、フォルダーを作らない。
    private void RefreshSavedSerialNotice()
    {
        var previous=_savedSerialNotice;
        _savedSerialNotice=null;
        if(_flow.Mode!=TestMode.Production || _flow.SerialMissing || _rootBox.Text.Length==0 || _serialCompleted)return;
        try
        {
            string root=Path.GetFullPath(SaveRootPath);
            if(!Directory.Exists(root) || OwnsSavedSerial(root))return;
            var files=SaveFolder.FindSerialFiles(root,_flow.Serial);
            if(files.Count==0)return;
            string names=string.Join("、",files.Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase));
            _savedSerialNotice=(root,_flow.Serial,$"S/N {_flow.Serial} は保存ファイルがすでにあります（{names}。合格にはなっていません）。測り直しなら、このまま進めてください。番号の入れ間違いなら、測定開始S/N を直してください。");
            if(previous!=_savedSerialNotice)AppendLog("測定開始S/N："+_savedSerialNotice.Value.Text);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { AppendLog("保存ファイルの確認に失敗："+ex.Message); }
    }
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
    // この起動で人が保存先を選んだか。データ取りでは、選ぶまで校正画面を開かず、校正記録も書かない(保存先 → 校正 → 型式 → S/N の順)。
    private bool _saveRootChosen;
    // データ取り以外は保存先を使わないので、校正へ進める。
    private bool SaveRootReady => _flow.Mode!=TestMode.Production || _saveRootChosen;
    // 保存先を決める(「保存先選択」と転送設定の保存先の変更から呼ぶ)。校正を適用済みなら、選んだ保存先へ校正記録を書く。
    // 書けない保存先は受け付けず、選ぶ前の状態に戻す。受け付けたら、データ取りで校正がまだなら校正画面を開く。
    private bool SelectSaveRoot(string path)
    {
        string previousRoot=_rootBox.Text;
        bool previousChosen=_saveRootChosen;
        _rootBox.Text=path;
        _saveRootChosen=true;
        try { WriteCalibrationRecordIfReady(); }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _rootBox.Text=previousRoot;
            _saveRootChosen=previousChosen;
            AppendLog($"保存先選択：校正記録を書けません / {ex.Message}");
            ErrorNotification.Show(this,"この保存先には校正記録を書けません。別の保存先を選んでください。\n"+CalibrationRecordFile.FolderPath(path)+"\n"+ex.Message,"保存先の選択",MessageBoxButtons.OK,MessageBoxIcon.Error);
            return false;
        }
        _transferSettings.SaveRoot=path;_transferSettings.Save();
        ApplySaveRootRange();
        if(_flow.Mode==TestMode.Production && CanChangeSetup) RunOperation("保存先選択",()=>{ConfigureProduct(requireSaveRoot:false);return _rootBox.Text;});
        UpdateControls();
        ReportUnrecordedData();
        // 保存先が決まったので、接続確認が済んでいれば校正画面を開く(まだなら接続確認の終わりで開く)。
        OpenCalibrationIfPending();
        return true;
    }
    // 保存先の Excel/PDF/LOGGER にある、完了記録に無い S/N のデータを知らせる(保護は「完了番号の保護と解除」の画面で付ける)。
    private void ReportUnrecordedData()
    {
        if(_rootBox.Text.Length==0) return;
        try
        {
            var store=Store;using var held=store.Lock();
            var (unrecorded,unreadable)=SaveFolder.FindUnrecorded(SaveRootPath,store.Read());
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

        // 保存先は起動のたびに空から始め、人が「保存先選択」で選ぶ(前回の保存先は選択画面の初期フォルダーにだけ使う)。
        // 前回の保存先を入れたままにすると、データ取りで校正を適用した時点で前回のロットのフォルダーへ校正記録が書かれ、
        // そのフォルダーが無い(USB を抜いた等)と保存先を選ぶ前に校正が止まっていた(2026-11-06、人の指摘)。
        ApplySaveRootRange();
        _chooseSaveRootButton.Click+=(_,_)=> {
            if(_operationInProgress || _flow.Measuring || _flow.SaveFailed || _leakRunning || _flow.HasSaved) return;
            using var dialog=new FolderBrowserDialog();
            string previous=_rootBox.Text.Length>0?_rootBox.Text:_transferSettings.SaveRoot;
            if(previous.Length>0 && Directory.Exists(previous)) dialog.SelectedPath=previous;
            if(dialog.ShowDialog(this)!=DialogResult.OK) return;
            SelectSaveRoot(dialog.SelectedPath);
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
        _actionPanel=new TableLayoutPanel { AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,RowCount=3,Margin=Padding.Empty };
        // 漏れ試験の試験秒数・しきい値(データ取りだけ表示。MainForm.LeakJudgment.cs)。
        _actionPanel.Controls.Add(mainActions,0,0);_actionPanel.Controls.Add(BuildLeakSettingsRow(),0,1);_actionPanel.Controls.Add(subActions,0,2);
        // 供給圧力(出荷データの工程がある型式だけ。MainForm.ShippingData.cs)は一番上。
        Control[] rows=[BuildSupplyPressureRow(),_shippingNoticeLabel,_savePathPanel,_saveRootWarningLabel,_rangePanel,_serialPanel,_serialSavedLabel,_protectionPanel,_actionPanel,_flowLabel];
        _productionPanel.RowCount=rows.Length;
        for(int i=0;i<rows.Length;i++){ _productionPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); _productionPanel.Controls.Add(rows[i],0,i); }

        // 入力中もフォルダー名との照合(赤字)と「入力してください」を更新する。
        _serialBox.TextChanged+=(_,_)=>UpdateControls();
        // 確定は欄を離れたとき(Tab・ほかの欄のクリック)と Enter の両方(2026-11-06、人の指摘: Enter を押しても「次にすること」が変わらず、ほかの欄をクリックしないと確定しなかった)。
        _serialBox.Leave+=(_,_)=>ApplySerialInput();
        _serialBox.KeyDown+=(_,e)=>{
            if(e.KeyCode!=Keys.Enter)return;
            e.Handled=e.SuppressKeyPress=true;  // Enter の警告音を出さない
            ApplySerialInput();
        };
        return _productionPanel;
    }
    // 最後に確定した測定開始S/N の入力文字列。Enter で確定した直後に欄を離れても、同じ内容を二度確定(ログを二重に出す)しないための覚え。
    private string? _appliedSerialText;
    // 測定開始S/N の欄の内容を確定する(Leave と Enter から呼ぶ)。
    private void ApplySerialInput()
    {
        if(!CanEnterSerial)return;
        string text=_serialBox.Text.Trim();
        // 工程の S/N とも同じときだけ飛ばす。合格で次の番号へ進んだあとに直前の番号(前に確定した文字)へ戻したとき、
        // 覚えだけで比べると確定されず、状態欄と M5Stack が次の番号のまま残っていた(2026-11-13、全体見直し)。
        if(text==_appliedSerialText && text==_flow.Serial)return;
        RunOperation("測定開始S/N設定",()=>{
            // S/N が空のまま残っていた測定結果(データ調整のものなど)は、S/N を入れた時点で破棄される(S/N なしではデータ取りに使えない)。
            bool discarded=_flow.HasCapture && _flow.SerialMissing && text.Length>0;
            ConfigureProduct(requireSaveRoot:false);
            _appliedSerialText=text;
            return _flow.Serial.Length>0?$"S/N {_flow.Serial} から"+(discarded?"（S/N なしの前の測定結果は破棄しました）":"")+(_serialCompleted?"（完了済みのため測定開始できません。保護解除が必要）":""):"測定開始S/N を入力してください";});
        // 測定開始S/N が入るまで Excel の事前準備を待たせているので、入ったらここで始める。
        if(_flow.Serial.Length>0 && _excel is null && _templateLoaded)RequestExcelPreparation();
    }
    private bool CanChangeSetup => !_operationInProgress && !_flow.Measuring && !_flow.SaveFailed && !_flow.CaptureFailed && !_leakRunning && !_flow.HasCapture;
    // 測定開始S/N を入れられるとき。測定結果が残っていても、S/N がまだ空なら入れられる(2026-11-06。データ調整の結果が残ったままデータ取りへ移ると、
    // 「S/N を入力してください」と案内しながら欄が灰色のままになり、何もできなくなっていた)。S/N が入った結果を変えるときは「設定変更（測定を破棄）」。
    private bool CanEnterSerial => _flow.Mode==TestMode.Production && !_operationInProgress && !_flow.Measuring && !_flow.SaveFailed && !_flow.CaptureFailed && !_leakRunning && (!_flow.HasCapture || _flow.SerialMissing);
    private ProductModel SelectedModel => _selectedModel;
    // 完了記録は保存先ごと。出荷データは「出荷データ(350kPa)」の中に別に持つ(SaveRootPath)。
    private CompletionStore Store => new(_rootBox.Text.Length>0?SaveRootPath:throw new InvalidOperationException("保存先を選択してください。"));
    private void ConfigureProduct(bool requireSaveRoot=true)
    {
        // 出荷データ以外は、データ取りの合格を確かめない(前の出荷データの結果を残さない)。
        if (_flow.Mode == TestMode.Motion) { _judgedPassed=true; return; }
        if (_flow.Mode == TestMode.Adjustment) { _flow.ConfigureAdjustment(SelectedModel); _judgedPassed=true; return; }
        _flow.SetLastSerial(_lastSerialBox.Text.Trim());
        // 測定開始S/N が空のときは型式だけ持ち、測定開始はできない(赤字「入力してください」で案内)。
        var (model,judged)=SelectedStage;
        if(_serialBox.Text.Trim().Length==0)
        {
            _flow.ConfigureWithoutSerialFor(model,judged); _serialCompleted=false; _judgedPassed=true;
            // 測定開始のとき(requireSaveRoot)は S/N なしで始めない。ふだんは「データ調整」を押せなくして止めているが、欄を空にして確定する前に
            // M5Stack の A ボタンで始めると、ボタンの状態が古いままここへ来る(2026-11-13、全体見直し)。
            if(requireSaveRoot)throw new InvalidOperationException("測定開始S/N を入力してください。");
            return;
        }
        _flow.ConfigureFor(_serialBox.Text.Trim(),model,judged);
        if(!_flow.ShippingData)_judgedPassed=true;
        // 測定開始のとき(requireSaveRoot)は、保存先フォルダー名「SN開始-最後」と合わない S/N では始めない(保存で初めて止まると、測定と転送が無駄になる)。
        if(requireSaveRoot && SaveRootRangeProblem.Length>0)throw new InvalidOperationException(SaveRootRangeProblem);
        if(_flow.Mode==TestMode.Production && (requireSaveRoot || _rootBox.Text.Length>0))
        {
            RefreshSerialCompletion(requireSaveRoot);
            // 測定開始のとき(requireSaveRoot)だけ止める。S/N 入力・保存先選択・モード選択のときはポップアップにせず、「次にすること」とボタンで知らせる(2026-11-06、人の指摘)。
            if(_serialCompleted && requireSaveRoot)throw new InvalidOperationException("このS/Nは完了済みです。保護解除が必要です。");
            // 出荷データは、データ取りで合格した S/N だけ測る(人の指示: アプリが確かめる)。
            if(!_judgedPassed && requireSaveRoot)throw new InvalidOperationException(JudgedBlockedText(afterMeasurement:false));
        }
    }
    // データ取りで、工程の S/N が保存先フォルダー名「SN開始-最後」と合わないときの文(合うとき・データ取り以外・保存先なし・S/N 空は "")。
    // 合わないあいだは「データ調整」を押せなくし、「次にすること」に出す(2026-11-13、全体見直し。以前は赤字を出すだけで測定は始められ、保存で初めて止まった)。
    // 保存先なし・S/N 空・最後 S/N 超えは、それぞれ専用の案内が先に出る。フォルダー名はここで毎回読むので、測定結果を残したまま保存先を選び直したときも今の保存先で確かめる。
    private string SaveRootRangeProblem =>
        _flow.Mode==TestMode.Production && !_flow.SerialMissing ? SaveFolder.CheckRange(_rootBox.Text,_flow.Serial) : "";
    // 測定開始S/N が完了記録で保護中か(押してからエラーにせず、「次にすること」とデータ調整ボタンに反映する)。
    private bool _serialCompleted;
    // 完了記録を読み直して _serialCompleted を更新する。S/N を入れたとき・保存先を選んだとき・合格で次の番号へ進んだとき・保護と解除を変えたときに呼ぶ。
    private void RefreshSerialCompletion(bool throwOnError=false)
    {
        _serialCompleted=false;
        try
        {
            RefreshJudgedPass(throwOnError);
            if(_flow.Mode!=TestMode.Production || _flow.SerialMissing || _rootBox.Text.Length==0)return;
            try { var store=Store;using var held=store.Lock();_serialCompleted=store.IsComplete(_flow.Serial); }
            catch(Exception ex) when(!throwOnError) { AppendLog("完了記録の確認に失敗："+ex.Message); }
        }
        // 完了済みかどうかが決まってから、保存ファイルがすでにある番号かを見直す。
        finally { RefreshSavedSerialNotice(); }
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
        // モードを変えたら供給圧力は型式の値(データ取り)に戻す(出荷データの 350 kPa をやめる)。
        _shippingDataSelected=false;
        if (templatePurposeChanged) ResetTemplate();
        RunOperation("モード選択",()=> { ReloadTemplateForMode(); ConfigureProduct(requireSaveRoot:false); StopLeakOutput();_generator?.PrepareBurst(CurrentBurstSettings);
            // データ調整で校正を済ませてからデータ取りへ移ったときは、前回の保存先が残っていればここで校正記録を書く。
            WriteCalibrationRecordIfReady();return _modeBox.Text; });
        UpdateControls();
        // 接続確認が済んでいれば、モードを選んだ流れで校正画面を開く(まだなら接続確認の終わりで開く)。
        OpenCalibrationIfPending();
    }
    // データ取りは、参照波形で選んだ型式の測る時間に合わせる(AS110-006 は周波数が半分。データ調整・動き検査は型式によらない。MeasurementProfile)。
    private GeneratorBurstSettings CurrentBurstSettings => As110MotionTest.GetSettings(
        isSdg1032X: _generator is Sdg1032XSession, isMotion: _flow.Mode == TestMode.Motion,
        lengthFactor: MeasurementProfile.LengthFactor(_selectedModel, _flow.Mode));
    // 参照波形で選んだ型式・いまのモードの転送の設定(AS110-006 のデータ取りは 13000 点・10 点平均。MeasurementProfile)。
    private TransferSettings ModelTransferSettings => _transferSettings.ForModel(_selectedModel, _flow.Mode);
    // データ調整とデータ取りで LOGGER の雛形が違う型式(AS110-006 はデータ取りだけ専用の 13000 点。MeasurementProfile)は、
    // モードを替えたときに、いま読み込んでいる雛形と違えば同じ校正値で読み直す(参照波形で型式を替えたときと同じ)。
    // 校正前(雛形をまだ読んでいない)なら何もしない(校正のときにそのモードの雛形を読む)。専用の雛形が無ければ雛形待ちに戻して知らせる。
    private void ReloadTemplateForMode()
    {
        if(_flow.Mode==TestMode.Motion || !_templateLoaded || !_calibrationApplied || _workingTemplateSource is null)return;
        string template;
        try { template=GetDetectedTemplatePath(); }
        catch { ResetTemplate(); throw; }
        if(!string.Equals(template,_workingTemplateSource,StringComparison.OrdinalIgnoreCase))ReloadTemplateForModel(template);
    }
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
        _saveButton.Visible=_completeButton.Visible=production;
        // 出荷データ(判定しない)は漏れ試験をしないので、漏れ試験のボタンと欄を出さない。
        _leakOutputButton.Visible=_leakButton.Visible=_leakSettingsPanel.Visible=production && !_flow.ShippingData;
        _completeButton.Text=_flow.ShippingData?"完了":"合格";
        UpdateSupplyPressureControls();
        _leakSecondsBox.Enabled=_leakThresholdBox.Enabled=_leakLowerLimitBox.Enabled=_leakFailDisplayBox.Enabled=!_leakRunning;
        RefreshLeakLowerLimitBox();
        _rangePanel.Visible=_rangeStartLabel.Visible=_rangeStartBox.Visible=_lastSerialLabel.Visible=_lastSerialBox.Visible=production;
        _serialPanel.Visible=_serialLabel.Visible=_serialBox.Visible=production;
        _saveRootWarningLabel.Text=production?SaveFolder.CheckRange(_rootBox.Text,_serialBox.Text.Trim()):"";
        _saveRootWarningLabel.Visible=_saveRootWarningLabel.Text.Length>0;
        _serialMissingLabel.Visible=production && _serialBox.Text.Trim().Length==0;
        // 保存ファイルがすでにある番号の知らせ。保存したあと・完了済み・自分が保存した番号の測り直し・作ったあとで保存先や番号が変わったときは出さない。
        bool savedNotice=production && _savedSerialNotice is { } notice && !_flow.SerialMissing && !_flow.HasSaved && !_serialCompleted && _rootBox.Text.Length>0
            && notice.Serial==_flow.Serial && string.Equals(notice.Root,Path.GetFullPath(SaveRootPath),StringComparison.OrdinalIgnoreCase) && !OwnsSavedSerial(notice.Root);
        _serialSavedLabel.Text=savedNotice?_savedSerialNotice!.Value.Text:"";
        _serialSavedLabel.Visible=savedNotice;
        _serialBox.Enabled=CanEnterSerial;
        _startButton.Text=_flow.Mode==TestMode.Motion?"測定開始":"データ調整";
        // 型式(参照波形)未選択・データ取りの保存先未選択は、押してからエラーにせず押せない状態にして「次にすること」で案内する(2026-11-06)。
        _startButton.Enabled=ready && !_flow.SaveFailed && !_flow.CaptureFailed && (!_reviewStage) && (_flow.Mode!=TestMode.Motion || MotionContactEnergized) && !_flow.SerialRangeFinished && !_flow.SerialMissing
            && (_flow.Mode==TestMode.Motion || _selectedReferencePath is not null) && (_flow.Mode!=TestMode.Production || _rootBox.Text.Length>0) && !_serialCompleted && _judgedPassed
            && SaveRootRangeProblem.Length==0;
        _transferButton.Enabled=idle && _flow.HasCapture && !_flow.CaptureFailed && !_reviewStage;
        string saveBlockReason=GetSaveBlockReason();
        _saveButton.Enabled=saveBlockReason.Length==0;
        string saveAvailability=$"モード={_flow.Mode} / 処理中={_operationInProgress} / 測定中={_flow.Measuring} / 漏れチェック中={_leakRunning} / revision={_flow.Revision} / 転送revision={_flow.TransferredRevision} / 転送済み={_flow.HasTransfer} / ExcelOK={_lastExcelPass} / 保存後工程={_reviewStage} / 保存ボタン={_saveButton.Enabled} / 理由={saveBlockReason}";
        if(saveAvailability!=_saveAvailabilitySignature)
        {
            _saveAvailabilitySignature=saveAvailability;
            AppendLog("保存許可："+saveAvailability);
        }
        _leakOutputButton.Enabled=ready && _reviewStage && _flow.Mode==TestMode.Production && !_flow.ShippingData && _flow.HasSaved && !_leakOutputReady;
        _leakButton.Enabled=ready && _reviewStage && _flow.Mode==TestMode.Production && !_flow.ShippingData && _flow.HasSaved && _leakOutputReady;
        // 漏れ判定が不合格・判定できなかったときは、転送設定で回避を許したときだけ押せる(押すと確認する)。
        _completeButton.Enabled=idle && !_leakOutputReady && (_flow.CanComplete || (_flow.LeakNeedsOverride && _transferSettings.AllowLeakOverride)) && _arduino.MeasurementInputReady && ConnectionsReady;
        _remeasureButton.Text=_flow.HasSaved?"再測定へ戻る":"設定変更（測定を破棄）";
        _remeasureButton.Enabled=idle && (_reviewStage || _flow.HasCapture) && !_flow.SaveFailed;
        _retryCaptureButton.Visible=_flow.CaptureFailed;_retryCaptureButton.Enabled=idle && _flow.CaptureFailed && ConnectionsReady;
        string identity = (_flow.ShippingData ? $"出荷データ {_flow.Model.Name}（{_flow.Model.SupplyPressure} kPa）　" : "") + (_flow.Mode == TestMode.Adjustment ? "データ調整（保存・S/N設定なし）" : _flow.SerialMissing ? "測定開始S/N 未入力" : _flow.LastSerial.Length > 0 ? $"S/N {_flow.Serial}（最後 {_flow.LastSerial}）" : $"S/N {_flow.Serial}");
        _flowLabel.Text=$"{identity}　{(_leakRunning?"漏れチェック中":_flow.Measuring?"測定中":_flow.CaptureFailed?"一時保存失敗：再試行してください":_flow.SaveFailed?"保存失敗：同じ番号で再保存してください":_flow.HasSaved&&_flow.ShippingData?"一式保存済み（出荷データ・漏れ試験なし）":_flow.HasSaved?$"一式保存済み／漏れチェック {_flow.LeakChecks}回"+(_flow.LastLeakOutcome is LeakOutcome last?$"（最後：{LeakOutcomeText.Of(last)}）":""):_flow.HasTransfer?$"Excel転送済み：{_excelJudgmentStatus}":_flow.HasCapture?"一時保存済み：Excel転送できます":_excelPreparing?"測定待ち（Excel準備中）":"測定待ち")}";
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
            // 一時保存できなかった測定を破棄したとき(ResetTemplate)は、接続・校正をやり直す間も、測り直しが要ることを出し続ける。
            _nextStepLabel.Text=$"次にすること：{blocked}（「{_startButton.Text}」は押せません）"+(_captureDiscardedNotice is null?"":$"　※{_captureDiscardedNotice}");
            return;
        }
        if(!_flow.HasTransfer || _flow.HasSaved)_saveConflict=null;
        if(_saveConflict is not null)
        {
            _nextStepLabel.ForeColor=Color.Red;
            _nextStepLabel.Text=$"次にすること：{_saveConflict}。今回の測定を保存するなら「保存」→「はい」（同じ名前のファイルは上書き、別の名前のファイルは残ります）。前のデータだけを残すなら「設定変更（測定を破棄）」→ 測定開始S/N を次の番号にして測り直す（前のデータを合格扱いにするなら「完了番号の保護と解除」で S/N {_flow.Serial} にチェック）";
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
        _nextStepLabel.Text="次にすること："+next+(_captureDiscardedNotice is null?"":$"　※{_captureDiscardedNotice}");
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
        // データ取りは保存先を選んでから校正に進む(校正記録を保存先に書くため)。
        if(!SaveRootReady)return "「保存先選択」で保存先を選んでください（選ぶと校正画面が開きます）";
        if(!_calibrationApplied)return CalibrationSupported?"校正値を入力してください（「校正値を入力」）":"雛形を読み込んでください（「雛形を読み込む」）";
        if(!_templateLoaded)return "LOGGER で雛形が開かれていません。「校正値を入力」で読み込み直してください";
        if(!LoggerReadyToStart)return $"LOGGER が{GetLoggerStateText()}です。LOGGER を停止してから「状態確認」を押してください";
        if(_flow.Mode!=TestMode.Motion && _selectedReferencePath is null)return "型式（参照波形）を選んでください";
        if(_flow.Mode==TestMode.Production)
        {
            if(_rootBox.Text.Length==0)return "「保存先選択」で保存先を選んでください";
            if(_flow.SerialMissing)return "測定開始S/N を入力してください";
            if(_flow.SerialRangeFinished)return $"最後の S/N {_flow.LastSerial} を超えています。保存先を選び直し、測定開始S/N を入力してください";
            // 保存先フォルダー名と合わない(範囲より前の番号・フォルダー名に範囲が無い・測定結果を残したまま保存先を選び直した)。
            if(SaveRootRangeProblem.Length>0)return SaveRootRangeProblem.TrimEnd('。');
            // 測定結果が残っているのに完了済み(測定を始めたあとで別の PC などから保護された)ときは、解除してから保存する一本道だけを出す。
            if(_serialCompleted && _flow.HasCapture)return $"S/N {_flow.Serial} は完了済み（保護中）になっています。今回の測定で上書きするなら「完了番号の保護と解除」で S/N {_flow.Serial} のチェックを外してから「保存」を押してください";
            if(_serialCompleted)return $"測定開始S/N {_flow.Serial} は完了済み（保護中）です。別の番号にするか、「完了番号の保護と解除」で S/N {_flow.Serial} のチェックを外してください";
            if(JudgedPassUnreadable)return JudgedReadFailedNextStep;
            if(!_judgedPassed)return JudgedNotPassedText+$"。番号を確かめるか、供給圧力を {_selectedModel.SupplyPressure} kPa に戻してください";
        }
        if(_flow.CaptureFailed)return "一時保存に失敗しています。「一時保存を再試行」を押してください";
        if(_flow.SaveFailed)return "保存に失敗しています。「保存」で同じ番号を再保存してください";
        if(_reviewStage)
        {
            if(_flow.ShippingData)return "保存しました（出荷データのため漏れ試験はしません）。「完了」を押してください（やり直すなら「再測定へ戻る」）";
            if(_leakOutputReady)return "+30mA 出力中です。「漏れ試験実施」を押してください";
            if(_flow.CanComplete)return $"漏れ試験が済みました（{LastLeakText()}）。「合格」を押してください（もう一度なら「+30mA」）";
            if(_flow.LeakNeedsOverride)return _transferSettings.AllowLeakOverride
                ? $"漏れ判定：{LastLeakText()}。やり直すなら「+30mA」→「漏れ試験実施」。人の判断で合格にするなら「合格」（確認が出ます）"
                : $"漏れ判定：{LastLeakText()}。「+30mA」→「漏れ試験実施」でやり直してください（「合格」は押せません）";
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
        if(_serialCompleted)return $"S/N {_flow.Serial} は完了済み（保護中）";
        // データ調整の結果を残したままデータ取りへ移ると、S/N が空(保存先もまだのことがある)のまま転送まで進める。保存はできないので押せなくする
        // (2026-11-13、全体見直し。以前は押せて、保存先なしだと保存失敗が残り、「保存先選択」も S/N 欄も使えなくなっていた)。
        // S/N を入れると測定結果は破棄されるので、S/N のある測定結果は保存先を選んでから測ったものだけ(保存先なしは SaveLatest でも止める)。
        if(_flow.SerialMissing)return "測定開始S/N 未入力";
        if(!_lastExcelPass)return _excelJudgmentStatus;
        if(_reviewStage)return "一式保存後の工程";
        return "";
    }
    // 出荷データの転送後の判定欄の表示(判定しない)。
    private string ShippingJudgmentText => $"判定なし（{_flow.JudgedModel?.SupplyPressure} kPa のデータ取りで合格済み）";
    private string BuildBoardDisplaySignature()
    {
        string a=_reviewStage?"+30mA":_startButton.Text,b=_reviewStage?"漏れ試験実施":_flow.CaptureFailed?"一時保存再試行":"Excel転送",c=_reviewStage?"合格":_flow.Mode==TestMode.Production?"保存":"-";
        bool aa=_reviewStage?_leakOutputButton.Enabled:_startButton.Enabled, bb=_reviewStage?_leakButton.Enabled:_flow.CaptureFailed?_retryCaptureButton.Enabled:_transferButton.Enabled, cc=_reviewStage?_completeButton.Enabled:_saveButton.Enabled;
        if (_flow.Mode == TestMode.Motion) { a="測定開始"; b=c="-"; bb=cc=false; }
        // 出荷データは保存後に漏れ試験をせず、C で完了する。
        if (_reviewStage && _flow.ShippingData) { a=b="-"; aa=bb=false; c="完了"; }
        // PC で画面(保存先選択・転送設定・完了番号の保護と解除・通知)を開いている間は、M5Stack のボタンも押せない表示にする(BoardButtonRequested でも断る)。
        if (ModalDialogOpen) aa=bb=cc=false;
        string displaySerial = _flow.Mode == TestMode.Production ? _flow.Serial : "-";
        return $"{(int)_flow.Mode}:{(aa?1:0)}{(bb?1:0)}{(cc?1:0)}:{displaySerial}:{a}:{b}:{c}:{_flowLabel.Text.Replace(':',' ')}";
    }
    private void SendBoardDisplay()
    {
        // 切断の処理中は送らない(送信の失敗がまた切断の通知になるため。MainForm.Measurement.cs の ArduinoDisconnected)。
        if(!_arduino.IsConnected || _arduinoDisconnectHandling)return;
        string signature=BuildBoardDisplaySignature();
        if(signature!=_displaySignature){_displaySignature=signature;_displayEpoch++;}
        try { _arduino.SendDisplay($"UI:{_displayEpoch}:{_boardToken}:{signature}"); }
        catch(Exception ex){AppendLog("M5Stack表示送信失敗："+ex.Message);}
    }
    // PC でモーダルの画面(ダイアログ・メッセージボックス)を開いていて、メイン画面が操作を受け付けない間 true。
    // 表示中のメイン画面が Windows の上で無効になっているかで見る(Enabled プロパティはモーダルの間も true のまま)。表示前(検査の非表示の画面)は false。
    private bool ModalDialogOpen => IsHandleCreated && Visible && !CanFocus;
    private void BoardButtonRequested(int button,long epoch,string token)
    {
        long received=Stopwatch.GetTimestamp();
        if(IsDisposed || !IsHandleCreated)return;
        BeginInvoke((Action)(async ()=> {
            if(_closeRequested)return;
            // PC でモーダルの画面を開いている間は受け付けない。画面を開いていても PC のボタンの「押せるか」(Enabled)は変わらないので、
            // それだけを見ていると、画面の裏で測定・転送・保存が進んだ(例: 「転送設定」を開いたまま M5Stack で保存 → 「設定保存」で保存済みが取り消される。
            // 2026-11-13、全体見直し・区切り 3)。
            if(ModalDialogOpen){AppendLog($"M5Stack操作を受け付けません：{(char)('A'+button-1)} / PC で画面（設定・確認・通知）を開いています。閉じてからもう一度押してください。");return;}
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
            if(count!=ModelTransferSettings.ExpectedSamples)throw new InvalidDataException($"測定点数が{count}点です。{ModelTransferSettings.ExpectedSamples}点ではありません。LOGGER設定を確認してください。");
            string csv=stage+".csv";if(File.Exists(csv))File.Delete(csv);
            long csvResult=_logger.SaveCsv(csv);
            if(csvResult!=0 || !File.Exists(csv) || new FileInfo(csv).Length==0)throw new IOException($"NR-Xの標準CSV一時保存に失敗（戻り値{csvResult}）。");
            File.Move(csv,path+".csv",true);
        }
        else
        {
            var info=XdtReader.Inspect(stage);
            if(info.Samples!=ModelTransferSettings.ExpectedSamples)throw new InvalidDataException($"測定点数が{info.Samples}点です。{ModelTransferSettings.ExpectedSamples}点ではありません。LOGGER設定を確認してください。");
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
            // データ取りでも S/N が空(データ調整の結果のままデータ取りへ移った)なら、データ調整と同じ「-」を見出しに書く。
            // 空文字を書くと見出しの「S/N 数字」が無くなり、S/N を入れた後の転送で置き換えられず、S/N の無い Excel・PDF が保存される(事前準備が S/N を待つのと同じ理由)。
            _excel=ExcelWorkSession.Open(ExcelTemplatePath,ExcelWorkPath,_capturePath,ModelTransferSettings,_flow.Model,
                _flow.Mode==TestMode.Production && !_flow.SerialMissing?_flow.Serial:"-",_flow.MeasuredAt,_excel,AppendLog);
            var calculationTimer=Stopwatch.StartNew();
            bool pass=await _excel.IsPassAsync();
            _excelJudgmentError="";
            if(_flow.ShippingData)
            {
                // 出荷データは判定しない(490 kPa のデータ取りで合格済み)。計算が終われば保存できる。判定セルはログに残すだけ。
                _lastExcelPass=true;
                _excelJudgmentStatus=ShippingJudgmentText;
                AppendLog($"Excel判定：{_excel.JudgmentDescription}（出荷データのため使いません）/ 保存可");
            }
            else
            {
                _lastExcelPass=pass;
                _excelJudgmentStatus=pass?"両方OK":"判定NG・保存不可";
                AppendLog($"Excel判定：{_excel.JudgmentDescription} / 保存{(pass?"可":"不可")}");
            }
            _flow.TransferSucceeded();
            transferred=true;
            AppendLog($"Excel転送時間：計算・判定={calculationTimer.Elapsed.TotalMilliseconds:F0}ms / 合計={transferTimer.Elapsed.TotalMilliseconds:F0}ms");
            return _flow.ShippingData?ShippingJudgmentText:pass?"E21・L21：両方OK":"E21・L21：不合格（正式保存できません）";
        });
        if (transferred)
        {
            // 出荷データは合否のポップアップを出さず、Excel を前に出すだけ。
            if(_flow.ShippingData)ShowTransferredExcel();else ShowJudgmentNotice();
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
            // 出荷データは判定しないので、計算が終わっていれば保存できる。
            _lastExcelPass=_flow.ShippingData?pass is not null:pass==true;
            _excelJudgmentStatus=pass is null?"Excel計算中・保存待機":_flow.ShippingData?ShippingJudgmentText:pass.Value?"両方OK":"判定NG・保存不可";
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
            // 「保存失敗」(同じ番号で再保存)にするのは、保存先へ書き始めてからだけ(下の「ここから書き込む」)。
            // それより前で止まったときは何も書いていないので、保存失敗にしない(2026-11-13、全体見直し。以前は最初に保存失敗にしてから
            // 止まる所ごとに戻していて、戻し忘れた所(保存先なし・完了記録を読めない)で保存失敗が残った)。
            if(_excel is null || !_flow.HasTransfer || _capturePath is null)throw new InvalidOperationException("最新結果をExcelへ転送してください。");
            if(_flow.SerialMissing)throw new InvalidOperationException("測定開始S/N を入力してください（S/N なしの測定結果は保存できません）。");
            if(_rootBox.Text.Length==0)throw new InvalidOperationException("保存先を選択してください。");
            if(!_flow.ShippingData && !_excel.IsPass()) { _lastExcelPass=false;throw new InvalidOperationException("E21・L21の両方がOKではありません。"); }
            // 出荷データは、保存の直前にもデータ取りの合格を確かめる(測ったあとで保護を外された場合など)。
            if(_flow.ShippingData)
            {
                RefreshJudgedPass();
                if(!_judgedPassed)throw new InvalidOperationException(JudgedBlockedText(afterMeasurement:true));
            }
            // 保存先フォルダー名「SN開始-最後」と合わないときは書き込まずに止める。
            string rangeProblem=SaveFolder.CheckRange(_rootBox.Text,_flow.Serial);
            if(rangeProblem.Length>0)throw new InvalidOperationException(rangeProblem);
            var store=Store;using var held=store.Lock();
            if(store.IsComplete(_flow.Serial))
            {
                // 測定を始めたあとで(別の PC などから)完了済みになった番号。まだ何も書いていないので「保存失敗」にはせず、
                // 完了済みとして「保存」を押せなくし、「次にすること」に解除してから保存する道筋を出す(2026-11-06。以前は保存失敗が残って S/N 欄と「設定変更」が固まっていた)。
                _serialCompleted=true;
                throw new InvalidOperationException($"S/N {_flow.Serial} は完了済み（保護中）のため上書きできません。\n今回の測定で上書きするなら「完了番号の保護と解除」で S/N {_flow.Serial} のチェックを外してから「保存」を押してください。");
            }
            string root=Path.GetFullPath(SaveRootPath),baseName=_flow.BaseName;
            string excel=Path.Combine(root,"Excel",baseName+".xlsx"),pdf=Path.Combine(root,"PDF",baseName+".pdf"),logger=Path.Combine(root,"LOGGER",baseName+Path.GetExtension(_capturePath));
            string ownership=root+"|"+baseName;
            // 同じ S/N の前の保存ファイルを、名前が違っても探す(日付・型式が違うもの、前の版の名前〈末尾 _6500〉。SaveFolder.FindSerialFiles)。
            // 同じ名前のものは今回の保存で上書きされ、別の名前のものは消えずに残る。2026-11-13 より前は同じ日付の名前しか見ず、
            // 日付が変わってから測り直すと、聞かれないまま同じ S/N のファイルが 2 組残った(全体見直し)。
            var (overwritten,kept)=SaveFolder.FindSaveConflicts(root,_flow.Serial,[excel,pdf,logger]);
            if(_ownedSaveBase!=ownership && !store.WasReleased(_flow.Serial) && overwritten.Length+kept.Length>0)
            {
                // 前に同じ S/N を保存したファイル(合格にはしていない)が残っている。まだ何も書いていないので「保存失敗」にはせず、
                // 保存するかを人に聞く。保存しないときは「次にすること」に取れる手を出す(2026-11-06、人の指摘: 何をすればいいか分からなかった)。
                _saveConflict=$"S/N {_flow.Serial} の保存ファイルがすでにあります（{string.Join("・",overwritten.Concat(kept).Select(f=>Path.GetFileName(Path.GetDirectoryName(f)!)).Distinct())}）";
                UseWaitCursor=false;
                DialogResult answer;
                try
                {
                    answer=ErrorNotification.Show(this,SaveFolder.DescribeSaveConflicts(_flow.Serial,baseName,overwritten,kept),
                        "正式保存：同じ S/N のファイル",MessageBoxButtons.YesNo,MessageBoxIcon.Warning);
                }
                finally { UseWaitCursor=true; }
                if(answer!=DialogResult.Yes) return "同じ S/N の保存ファイルがあるため保存しませんでした。"+_saveConflict;
                if(overwritten.Length>0)AppendLog("正式保存：同じ名前のファイルを上書きします / "+string.Join(" / ",overwritten));
            }
            // 別の名前のファイルは残る。聞かなかったとき(保護を解除した S/N・この起動で自分が保存した S/N)も、残ることをログに残す。
            string keptNote=kept.Length>0?$"（S/N {_flow.Serial} の別の名前のファイルが {kept.Length} 件残っています：{string.Join("、",kept.Select(Path.GetFileName))}）":"";
            _saveConflict=null;
            _ownedSaveBase=ownership;
            // ここから書き込む。途中で止まったら保存失敗(同じ番号で再保存)。成功したら SaveSucceeded が戻す。
            _flow.SaveFailed=true;
            // 前に保存した Excel・PDF を開いたまま保存し直すと、置き換えのところで止まる。どのファイルを閉じるかを日本語で出す
            // (2026-11-14、全体見直し・区切り 5。それまでは「Access to the path is denied.」だけが出た)。
            const string retry="閉じたあと、もう一度「保存」を押してください（同じ番号で保存し直します）。";
            foreach(string file in new[]{excel,pdf,logger})FileAccessAdvice.Run("保存先",Path.GetDirectoryName(file)!,()=>Directory.CreateDirectory(Path.GetDirectoryName(file)!),retry,"保存先のフォルダーが消えていないか確かめてください");
            // 生成は一時ファイルへ。個別の置換が失敗してもSavedRevisionは更新しない。
            string stage=Path.Combine(root,"tmp",Guid.NewGuid().ToString("N"));FileAccessAdvice.Run("保存先",stage,()=>Directory.CreateDirectory(stage),retry,"保存先のフォルダーが消えていないか確かめてください");
            bool keepStage=false;
            try
            {
                string x=Path.Combine(stage,"result.xlsx"),p=Path.Combine(stage,"result.pdf"),l=Path.Combine(stage,"result"+Path.GetExtension(logger));
                if(_flow.ShippingData)_excel.SaveCopiesWithoutJudgment(x,p);else _excel.SaveCopies(x,p);
                File.Copy(_capturePath,l);
                foreach(string f in new[]{x,p,l})if(!File.Exists(f)||new FileInfo(f).Length==0)throw new IOException("保存ファイルが生成されませんでした。");
                // 置き換えが途中で止まったら、置き換えた分を元に戻す(SaveReplace)。戻せたら保存先は保存の前のままなので、保存失敗にしない。
                try { SaveReplace.ReplaceAll([(x,excel),(p,pdf),(l,logger)],stage); }
                catch(SaveReplaceException ex)
                {
                    string openHint=$"「{Path.GetFileName(ex.Path)}」を Excel や PDF の画面で開いていたら閉じてください";
                    if(ex.Restored)
                    {
                        _flow.SaveFailed=false;
                        throw new FileAccessAdviceException(FileAccessAdvice.Text("保存先のファイル",ex.Path,ex.InnerException!,
                            "今回の分は保存していません（保存先のファイルは保存の前のままです）。閉じたあと、もう一度「保存」を押してください。",openHint),ex);
                    }
                    // 戻せなかったときは、よけておいた前のファイルを消さない(一時フォルダーを残す)。保存失敗のままにして、同じ番号で保存し直してもらう。
                    keepStage=true;
                    throw new FileAccessAdviceException(FileAccessAdvice.Text("保存先のファイル",ex.Path,ex.InnerException!,
                        "置き換えの途中で止まり、元に戻せませんでした。保存先に今回と前回のファイルが混ざっています。"+retry+$"\n前のファイルの控え：{stage}",openHint),ex);
                }
                _flow.SaveSucceeded();_reviewStage=true;
            }
            finally {if(!keepStage)try{Directory.Delete(stage,true);}catch{}}
            return (_flow.ShippingData?$"Excel・PDF・LOGGERをすべて保存しました（{root}）。「完了」を押してください（漏れ試験はしません）。":"Excel・PDF・LOGGERをすべて保存しました。漏れチェックを行ってください。")+keptNote;
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
    // 漏れ試験の実施(LeakCheckAsync)は MainForm.LeakJudgment.cs。
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
            bool overriding=!_flow.CanComplete && _flow.LeakNeedsOverride && _transferSettings.AllowLeakOverride;
            if((!_flow.CanComplete && !overriding) || _excel is null)throw new InvalidOperationException(_flow.LeakNeedsOverride
                ? $"漏れ判定が合格していません（{LastLeakText()}）。「+30mA」から漏れ試験をやり直してください。"
                : "最新結果の保存と漏れチェックが必要です。");
            var store=Store;using var held=store.Lock();if(store.IsComplete(_flow.Serial))throw new IOException("このS/Nは完了済みです。");
            // 初期運用の回避(SPEC「漏れ試験の自動判定」5): 人に確かめ、漏れ試験記録に残してから合格にする。
            if(overriding && !ConfirmLeakOverride())return "漏れ判定が合格していないため、合格にしませんでした。";
            // 閉じる処理が失敗したら記録・加算しない。人が Excel(またはブック)を先に閉じていたときは閉じた扱いで進める(2026-11-06。以前は COM の切断エラーで合格が止まっていた)。
            bool excelAlreadyClosed=_excel.CloseVerifiedOrAlreadyClosed();
            if(excelAlreadyClosed)AppendLog("合格確定：Excel は先に閉じられていました（閉じた扱いで記録します）。");
            store.Append("COMPLETE",_flow.Serial,_flow.Model.Name,_flow.BaseName);
            // 次の番号は確定済みとして覚える(欄を離れただけで、同じ番号をもう一度確定してログを出さないように)。
            string previous=_flow.Serial;_flow.Advance();_serialBox.Text=_appliedSerialText=_flow.Serial;_reviewStage=false;_ownedSaveBase=null;
            // 次の番号がすでに完了済み(測り直しの途中など)なら、押す前に分かるようにしておく。
            _serialCompleted=store.IsComplete(_flow.Serial);
            // 出荷データは次の番号もデータ取りで合格しているかを確かめておく(データ取りの完了記録はロックのファイルが別)。
            RefreshJudgedPass();
            // 次の番号に保存ファイルがすでにあれば(前に測って合格にしていない)、押す前に分かるようにしておく。
            RefreshSavedSerialNotice();
            _excel.Dispose();_excel=null;_capturePath=null;
            return _flow.SerialRangeFinished ? $"S/N{previous}完了。最後のS/N {_flow.LastSerial} まで終わりました。次の範囲は保存先を選び直し、測定開始S/Nを入力してください。" : $"S/N{previous}完了。次はS/N{_flow.Serial}です。";
        });
        // 製品の付け替え中に、次のS/N用のExcelを準備する。
        if(_excel is null)RequestExcelPreparation();
    }
    // その S/N がいま測定中(データ取りで、その番号の測定結果が残っている)か。保護画面でその番号を保護させないために使う。番号は先頭の 0 を無視して比べる。
    private bool IsSerialUnderMeasurement(string serial)
        => _flow.Mode==TestMode.Production && !_flow.SerialMissing && _flow.HasCapture
           && long.TryParse(serial,NumberStyles.None,CultureInfo.InvariantCulture,out long n) && n==long.Parse(_flow.Serial,CultureInfo.InvariantCulture);
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
            var (unrecorded,_)=SaveFolder.FindUnrecorded(SaveRootPath,store.Read());
            entries.AddRange(unrecorded.Select(f=>(f.Serial,false,f.Model,f.BaseName,"（記録なし）")));
            // いま測定中(測定結果が残っている)の番号は保護できない。測定の途中で保護すると「保存」が止まり、解除しないと先へ進めなくなるため(2026-11-06、人の判断)。
            entries=entries.Select(e=>!e.Protected && IsSerialUnderMeasurement(e.Serial)?(e.Serial,e.Protected,e.Model,e.BaseName,e.Note+"（測定中のため保護できません）"):e)
                .OrderBy(e=>long.Parse(e.Serial)).ToList();
        }
        catch(Exception ex) { AppendLog($"保護と解除：エラー / {ex.Message}");ErrorNotification.Show(this,ex.Message,"完了番号の保護と解除",MessageBoxButtons.OK,MessageBoxIcon.Error);return; }
        if(entries.Count==0)
        {
            ErrorNotification.Show(this,$"完了記録のあるS/Nも、保存されたデータもありません。\n保存先: {SaveRootPath}","完了番号の保護と解除",MessageBoxButtons.OK,MessageBoxIcon.Information);
            return;
        }
        // 番号順に並べ、保護中はチェック付き、解除済み・記録なしはチェックなしで出す。
        // チェックを外せば解除、付ければ保護(記録なしは型式とファイル名を記録に残す)。「確定」を押したときにまとめて反映する(誤クリックはキャンセルで戻せる)。
        using var dialog=new Form {Text="完了番号の保護と解除",ClientSize=new Size(420,380),StartPosition=FormStartPosition.CenterParent,TopMost=true,MinimizeBox=false,MaximizeBox=false};
        var guide=new Label{Left=12,Top=10,Width=396,Height=68,Text=$"保存先: {SaveRootPath}\nチェックあり＝保護中。チェックを外すと保護を解除し、付けると保護します（記録なしのデータも保護できます）。\n「確定」で反映します。既存ファイルは残します。"};
        var list=new CheckedListBox{Left=12,Top=82,Width=396,Height=240,CheckOnClick=true,Font=new Font(Font.FontFamily,12)};
        foreach(var entry in entries) list.Items.Add("S/N "+entry.Serial+entry.Note,entry.Protected);
        // 測定中の番号にチェックを付けようとしても付かない(外すのはできる)。
        list.ItemCheck+=(_,e)=>{ if(e.NewValue==CheckState.Checked && !entries[e.Index].Protected && IsSerialUnderMeasurement(entries[e.Index].Serial))e.NewValue=e.CurrentValue; };
        var ok=new Button{Text="確定",Left=208,Top=332,Width=96,Height=36,DialogResult=DialogResult.OK};
        var cancel=new Button{Text="キャンセル",Left=312,Top=332,Width=96,Height=36,DialogResult=DialogResult.Cancel};
        dialog.Controls.AddRange([guide,list,ok,cancel]);dialog.AcceptButton=ok;dialog.CancelButton=cancel;
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        var desired=entries.Select((e,i)=>(e.Serial,Protected:list.GetItemChecked(i),e.Model,e.BaseName)).ToArray();
        if(desired.Select(d=>d.Protected).SequenceEqual(entries.Select(e=>e.Protected))){AppendLog("保護と解除：チェックを変えた S/N が無いので何もしていません。");return;}
        RunOperation("保護と解除",()=> {
            var store=Store;using var held=store.Lock();
            var (released,reprotected)=store.SetProtection(desired);
            // 今の測定開始S/N の保護が変わったかもしれないので、データ調整ボタンと「次にすること」に反映する。
            if(!_flow.SerialMissing)_serialCompleted=store.IsComplete(_flow.Serial);
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
        // 表で直すのは設定の写し。「設定保存」を押したときだけ元の設定に取り込み、× で閉じたら何も変えない(転送済みの結果もそのまま)。
        // 以前は表が設定をその場で書き換え、開いただけで転送済みを取り消していた(2026-11-13、全体見直し)。
        var draft=_transferSettings.CreateDraft();
        var grid=new PropertyGrid {Dock=DockStyle.Fill,SelectedObject=draft,ToolbarVisible=false,PropertySort=PropertySort.NoSort};
        var ok=new Button{Text="設定保存",Dock=DockStyle.Bottom,Height=40,DialogResult=DialogResult.OK};
        // 漏れ試験の初期運用の回避(SPEC「漏れ試験の自動判定」5)。軌道に乗ったら外す(人の方針)。
        var allowLeakOverride=new CheckBox{Text="漏れ判定が不合格でも、人の判断で合格にできる（初期運用）",Dock=DockStyle.Bottom,Height=32,Padding=new Padding(8,0,0,0),Checked=_transferSettings.AllowLeakOverride};
        dialog.Controls.AddRange([grid,allowLeakOverride,ok]);
        // 「設定保存」のときに値を確かめ、だめなら理由を出して画面を閉じない(直せる)。× で閉じるときは確かめない(何も変えないため)。
        // 2026-11-14、全体見直し・区切り 5。それまでは何でも保存でき、測定したあとの転送で初めて止まった。
        dialog.FormClosing+=(_,e)=>{
            if(dialog.DialogResult!=DialogResult.OK)return;
            var problems=draft.FindProblems(ExistingExcelTemplates());
            if(problems.Count==0)return;
            e.Cancel=true;
            AppendLog("転送設定：保存しません / "+string.Join(" ",problems));
            ErrorNotification.Show(dialog,"この設定は保存できません。直してから「設定保存」を押してください（やめるなら × で閉じる）。\n\n・"+string.Join("\n・",problems),"転送設定の確認",MessageBoxButtons.OK,MessageBoxIcon.Warning);
        };
        string previousSaveRoot=_transferSettings.SaveRoot;
        if(dialog.ShowDialog(this)!=DialogResult.OK){AppendLog("転送設定：保存せずに閉じました（設定は変えていません）。");return;}
        RunOperation("転送設定保存",()=>{
            // 設定が変わるので、前の設定で転送した結果では保存させない(転送し直す)。
            _flow.TransferFailed();
            _transferSettings.ApplyEdited(draft);
            bool overrideChanged=_transferSettings.AllowLeakOverride!=allowLeakOverride.Checked;
            _transferSettings.AllowLeakOverride=allowLeakOverride.Checked;_transferSettings.Save();
            return "設定しました。"+(overrideChanged?(allowLeakOverride.Checked?"漏れ判定の不合格を人の判断で合格にできます。":"漏れ判定の不合格では合格にできません。"):"");});
        UpdateControls();
        // 転送設定の「保存先フォルダ」を書き換えたときは、「保存先選択」で選んだのと同じ扱いにする(変えていなければ今の保存先のまま)。
        if(_transferSettings.SaveRoot!=previousSaveRoot && _transferSettings.SaveRoot.Length>0) SelectSaveRoot(_transferSettings.SaveRoot);
    }
}
