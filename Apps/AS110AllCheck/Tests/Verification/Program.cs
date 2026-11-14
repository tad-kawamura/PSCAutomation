using System.IO.Compression;
using System.Xml.Linq;
using PSCAutomation.Apps.AS110AllCheck;
static void Check(bool b,string name){if(!b)throw new Exception(name);Console.WriteLine("PASS "+name);}
await LeakCheckTests.RunAsync();
CalibrationTests.Run();
CalibrationRecordTests.Run();
await AutoCalibrationTests.RunAsync();
await SupplyPressureTests.RunAsync();
ShippingDataTests.Run();
MeasurementProfileTests.Run();
SaveConflictTests.Run();
FileAndSettingsTests.Run();
FileAndSettingsTests.RunEditsAndShippingData();
FileAndSettingsTests.RunSessionFolders();
foreach (var isMotion in new[] { true, false })
{
    double oldFrequency = isMotion ? 0.2 : 0.04;
    var wf = As110MotionTest.GetSettings(isSdg1032X: false, isMotion: isMotion);
    wf.Validate();
    Check(wf.Waveform == GeneratorWaveform.Triangle && wf.FrequencyHz == oldFrequency &&
        wf.AmplitudeVpp == 10.2 && wf.OffsetVolts == 0 && wf.BurstCycles == 0.5 &&
        wf.StartPhaseDegrees == 0, "WF1973 uses half-cycle settings with explicit zero-degree phase");
    Check(Math.Abs(wf.BurstCycles / wf.FrequencyHz - (isMotion ? 2.5 : 12.5)) < 1e-10,
        "WF1973 burst duration is 2.5 or 12.5 seconds");
    var settings = As110MotionTest.GetSettings(isSdg1032X: true, isMotion: isMotion);
    settings.Validate();
    Check(settings.BurstCycles == 1 && settings.StartPhaseDegrees == 270, "one-cycle triangle starts at trough");
    Check(Math.Abs(settings.OffsetVolts - settings.AmplitudeVpp / 2) < 1e-10 &&
        Math.Abs(settings.OffsetVolts + settings.AmplitudeVpp / 2 - 5.1) < 1e-10, "triangle voltage range is 0 to 5.1 V");
    Check(Math.Abs(settings.BurstCycles / settings.FrequencyHz - 0.5 / oldFrequency) < 1e-10,
        "one-cycle burst preserves previous duration");
    Check(Math.Abs(2 * settings.AmplitudeVpp * settings.FrequencyHz - 2 * 10.2 * oldFrequency) < 1e-10,
        "one-cycle triangle preserves previous ramp slope");
}
var f=new ProductionWorkflow {Mode=TestMode.Production}; f.Configure("0074233",ProductModel.All[0]);
var adjustment=new ProductionWorkflow {Mode=TestMode.Adjustment};
adjustment.Configure("123",ProductModel.All[0]);
adjustment.ConfigureAdjustment(ProductModel.All[1]);
Check(adjustment.Serial=="" && adjustment.Model==ProductModel.All[1],"adjustment selects model without retaining production serial");
adjustment.BeginMeasurement(DateTime.Now);adjustment.CaptureSucceeded();
adjustment.ConfigureAdjustment(ProductModel.All[1]);
Check(adjustment.HasCapture && !adjustment.CanComplete,"adjustment setup preserves same-model capture without serial");
adjustment.Configure("456",ProductModel.All[1]);
Check(!adjustment.HasCapture && adjustment.Serial=="456","production serial setup invalidates adjustment capture");
f.BeginMeasurement(new DateTime(2026,10,19,23,59,59));Check(!f.HasCapture && !f.CanComplete,"measurement invalidates old results");
f.CaptureSucceeded();f.TransferSucceeded();f.SaveFailed=true;Check(!f.HasSaved&&!f.CanComplete,"partial save cannot complete");
f.SaveSucceeded();Check(!f.CanComplete,"saved without leak cannot complete");f.LeakSucceeded();Check(f.CanComplete,"saved plus leak can complete");
f.BeginMeasurement(new DateTime(2026,10,20));Check(!f.HasTransfer&&!f.HasSaved&&!f.CanComplete&&f.LeakChecks==0,"remeasure clears transfer save leak");
f.CaptureError();Check(f.CaptureFailed&&!f.Measuring,"capture failure allows retry");f.CaptureSucceeded();f.TransferSucceeded();f.SaveSucceeded();f.LeakSucceeded();f.Advance();Check(f.Serial=="0074234"&&!f.CanComplete,"completion increments preserving digits");
var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());var store=new CompletionStore(root);
using(store.Lock()){store.Append("COMPLETE","00012","AS110-035","a");Check(store.IsComplete("12"),"serial protection ignores model date and leading zeros");store.Append("RELEASE","12","-","-");Check(!store.IsComplete("00012")&&store.WasReleased("12"),"release persisted");}
var reopened=new CompletionStore(root);Check(reopened.WasReleased("12"),"journal survives restart");
using(store.Lock()){
    store.Append("COMPLETE","0100","AS110-035","c");store.Append("COMPLETE","7","AS110-035","d");
    Check(store.Entries().SequenceEqual([("7",true),("12",false),("100",true)]),"entries sort by number with protection state");
    var (released,reprotected)=store.SetProtection([("7",false),("12",true),("100",true)]);
    Check(released.SequenceEqual(["7"])&&reprotected.SequenceEqual(["12"]),"set protection releases unchecked and re-protects checked, skipping unchanged");
    Check(!store.IsComplete("7")&&store.IsComplete("12")&&store.IsComplete("100"),"protection state after set");
    Check(File.ReadAllLines(Path.Combine(root,"完了記録.txt")).Last().StartsWith("COMPLETE\t")&&File.ReadAllLines(Path.Combine(root,"完了記録.txt")).Last().EndsWith("\t12\t-\t再保護"),"re-protection is journaled as COMPLETE");
}
// 保存先フォルダー名「SN開始-最後」の解析と、S/N との照合。
Check(SaveFolder.ParseRange(Path.Combine(root,"SN71360-71460"))==("71360","71460"),"folder range parsed");
Check(SaveFolder.ParseRange(Path.Combine(root,"AS110-035 SN00010-00020 試作"))==("00010","00020"),"folder range parsed inside other words");
Check(SaveFolder.ParseRange(Path.Combine(root,"SN20-10")) is null && SaveFolder.ParseRange(Path.Combine(root,"測定データ")) is null && SaveFolder.ParseRange("") is null,"folder without a valid range has none");
string rangeRoot=Path.Combine(root,"SN71360-71460");
Check(SaveFolder.CheckRange(rangeRoot,"71360")=="" && SaveFolder.CheckRange(rangeRoot,"71460")=="","measurement serial inside range is accepted");
Check(SaveFolder.CheckRange(rangeRoot,"71500").Contains("範囲 71360〜71460 の外") && SaveFolder.CheckRange(rangeRoot,"71359").Contains("の外"),"measurement serial outside range is reported");
Check(SaveFolder.CheckRange(rangeRoot,"").Contains("測定開始S/N が空です") && SaveFolder.CheckRange(rangeRoot,"abc").Contains("数字ではありません"),"empty or non-numeric measurement serial is reported");
Check(SaveFolder.CheckRange(Path.Combine(root,"測定データ"),"1").Contains("SN開始-最後 がありません") && SaveFolder.CheckRange("","1")=="","folder without range is reported; no folder is not checked");
Check(SaveFolder.CheckRange(rangeRoot,"71500").EndsWith("保存先フォルダーを確認してください。"),"range problems end with the folder reminder");
// 保存先の走査: Excel/PDF/LOGGER のファイル名から S/N を拾い、完了記録に無いものを返す。形式外は件数だけ。
Directory.CreateDirectory(Path.Combine(rangeRoot,"Excel"));Directory.CreateDirectory(Path.Combine(rangeRoot,"LOGGER"));
File.WriteAllText(Path.Combine(rangeRoot,"Excel","AS110-035_SN71362_20261106_6500.xlsx"),"");
File.WriteAllText(Path.Combine(rangeRoot,"LOGGER","AS110-035_SN71362_20261106_6500.xdt"),"");
File.WriteAllText(Path.Combine(rangeRoot,"LOGGER","AS110-035_SN0071361_20261106_6500.ydt"),"");
File.WriteAllText(Path.Combine(rangeRoot,"Excel","メモ.xlsx"),"");
var scanStore=new CompletionStore(rangeRoot);
using(scanStore.Lock()){
    scanStore.Append("COMPLETE","71361","AS110-035","x");
    var (unrecorded,unreadable)=SaveFolder.FindUnrecorded(rangeRoot,scanStore.Read());
    Check(unrecorded.Count==1 && unrecorded[0]==("71362","AS110-035","AS110-035_SN71362_20261106_6500") && unreadable==1,"unrecorded data found once per serial, leading zeros ignored, unreadable counted");
    var (_,protectedNow)=scanStore.SetProtection([("71362",true,unrecorded[0].Model,unrecorded[0].BaseName)]);
    Check(protectedNow.SequenceEqual(["71362"]) && scanStore.IsComplete("71362") && File.ReadAllLines(Path.Combine(rangeRoot,"完了記録.txt")).Last().EndsWith("\t71362\tAS110-035\tAS110-035_SN71362_20261106_6500"),"unrecorded data protected with model and file name");
    Check(SaveFolder.FindUnrecorded(rangeRoot,scanStore.Read()).Unrecorded.Count==0,"nothing unrecorded after protecting");
}
using(var held=store.Lock()){bool locked=false;try{using var other=store.Lock();}catch(IOException){locked=true;}Check(locked,"shared store lock excludes second process");}
File.AppendAllText(Path.Combine(root,"完了記録.txt"),"COMPLETE\tbroken");bool failed=false;try{store.Read();}catch(InvalidDataException){failed=true;}Check(failed,"corrupt journal fails closed");Directory.Delete(root,true);
Check(TransferSettings.Compress([1,2,3,4,5,6,7,8,9,10],5).SequenceEqual([3.0,8.0]),"compression is block average");
if(args.Length>0){var data=XdtReader.Read(args[0],[1,2]);Check(data.Info.Samples==6500&&data.Info.Channels[1].Window==7,"fixture channels and moving average");Check(TransferSettings.Compress(data.Values[2],5).Length==1300,"fixture transfer 1300 points");}
Console.WriteLine("ALL CORE CHECKS PASSED");
var csvPath=Path.GetTempFileName();
var csvSettings=new TransferSettings {ExpectedSamples=10};
File.WriteAllText(csvPath,"WAVE LOGGER X\nTIME,CH1[mA],CH2[kPa]\n"+string.Join("\n",Enumerable.Range(0,10).Select(i=>$"{i*0.002},{i},{i*10}")));
var csvData=csvSettings.ReadNrxCsv(csvPath);Check(csvData.Current.Length==10 && csvData.Pressure[9]==90,"NR-X CSV header and values");
File.WriteAllText(csvPath,"TIME,a,b\n"+string.Join("\n",Enumerable.Range(0,10).Select(i=>$"{i},{i},{i*10}")));
failed=false;try{csvSettings.ReadNrxCsv(csvPath);}catch(InvalidDataException){failed=true;}Check(failed,"NR-X CSV unknown columns rejected");
csvSettings.NrxCurrentCsvColumn=2;csvSettings.NrxPressureCsvColumn=3;Check(csvSettings.ReadNrxCsv(csvPath).Current[9]==9,"NR-X explicit CSV columns");File.Delete(csvPath);

