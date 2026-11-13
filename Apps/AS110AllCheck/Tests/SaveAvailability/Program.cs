using System.Reflection;
using System.Windows.Forms;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--foreground-target")) { ForegroundProbe.RunTarget(); return; }
        if (args.Contains("--calibration-foreground")) { DialogForegroundTests.RunCalibrationOnly(); return; }
        // --no-mainform-display: 実MainFormを表示する検証(機器の初期化が始まり、機器のないPCでは止まる)だけを飛ばし、合否ポップアップの検証は行う。
        if (!args.Contains("--no-dialogs") && !args.Contains("--no-mainform-display")) DialogForegroundTests.Run();
        var assembly=Assembly.Load("AS110AllCheck");
        CalibrationDialogTests.Run(assembly);
        LeakJudgeWindowTests.Run(assembly);
        var type=assembly.GetType("PSCAutomation.Apps.AS110AllCheck.MainForm")!;
        using var form=(Form)Activator.CreateInstance(type)!;
        object Field(string name)=>type.GetField(name,Private)!.GetValue(form)!;
        void Call(string name)=>type.GetMethod(name,Private)!.Invoke(form,null);
        var modeBox=(ComboBox)Field("_modeBox");
        if(modeBox.SelectedIndex!=-1 || (bool)Field("_modeSelected"))
            throw new Exception("Startup must wait for an explicit mode selection");
        // 未選択のモード欄は空欄ではなく赤文字の「未設定」を描く。Windows は未選択の表示欄を Index=-1 で描かせる(DrawToBitmap では呼ばれない)ので、
        // 同じ引数で描画処理を直接呼び、赤い画素があること・選択後は赤が消えることで確かめる。
        if(modeBox.DrawMode!=DrawMode.OwnerDrawFixed)throw new Exception("Mode box must draw the unset text itself");
        _=modeBox.Handle;
        var onDrawItem=typeof(ComboBox).GetMethod("OnDrawItem",Private)!;
        bool DrawsRed(int index,string imageName)
        {
            using var bitmap=new Bitmap(Math.Max(modeBox.Width,1),Math.Max(modeBox.ItemHeight,1));
            using(var graphics=Graphics.FromImage(bitmap))
                onDrawItem.Invoke(modeBox,[new DrawItemEventArgs(graphics,modeBox.Font,new Rectangle(Point.Empty,bitmap.Size),index,DrawItemState.None)]);
            bitmap.Save(Path.Combine(AppContext.BaseDirectory,imageName));
            for(int y=0;y<bitmap.Height;y++)for(int x=0;x<bitmap.Width;x++){ var c=bitmap.GetPixel(x,y); if(c.R>200 && c.G<80 && c.B<80) return true; }
            return false;
        }
        if(!DrawsRed(-1,"mode-unset.png"))throw new Exception("Unset mode box did not draw the red '未設定' text");
        if(DrawsRed(2,"mode-selected.png"))throw new Exception("Selected mode must not be drawn in red");
        Console.WriteLine("PASS unset mode box shows red text, selected mode does not");
        var standPanel=(Control)Field("_calibrationPanel");
        type.GetMethod("UpdateCalibrationControls",Private)!.Invoke(form,[true]);
        // 非表示の親Formに影響されないよう、Visibleのローカル状態を調べる。
        var getState=typeof(Control).GetMethod("GetState",Private)!;
        bool StandVisible()=>(bool)getState.Invoke(standPanel,[2])!;
        if(StandVisible())throw new Exception("Calibration panel appeared before mode selection");
        var productionPanel=(Control)Field("_productionPanel");
        var rootBox=(Control)Field("_rootBox");
        bool LocalVisible(Control control)=>(bool)getState.Invoke(control,[2])!;
        if(LocalVisible(productionPanel))throw new Exception("Operation panel appeared before mode selection");
        // 保存先は起動のたびに空から始める(転送設定に残っている前回の保存先は入れない。2026-11-06、人の指摘)。
        if(((TextBox)Field("_rootBox")).Text.Length!=0)throw new Exception("Save root must start empty instead of the previous save root");
        foreach(int mode in new[]{0,1,2})
        {
            modeBox.SelectedIndex=mode;
            Call("ChangeTestMode");
            type.GetMethod("UpdateCalibrationControls",Private)!.Invoke(form,[true]);
            if(!(bool)Field("_modeSelected") || !StandVisible())
                throw new Exception("Calibration panel did not appear after mode selection: "+mode);
            if(!LocalVisible(productionPanel))throw new Exception("Operation panel did not appear after mode selection: "+mode);
            // 保存先・S/N はデータ取り(2)だけに表示する。
            if(LocalVisible(rootBox)!=(mode==2) || LocalVisible((Control)Field("_serialBox"))!=(mode==2))
                throw new Exception("Save root / serial visibility did not follow mode: "+mode);
        }
        Console.WriteLine("PASS startup waits for mode; all three modes reveal the calibration panel; save root and serial only in production");
        // データ取りは保存先を選んでから校正へ進む(保存先 → 校正 → 型式 → S/N。2026-11-06、人の指摘)。
        // 「保存先選択」で選ぶまで校正記録を書かず、選んだ時点でその保存先に書く。データ調整・動き検査は保存先が無いので校正へ進める。
        var saveRootReady=type.GetProperty("SaveRootReady",Private)!;
        if((bool)Field("_saveRootChosen") || (bool)saveRootReady.GetValue(form)!)throw new Exception("Production must wait for the save root before calibration");
        modeBox.SelectedIndex=1;Call("ChangeTestMode");
        if(!(bool)saveRootReady.GetValue(form)!)throw new Exception("Adjustment has no save root and must proceed to calibration");
        modeBox.SelectedIndex=2;Call("ChangeTestMode");
        var calibrationType=assembly.GetType("PSCAutomation.Apps.AS110AllCheck.As110Calibration")!;
        object calibration=Activator.CreateInstance(calibrationType,new PSCAutomation.Wrappers.LoggerFiles.ScalingPoints(-0.002f,0.01f,0.993f,30f),new PSCAutomation.Wrappers.LoggerFiles.ScalingPoints(-0.001f,0f,5.524f,540f))!;
        object record=Activator.CreateInstance(assembly.GetType("PSCAutomation.Apps.AS110AllCheck.CalibrationRecord")!,DateTimeOffset.Now,calibration,"データ取り","NR-500","test.xdt")!;
        var recordField=type.GetField("_appliedCalibrationRecord",Private)!;var recordedRootField=type.GetField("_calibrationRecordedRoot",Private)!;
        var transferSettings=Field("_transferSettings");var saveRootProperty=transferSettings.GetType().GetProperty("SaveRoot")!;
        string originalSaveRoot=(string)saveRootProperty.GetValue(transferSettings)!;
        string chosenRoot=Path.Combine(Path.GetTempPath(),"AS110AllCheck-choose-"+Guid.NewGuid().ToString("N"),"SN1-10");
        Directory.CreateDirectory(chosenRoot);
        try
        {
            recordField.SetValue(form,record);
            ((TextBox)Field("_rootBox")).Text=chosenRoot;Call("WriteCalibrationRecordIfReady");
            if(Directory.Exists(Path.Combine(chosenRoot,"校正記録")))throw new Exception("Calibration record must not be written before the save root is chosen in this run");
            ((TextBox)Field("_rootBox")).Text="";
            if(!(bool)type.GetMethod("SelectSaveRoot",Private)!.Invoke(form,[chosenRoot])!)throw new Exception("Selecting a writable save root must be accepted");
            if(!(bool)Field("_saveRootChosen") || !(bool)saveRootReady.GetValue(form)! || rootBox.Text!=chosenRoot)throw new Exception("Selecting the save root must mark it chosen and allow calibration");
            if(Directory.GetFiles(Path.Combine(chosenRoot,"校正記録"),"校正_*.txt").Length!=1)throw new Exception("Selecting the save root must write the calibration record there");
            if(((TextBox)Field("_lastSerialBox")).Text!="10")throw new Exception("Selecting the save root must fill the range from the folder name");
            Console.WriteLine("PASS production waits for the save root before calibration; the calibration record is written when the save root is chosen");
        }
        finally
        {
            recordField.SetValue(form,null);recordedRootField.SetValue(form,null);type.GetField("_saveRootChosen",Private)!.SetValue(form,false);
            saveRootProperty.SetValue(transferSettings,originalSaveRoot);transferSettings.GetType().GetMethod("Save")!.Invoke(transferSettings,null);
            ((TextBox)Field("_rootBox")).Text="";Call("ApplySaveRootRange");Call("UpdateControls");
            try{Directory.Delete(Path.GetDirectoryName(chosenRoot)!,true);}catch(IOException){}
        }
        // 「次にすること」: データ調整ボタンが押せない間は、理由と次の操作を赤字で出す(機器なしなので未接続の案内になる)。押せない理由が空のままボタンが押せない状態を作らない。
        var nextStep=(Label)Field("_nextStepLabel");var startButton=(Button)Field("_startButton");
        string startBlockReason=(string)type.GetMethod("GetStartBlockReason",Private)!.Invoke(form,null)!;
        if(startButton.Enabled || startBlockReason.Length==0)throw new Exception("Start must be blocked with a reason when devices are missing: "+startBlockReason);
        if(!nextStep.Text.StartsWith("次にすること：") || !nextStep.Text.Contains("未接続") || !nextStep.Text.Contains("「確認」") || nextStep.ForeColor!=Color.Red)throw new Exception("Next-step guide must explain the missing devices in red: "+nextStep.Text);
        if(nextStep.Parent!=startButton.Parent || nextStep.Top<=startButton.Top)throw new Exception("Next-step guide must be under the top button row");
        Console.WriteLine("PASS next-step guide shows the red block reason under the buttons: "+nextStep.Text);
        // データ取りの操作欄の並び: 保存先 → 開始S/N・最後S/N(フォルダー名から) → 測定開始S/N → 保護解除・転送設定 → ボタン → 状態(型式の行は無い。参照波形で決まる)。最後に選んだモードはデータ取り(2)。
        _=form.Handle;form.PerformLayout();
        // 「設定変更（測定を破棄）」は操作欄ではなく校正欄の「校正値を変更」の右隣。
        var remeasure=(Control)Field("_remeasureButton");var calibrationButton=(Control)Field("_calibrationButton");
        if(remeasure.Parent!=calibrationButton.Parent || remeasure.Left<=calibrationButton.Left)throw new Exception("設定変更（測定を破棄） must sit right of 校正値を変更 in the calibration panel");
        if(productionPanel.Contains(remeasure))throw new Exception("設定変更（測定を破棄） must not be in the production panel");
        Control[] orderedRows=[(Control)Field("_savePathPanel"),(Control)Field("_rangePanel"),(Control)Field("_serialPanel"),(Control)Field("_protectionPanel"),(Control)Field("_actionPanel"),(Control)Field("_flowLabel")];
        for(int i=1;i<orderedRows.Length;i++)
            if(orderedRows[i].Top<=orderedRows[i-1].Top)throw new Exception($"Production rows out of order at {i}: {string.Join(",",orderedRows.Select(r=>r.Top))}");
        var serialBox=(TextBox)Field("_serialBox");var rangeStartBox=(TextBox)Field("_rangeStartBox");var lastSerialBox=(TextBox)Field("_lastSerialBox");
        if(rangeStartBox.Left>=lastSerialBox.Left)throw new Exception("Range start and last serial are not left to right");
        if(!rangeStartBox.ReadOnly || !lastSerialBox.ReadOnly || serialBox.ReadOnly)throw new Exception("Range boxes must be read-only and the measurement serial editable");
        if(rangeStartBox.Font.Size>=serialBox.Font.Size)throw new Exception("Range boxes should be smaller than the measurement serial box");
        // 保存先フォルダー名「SN開始-最後」から開始S/N・最後S/N を緑字で入れ(選び直すたびに入れ替え)、測定開始S/N が範囲外なら保存先の行の下に赤字を出す。
        // 測定開始S/N は人が入れる。空のあいだは欄の右に赤字「入力してください」。
        var warning=(Label)Field("_saveRootWarningLabel");var missing=(Label)Field("_serialMissingLabel");
        string rangeRoot=Path.Combine(Path.GetTempPath(),"AS110AllCheck-range-"+Guid.NewGuid().ToString("N"),"SN71360-71460");
        Directory.CreateDirectory(rangeRoot);
        try
        {
            ((TextBox)Field("_rootBox")).Text="";serialBox.Text="";Call("ApplySaveRootRange");Call("UpdateControls");
            if(!LocalVisible(missing) || missing.Text!="入力してください" || missing.ForeColor!=Color.Red)throw new Exception("Empty measurement serial must show the red prompt");
            if(missing.Left<=serialBox.Left)throw new Exception("The red prompt must be to the right of the measurement serial box");
            ((TextBox)Field("_rootBox")).Text=rangeRoot;
            Call("ApplySaveRootRange");Call("UpdateControls");
            if(rangeStartBox.Text!="71360" || lastSerialBox.Text!="71460")throw new Exception($"Save root range was not applied: {rangeStartBox.Text}/{lastSerialBox.Text}");
            if(rangeStartBox.ForeColor!=Color.Green || lastSerialBox.ForeColor!=Color.Green)throw new Exception("Range filled from the folder must be green");
            if(serialBox.Text.Length!=0 || !LocalVisible(missing))throw new Exception("Selecting a folder must not fill the measurement serial");
            if(!LocalVisible(warning) || !warning.Text.Contains("測定開始S/N が空です"))throw new Exception("Empty measurement serial must be reported against the folder: "+warning.Text);
            serialBox.Text="71500";Call("UpdateControls");
            if(LocalVisible(missing))throw new Exception("Red prompt remained after typing a serial");
            if(!LocalVisible(warning) || !warning.Text.Contains("範囲 71360〜71460 の外") || warning.ForeColor!=Color.Red)throw new Exception("Range warning not shown in red for a serial outside the folder: "+warning.Text);
            if(warning.Top<=((Control)Field("_savePathPanel")).Top || warning.Top>=((Control)Field("_rangePanel")).Top)throw new Exception("Range warning is not between the save root row and the range row");
            serialBox.Text="71400";Call("UpdateControls");
            if(LocalVisible(warning))throw new Exception("Range warning remained for a serial inside the folder: "+warning.Text);
            string plainRoot=Path.Combine(Path.GetDirectoryName(rangeRoot)!,"測定データ");Directory.CreateDirectory(plainRoot);
            ((TextBox)Field("_rootBox")).Text=plainRoot;Call("ApplySaveRootRange");Call("UpdateControls");
            if(rangeStartBox.Text.Length!=0 || lastSerialBox.Text.Length!=0 || rangeStartBox.ForeColor==Color.Green)throw new Exception("Folder without a range must clear the range boxes");
            if(!warning.Text.Contains("SN開始-最後 がありません"))throw new Exception("Folder without a range not reported: "+warning.Text);
            Console.WriteLine("PASS save root fills start/last serial in green, measurement serial is typed, red prompt when empty, red warning when outside");
        }
        finally { ((TextBox)Field("_rootBox")).Text="";serialBox.Text="";Call("ApplySaveRootRange");Call("UpdateControls");try{Directory.Delete(Path.GetDirectoryName(rangeRoot)!,true);}catch(IOException){} }
        // 完了済み(保護中)の S/N を測定開始S/N に入れたときは、押してからエラーにせず、データ調整ボタンを押せなくして「次にすること」で知らせる。
        string completedRoot=Path.Combine(Path.GetTempPath(),"AS110AllCheck-complete-"+Guid.NewGuid().ToString("N"),"SN1-100");
        Directory.CreateDirectory(completedRoot);
        try
        {
            var storeType=assembly.GetType("PSCAutomation.Apps.AS110AllCheck.CompletionStore")!;
            object completionStore=Activator.CreateInstance(storeType,completedRoot)!;
            using(var held=(IDisposable)storeType.GetMethod("Lock")!.Invoke(completionStore,null)!)
                storeType.GetMethod("Append")!.Invoke(completionStore,["COMPLETE","2","AS110-035","x"]);
            var configure=type.GetMethod("ConfigureProduct",Private)!;
            ((TextBox)Field("_rootBox")).Text=completedRoot;Call("ApplySaveRootRange");
            serialBox.Text="2";configure.Invoke(form,[false]);Call("UpdateControls");
            if(!(bool)Field("_serialCompleted"))throw new Exception("Completed serial was not detected when typed");
            // 機器なしなので「次にすること」は未接続の案内が先に出る。ここではボタンが押せないことと、完了済みの判定だけを確かめる(文言は GetStartBlockReason の順で接続後に出る)。
            if(((Button)Field("_startButton")).Enabled)throw new Exception("Completed serial must disable start");
            serialBox.Text="3";configure.Invoke(form,[false]);Call("UpdateControls");
            if((bool)Field("_serialCompleted"))throw new Exception("Non-completed serial must clear the completed state");
            Console.WriteLine("PASS completed serial is detected when typed and blocks start; another serial clears it");
        }
        finally { ((TextBox)Field("_rootBox")).Text="";serialBox.Text="";Call("ApplySaveRootRange");Call("UpdateControls");try{Directory.Delete(Path.GetDirectoryName(completedRoot)!,true);}catch(IOException){} }
        if(((Control)Field("_transferButton")).Left>=((Control)Field("_saveButton")).Left)throw new Exception("Excel transfer button is not first in the action row");
        string productionImage=Path.Combine(AppContext.BaseDirectory,"production-panel.png");
        // 非表示フォームの子はハンドルが無く描かれないので、先に作る。
        static void CreateHandles(Control control){ _=control.Handle; foreach(Control child in control.Controls) CreateHandles(child); }
        CreateHandles(productionPanel);form.PerformLayout();
        using(var bitmap=new Bitmap(Math.Max(productionPanel.Width,1),Math.Max(productionPanel.Height,1)))
        {
            productionPanel.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));
            bitmap.Save(productionImage);
        }
        // フォーム全体(データ取り選択後)の画像。操作欄がログ欄の上に収まっているかを目で確かめる用。
        string formImage=Path.Combine(AppContext.BaseDirectory,"main-form.png");
        CreateHandles(form);form.PerformLayout();
        using(var bitmap=new Bitmap(form.Width,form.Height)){ form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size)); bitmap.Save(formImage); }
        Console.WriteLine("PASS production rows are save root, range, measurement serial, release, buttons, status; layout images: "+productionImage+" / "+formImage);
        var flow=Field("_flow");
        var flowType=flow.GetType();
        flowType.GetProperty("Mode")!.SetValue(flow,Enum.Parse(assembly.GetType("PSCAutomation.Apps.AS110AllCheck.TestMode")!,"Production"));
        object product=((Array)assembly.GetType("PSCAutomation.Apps.AS110AllCheck.ProductModel")!.GetField("All")!.GetValue(null)!).GetValue(0)!;
        flowType.GetMethod("Configure")!.Invoke(flow,["123",product]);
        flowType.GetMethod("BeginMeasurement")!.Invoke(flow,[DateTime.Now]);
        flowType.GetMethod("CaptureSucceeded")!.Invoke(flow,null);
        flowType.GetMethod("TransferSucceeded")!.Invoke(flow,null);
        // 準備画面を前に出しても最前面には固定しない(2026-11-11、データ調整でアプリの画面がLOGGERより前に出たため固定をやめた)。
        _=form.Handle;
        Call("KeepPreparationInFront");
        if(form.TopMost)throw new Exception("Preparation screen must not stay topmost");
        Console.WriteLine("PASS preparation screen is raised without staying topmost");
        if (!args.Contains("--no-dialogs")) JudgmentNoticeTests.Run(form, type, flow, flowType, assembly);
        if (args.Contains("--with-excel")) ExcelJudgmentFocusTests.Run(form, type);
        if (args.Contains("--with-excel")) ExcelPreparationTests.Run(assembly);
        // 測定開始S/N が空のときは SerialMissing で測定開始を止め、状態欄に入力の案内を出す。
        var serialMissing=flowType.GetProperty("SerialMissing")!;
        flowType.GetMethod("ConfigureWithoutSerial")!.Invoke(flow,[product]);Call("UpdateProductionControls");
        if(!(bool)serialMissing.GetValue(flow)! || !((Label)Field("_flowLabel")).Text.Contains("測定開始S/Nを入力してください") || ((Button)Field("_startButton")).Enabled)throw new Exception("Missing serial must block measurement and prompt for input: "+((Label)Field("_flowLabel")).Text);
        // S/N が空のまま測定結果が残っていても(データ調整の結果のままデータ取りへ移ったとき)、測定開始S/N の欄は入力できる。S/N が入った結果が残っているときは変えられない(「設定変更（測定を破棄）」)。
        flowType.GetMethod("BeginMeasurement")!.Invoke(flow,[DateTime.Now]);flowType.GetMethod("CaptureSucceeded")!.Invoke(flow,null);Call("UpdateProductionControls");
        if(!(bool)flowType.GetProperty("HasCapture")!.GetValue(flow)! || !serialBox.Enabled)throw new Exception("Measurement serial box must stay editable while the serial is empty, even with a capture");
        // S/N が空の測定結果は、転送して判定が OK でも「保存」を押せない(2026-11-13、全体見直し。以前は押せて、保存先なしだと保存失敗が残り、保存先選択も S/N 欄も使えなくなった)。
        flowType.GetMethod("TransferSucceeded")!.Invoke(flow,null);type.GetField("_lastExcelPass",Private)!.SetValue(form,true);Call("UpdateProductionControls");
        string missingSerialSaveReason=(string)type.GetMethod("GetSaveBlockReason",Private)!.Invoke(form,null)!;
        if(((Button)Field("_saveButton")).Enabled || !missingSerialSaveReason.Contains("S/N") || !serialBox.Enabled)throw new Exception("A capture without a serial must not enable save, and the serial box must stay editable: "+missingSerialSaveReason);
        type.GetField("_lastExcelPass",Private)!.SetValue(form,false);
        Console.WriteLine("PASS a transferred capture without a serial cannot be saved: "+missingSerialSaveReason);
        flowType.GetMethod("Configure")!.Invoke(flow,["123",product]);flowType.GetMethod("BeginMeasurement")!.Invoke(flow,[DateTime.Now]);flowType.GetMethod("CaptureSucceeded")!.Invoke(flow,null);Call("UpdateProductionControls");
        if(serialBox.Enabled)throw new Exception("Measurement serial box must be locked while a capture with a serial exists");
        Console.WriteLine("PASS measurement serial is editable while empty even with a capture, locked once a capture has a serial");
        // 測定中の番号(データ取りで測定結果が残っている S/N)は「完了番号の保護と解除」で保護できない。先頭の 0 は無視して同じ番号とみなす。測定結果が無くなれば保護できる。
        var underMeasurement=type.GetMethod("IsSerialUnderMeasurement",Private)!;
        if(!(bool)underMeasurement.Invoke(form,["123"])! || !(bool)underMeasurement.Invoke(form,["0123"])! || (bool)underMeasurement.Invoke(form,["124"])!)throw new Exception("Serial under measurement must be detected (ignoring leading zeros) and other serials must not");
        // 測定を始めたあとで完了済みになった番号は「保存」を押せず、理由に完了済みと出す(保存失敗の状態にはしない)。
        flowType.GetMethod("TransferSucceeded")!.Invoke(flow,null);
        var serialCompletedField=type.GetField("_serialCompleted",Private)!;
        serialCompletedField.SetValue(form,true);Call("UpdateProductionControls");
        string saveReason=(string)type.GetMethod("GetSaveBlockReason",Private)!.Invoke(form,null)!;
        if(!saveReason.Contains("完了済み") || ((Button)Field("_saveButton")).Enabled || (bool)flowType.GetProperty("SaveFailed")!.GetValue(flow)!)throw new Exception("Completed serial with a capture must block save with the reason, not as a save failure: "+saveReason);
        serialCompletedField.SetValue(form,false);
        flowType.GetMethod("Invalidate")!.Invoke(flow,null);Call("UpdateProductionControls");
        if((bool)underMeasurement.Invoke(form,["123"])!)throw new Exception("Serial must not count as under measurement once the capture is discarded");
        Console.WriteLine("PASS serial under measurement cannot be protected; a completed serial with a capture blocks save with the reason");
        flowType.GetMethod("Configure")!.Invoke(flow,["123",product]);
        flowType.GetMethod("BeginMeasurement")!.Invoke(flow,[DateTime.Now]);
        flowType.GetMethod("CaptureSucceeded")!.Invoke(flow,null);
        flowType.GetMethod("TransferSucceeded")!.Invoke(flow,null);
        if((bool)serialMissing.GetValue(flow)!)throw new Exception("Serial set must clear SerialMissing");
        Console.WriteLine("PASS missing measurement serial blocks start and prompts");
        // 最後 S/N: 空は上限なし、開始 S/N が最後 S/N を超えたら範囲終了と判定し、状態欄に案内を出す。
        var rangeFinished=flowType.GetProperty("SerialRangeFinished")!;
        var setLast=flowType.GetMethod("SetLastSerial")!;
        var flowLabel=(Label)Field("_flowLabel");
        // 起動時に読んだ転送設定の保存先に「SN開始-最後」が入っていると最後 S/N が残るので、ここで空にしてから確かめる(PC の設定に依らないようにする)。
        setLast.Invoke(flow,[""]);
        if((bool)rangeFinished.GetValue(flow)!)throw new Exception("Empty last serial must not finish the range");
        setLast.Invoke(flow,["123"]);Call("UpdateProductionControls");
        if((bool)rangeFinished.GetValue(flow)! || !flowLabel.Text.Contains("最後 123"))throw new Exception("Serial equal to last serial must stay in range: "+flowLabel.Text);
        setLast.Invoke(flow,["122"]);Call("UpdateProductionControls");
        if(!(bool)rangeFinished.GetValue(flow)!)throw new Exception("Serial beyond last serial must finish the range");
        try{setLast.Invoke(flow,["abc"]);throw new Exception("Non-numeric last serial was accepted");}catch(System.Reflection.TargetInvocationException ex) when(ex.InnerException is ArgumentException){}
        setLast.Invoke(flow,[""]);Call("UpdateProductionControls");
        if((bool)rangeFinished.GetValue(flow)!)throw new Exception("Clearing last serial must reopen the range");
        Console.WriteLine("PASS last serial: empty is unlimited, beyond last finishes the range");
        type.GetField("_lastExcelPass",Private)!.SetValue(form,true);
        Call("UpdateProductionControls");
        var save=(Button)Field("_saveButton");
        if(!save.Enabled)throw new Exception("Production + transferred + OK did not enable save");
        Console.WriteLine("PASS actual MainForm enables save after transfer + OK");
        type.GetField("_lastExcelPass",Private)!.SetValue(form,false);
        Call("UpdateProductionControls");
        if(save.Enabled)throw new Exception("NG enabled save");
        Console.WriteLine("PASS actual MainForm disables save for NG");
        void AssertSave(bool expected)
        {
            Call("UpdateProductionControls");
            string signature=(string)type.GetMethod("BuildBoardDisplaySignature",Private)!.Invoke(form,null)!;
            if(save.Enabled!=expected || (signature.Split(':')[1][2]=='1')!=expected)
                throw new Exception("Save button / M5Stack C mask mismatch: "+signature);
            Console.WriteLine("PASS actual MainForm and M5Stack C mask: "+signature);
        }
        // AS110-116B の供給圧力と、350 kPa の出荷データ(データ取り２)。
        ShippingDataFormTests.Run(form, type, assembly);
        // 合格で次の番号へ進んだあと、直前に確定した番号へ欄を戻しても確定される(2026-11-13、全体見直し。以前は「前に確定した文字と同じ」として飛ばし、
        // 欄は 100・状態欄と M5Stack は 101 のままになった)。次の番号へ進める所は合格のとき(CompleteProduct)と同じ操作をここで行う。
        var serialOf=flowType.GetProperty("Serial")!;
        serialBox.Text="100";Call("ApplySerialInput");
        if((string)serialOf.GetValue(flow)!!="100")throw new Exception("Typing a serial must configure it");
        flowType.GetMethod("Advance")!.Invoke(flow,null);
        type.GetField("_appliedSerialText",Private)!.SetValue(form,(string)serialOf.GetValue(flow)!);serialBox.Text=(string)serialOf.GetValue(flow)!;
        serialBox.Text="100";Call("ApplySerialInput");
        if((string)serialOf.GetValue(flow)!!="100")throw new Exception("Typing the previous serial again after advancing must configure it: "+serialOf.GetValue(flow));
        // 次へ進んだときの覚えが古いまま(以前の合格の動き)でも確定される。
        flowType.GetMethod("Advance")!.Invoke(flow,null);serialBox.Text=(string)serialOf.GetValue(flow)!;
        serialBox.Text="100";Call("ApplySerialInput");
        if((string)serialOf.GetValue(flow)!!="100" || !((Label)Field("_flowLabel")).Text.Contains("S/N 100"))throw new Exception("A stale applied-serial memory must not skip the input: "+((Label)Field("_flowLabel")).Text);
        serialBox.Text="";Call("ApplySerialInput");
        Console.WriteLine("PASS typing the previous serial again after advancing to the next one is applied");
        string[] fileArgs = args.Where(arg => !arg.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if(fileArgs.Length==2)
        {
            var excelType=assembly.GetType("PSCAutomation.Apps.AS110AllCheck.ExcelWorkSession")!;
            object settings=Activator.CreateInstance(assembly.GetType("PSCAutomation.Apps.AS110AllCheck.TransferSettings")!)!;
            object model=assembly.GetType("PSCAutomation.Apps.AS110AllCheck.ProductModel")!.GetField("All")!.GetValue(null) is Array models?models.GetValue(0)!:throw new Exception();
            string output=Path.Combine(Path.GetTempPath(),"as110-save-ui-"+Guid.NewGuid()+".xlsx");
            object? session=null;
            try
            {
                session=excelType.GetMethod("Open")!.Invoke(null,[Path.GetFullPath(fileArgs[0]),output,Path.GetFullPath(fileArgs[1]),settings,model,"123",DateTime.Now,null,null])!;
                type.GetField("_excel",Private)!.SetValue(form,session);
                // ExcelWorkSession はブックを Wrappers/Excel の ExcelWorkbook で持つ。セルは中の COM で直接書く。
                object RawBook(){object workbook=excelType.GetField("_book",Private)!.GetValue(session)!;return workbook.GetType().GetField("_book",Private)!.GetValue(workbook)!;}
                dynamic originalBook=RawBook();
                dynamic originalSheet=originalBook.Worksheets["sheet1"];
                if(!((string)originalSheet.Range["E21"].Formula).StartsWith("=IF("))throw new Exception("Missing original judgment formula");
                originalSheet.Range["D10"].Value2=250;
                originalSheet.Range["D13"].Value2=390;
                originalSheet.Range["D16"].Value2=470;
                originalSheet.Range["L11"].Value2=3;
                originalSheet.Range["N11"].Value2=3;
                ((Task<bool>)excelType.GetMethod("IsPassAsync")!.Invoke(session,null)!).GetAwaiter().GetResult();
                Call("RefreshExcelJudgment");
                AssertSave(true);
                Console.WriteLine("PASS original template judgment formulas -> actual MainForm -> enabled M5Stack C");
                foreach(string judgment in new[]{"OK","NG","","OK"})
                {
                    dynamic book=RawBook();
                    dynamic sheet=book.Worksheets["sheet1"];
                    sheet.Range["E21"].Value2="OK";
                    sheet.Range["L21"].Value2=judgment;
                    Call("RefreshExcelJudgment");
                    AssertSave(judgment=="OK");
                }
                type.GetField("_operationInProgress",Private)!.SetValue(form,true);
                AssertSave(false);
                type.GetField("_operationInProgress",Private)!.SetValue(form,false);
                AssertSave(true);
                flowType.GetMethod("TransferFailed")!.Invoke(flow,null);
                AssertSave(false);
                string logBefore=((TextBox)Field("_logTextBox")).Text;
                Call("ShowTransferredExcel");
                if(((TextBox)Field("_logTextBox")).Text!=logBefore)
                    throw new Exception("Invalidated transfer attempted to show Excel");
                Console.WriteLine("PASS invalidated transfer does not bring stale Excel forward");
                Console.WriteLine("PASS real Excel -> actual MainForm -> M5Stack mask, including busy and invalidated transfer");
            }
            finally
            {
                (Field("_saveNotice") as Form)?.Close();
                (session as IDisposable)?.Dispose();
                if(File.Exists(output))File.Delete(output);
            }
        }
        type.GetField("_reviewStage",Private)!.SetValue(form,true);
        var outputButton=(Button)Field("_leakOutputButton");
        var leakButton=(Button)Field("_leakButton");
        var completeButton=(Button)Field("_completeButton");
        foreach(string mask in new[]{"100","010","001","101","000"})
        {
            outputButton.Enabled=mask[0]=='1';
            leakButton.Enabled=mask[1]=='1';
            completeButton.Enabled=mask[2]=='1';
            string signature=(string)type.GetMethod("BuildBoardDisplaySignature",Private)!.Invoke(form,null)!;
            string[] fields=signature.Split(':');
            if(fields[1]!=mask || fields[3]!="+30mA" || fields[4]!="漏れ試験実施" || fields[5]!="合格")
                throw new Exception("Saved-stage M5Stack A/B/C mapping mismatch: "+signature);
        }
        Console.WriteLine("PASS saved-stage M5Stack labels and enabled masks map to output / leak / complete");
        ((System.Windows.Forms.Timer)Field("_statusHeartbeat")).Stop();
        ((IDisposable)Field("_arduino")).Dispose();
        Call("StopPcDelayObservation");
    }
}
