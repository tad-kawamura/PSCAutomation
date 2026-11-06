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
        if (!args.Contains("--no-dialogs")) DialogForegroundTests.Run();
        var assembly=Assembly.Load("AS110AllCheck");
        CalibrationDialogTests.Run(assembly);
        var type=assembly.GetType("PSCAutomation.Apps.AS110AllCheck.MainForm")!;
        using var form=(Form)Activator.CreateInstance(type)!;
        object Field(string name)=>type.GetField(name,Private)!.GetValue(form)!;
        void Call(string name)=>type.GetMethod(name,Private)!.Invoke(form,null);
        var modeBox=(ComboBox)Field("_modeBox");
        if(modeBox.SelectedIndex!=-1 || (bool)Field("_modeSelected"))
            throw new Exception("Startup must wait for an explicit mode selection");
        var standPanel=(Control)Field("_standSelectionPanel");
        type.GetMethod("UpdateStandControls",Private)!.Invoke(form,[true]);
        // 非表示の親Formに影響されないよう、Visibleのローカル状態を調べる。
        var getState=typeof(Control).GetMethod("GetState",Private)!;
        bool StandVisible()=>(bool)getState.Invoke(standPanel,[2])!;
        if(StandVisible())throw new Exception("Stand selection appeared before mode selection");
        var productionPanel=(Control)Field("_productionPanel");
        var rootBox=(Control)Field("_rootBox");
        bool LocalVisible(Control control)=>(bool)getState.Invoke(control,[2])!;
        if(LocalVisible(productionPanel))throw new Exception("Operation panel appeared before mode selection");
        ((TextBox)Field("_rootBox")).Text="";
        foreach(int mode in new[]{0,1,2})
        {
            modeBox.SelectedIndex=mode;
            Call("ChangeTestMode");
            type.GetMethod("UpdateStandControls",Private)!.Invoke(form,[true]);
            if(!(bool)Field("_modeSelected") || !StandVisible())
                throw new Exception("Stand selection did not appear after mode selection: "+mode);
            if(!LocalVisible(productionPanel))throw new Exception("Operation panel did not appear after mode selection: "+mode);
            // 保存先・S/N はデータ取り(2)だけに表示する。
            if(LocalVisible(rootBox)!=(mode==2) || LocalVisible((Control)Field("_serialBox"))!=(mode==2))
                throw new Exception("Save root / serial visibility did not follow mode: "+mode);
        }
        Console.WriteLine("PASS startup waits for mode; all three modes reveal stand selection; save root and serial only in production");
        var flow=Field("_flow");
        var flowType=flow.GetType();
        flowType.GetProperty("Mode")!.SetValue(flow,Enum.Parse(assembly.GetType("PSCAutomation.Apps.AS110AllCheck.TestMode")!,"Production"));
        object product=((Array)assembly.GetType("PSCAutomation.Apps.AS110AllCheck.ProductModel")!.GetField("All")!.GetValue(null)!).GetValue(0)!;
        flowType.GetMethod("Configure")!.Invoke(flow,["123",product]);
        flowType.GetMethod("BeginMeasurement")!.Invoke(flow,[DateTime.Now]);
        flowType.GetMethod("CaptureSucceeded")!.Invoke(flow,null);
        flowType.GetMethod("TransferSucceeded")!.Invoke(flow,null);
        if (!args.Contains("--no-dialogs")) JudgmentNoticeTests.Run(form, type, flow, flowType, assembly);
        if (args.Contains("--with-excel")) ExcelJudgmentFocusTests.Run(form, type);
        if (args.Contains("--with-excel")) ExcelPreparationTests.Run(assembly);
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