if(args.Length>1)
{
    using var fixture=ZipFile.OpenRead(args[1]);
    XDocument Load(string name) { using var stream=fixture.GetEntry(name)!.Open();return XDocument.Load(stream); }
    XNamespace ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    XNamespace rel="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    string id=(string)Load("xl/workbook.xml").Descendants(ns+"sheet").Single(x=>(string?)x.Attribute("name")=="sheet1").Attribute(rel+"id")!;
    string target=(string)Load("xl/_rels/workbook.xml.rels").Root!.Elements().Single(x=>(string?)x.Attribute("Id")==id).Attribute("Target")!;
    string sheetPath=new Uri(new Uri("http://xlsx/xl/workbook.xml"),target).AbsolutePath.TrimStart('/');
    var cells=Load(sheetPath).Descendants(ns+"c").Where(x=>x.Element(ns+"v")!=null).ToDictionary(x=>(string)x.Attribute("r")!,x=>(string)x.Element(ns+"v")!);
    var data=XdtReader.Read(args[0],[1,2]);var current=TransferSettings.Compress(data.Values[1],5);var pressure=TransferSettings.Compress(data.Values[2],5);
    double maxA=0,maxB=0;
    for(int i=0;i<1300;i++) { maxA=Math.Max(maxA,Math.Abs(current[i]-double.Parse(cells["A"+(i+4)],System.Globalization.CultureInfo.InvariantCulture))); maxB=Math.Max(maxB,Math.Abs(pressure[i]-double.Parse(cells["B"+(i+4)],System.Globalization.CultureInfo.InvariantCulture))); }
    Check(maxA<1e-12,"real Excel current values exact");Check(maxB<=0.000062,"real Excel pressure values within 0.000062 kPa");
    string written=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".xlsx");
    TemplateWriter.Save(args[1],written,[new("sheet1","A4",current),new("sheet1","B4",pressure)]);
    using(var output=ZipFile.OpenRead(written))
    {
        using var stream=output.GetEntry(sheetPath)!.Open();var xml=XDocument.Load(stream);
        var cell=xml.Descendants(ns+"c").Single(x=>(string?)x.Attribute("r")=="B1303");
        Check(double.Parse((string)cell.Element(ns+"v")!,System.Globalization.CultureInfo.InvariantCulture)==pressure[^1],"Excel transfer writes final sample");
        using var a=fixture.GetEntry("xl/drawings/drawing1.xml")!.Open();using var b=output.GetEntry("xl/drawings/drawing1.xml")!.Open();
        using var aa=new MemoryStream();using var bb=new MemoryStream();a.CopyTo(aa);b.CopyTo(bb);Check(aa.ToArray().SequenceEqual(bb.ToArray()),"Excel transfer preserves original drawing XML");
    }
    File.Delete(written);
    Console.WriteLine($"Fixture comparison: current={maxA:R} mA, pressure={maxB:R} kPa");
}
