using System.Diagnostics;
using System.Reflection;

// 専用Excelと一時ファイルのみ使用。機器・元の雛形・測定ファイルには書き込まない。
internal static class ReuseTests
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    public static void Run(Assembly assembly,Type type,Type modelType,string template,string captured)
    {
        string folder=Path.Combine(Path.GetTempPath(),"as110-reuse-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        object? session=null;
        object settings=Activator.CreateInstance(assembly.GetType("PSCAutomation.Apps.AS110AllCheck.TransferSettings")!)!;
        object model=Activator.CreateInstance(modelType,"AS110-035",539)!;
        string serial="-";
        var date=new DateTime(2026,10,30);
        var logs=new List<string>();
        bool lastPass=false;
        // 同じブック・同じ Excel かは部品(ExcelWorkbook・ExcelApplication)で比べ、セルや図形は中の COM で確かめる。
        object? Book()=>SessionAccess.Workbook(session);
        object? RawBook()=>SessionAccess.RawBook(session);
        object? App()=>SessionAccess.Application(session);
        double Transfer(string input,bool rebuild=false)
        {
            logs.Clear();
            var timer=Stopwatch.StartNew();
            if(session is not null)
            {
                if(rebuild)type.GetMethod("CloseVerified")!.Invoke(session,null);
                type.GetMethod("PrepareForTransfer")!.Invoke(session,null);
            }
            session=type.GetMethod("Open")!.Invoke(null,[template,Path.Combine(folder,"work.xlsx"),input,settings,model,serial,date,session,
                (Action<string>)(line=>{logs.Add(line);Console.WriteLine(line);})])!;
            // 再計算はアプリと同じ既存処理を使う。
            bool pass=((Task<bool>)type.GetMethod("IsPassAsync")!.Invoke(session,null)!).GetAwaiter().GetResult();
            lastPass=pass;
            double elapsed=timer.Elapsed.TotalMilliseconds;
            Console.WriteLine($"TRANSFER rebuild={rebuild} total={elapsed:F0}ms pass={pass}");
            return elapsed;
        }
        void Check(bool value,string message)
        {
            if(!value)throw new Exception(message);
            Console.WriteLine("PASS "+message);
        }
        string Header()
        {
            dynamic book=RawBook()!;
            return (string)book.Worksheets["圧縮5"].Shapes.Item("テキスト ボックス 3").TextFrame2.TextRange.Text;
        }
        try
        {
            // 実際に異なる波形を作り、2回目が古い値のままにならないことを検証。
            // xdt の読み取りはアプリの隣にある Wrappers/LoggerFiles の DLL(移植前はアプリの中の XdtConversion.XdtReader)。
            var reader=Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(assembly.Location)!,"PSCAutomation.Wrappers.LoggerFiles.dll"))
                .GetType("PSCAutomation.Wrappers.LoggerFiles.XdtReader")!;
            dynamic info=reader.GetMethod("Inspect")!.Invoke(null,[captured])!;
            int offset=info.DataOffset, count=info.Samples, channels=info.ChannelCount;
            var bytes=File.ReadAllBytes(captured);
            for(int i=0;i<count;i++)
                BitConverter.GetBytes(BitConverter.ToSingle(bytes,offset+4*i*channels)+0.123f).CopyTo(bytes,offset+4*i*channels);
            string changed=Path.Combine(folder,"changed.xdt");File.WriteAllBytes(changed,bytes);
            double first=Transfer(captured);
            Check(lastPass,"original waveform passes original judgment formulas");
            object original=Book()!,app=App()!;
            string header=Header();
            dynamic book=RawBook()!;
            dynamic sheet=book.Worksheets["sheet1"];
            string formula=(string)sheet.Range["E21"].Formula;
            double oldValue=(double)sheet.Range["A4"].Value2;
            double reuse=Transfer(changed);
            Check(!lastPass,"changed waveform recalculates to NG instead of retaining old OK");
            Check(ReferenceEquals(original,Book()),"same workbook reused after measurement preparation");
            Check(Header()==header && logs.Any(x=>x.Contains("ヘッダ変更なし")),"unchanged header skipped");
            book=RawBook()!;sheet=book.Worksheets["sheet1"];
            Check((string)sheet.Range["E21"].Formula==formula,"original judgment formula preserved");
            Check((double)sheet.Range["A4"].Value2!=oldValue,"changed waveform replaced previous values");
            dynamic parsed=reader.GetMethod("Read")!.Invoke(null,[changed,new[]{1,2}])!;
            var values=(Dictionary<int,double[]>)parsed.Values;
            var compress=assembly.GetType("PSCAutomation.Apps.AS110AllCheck.TransferSettings")!.GetMethod("Compress")!;
            foreach(var (channel,column) in new[]{(1,"A"),(2,"B")})
            {
                var expected=(double[])compress.Invoke(null,[values[channel],5])!;
                var actual=(Array)sheet.Range[$"{column}4:{column}{expected.Length+3}"].Value2;
                for(int i=0;i<expected.Length;i++)
                    if(Math.Abs(Convert.ToDouble(actual.GetValue(i+1,1))-expected[i])>1e-9)
                        throw new Exception($"Measurement mismatch {column}{i+4}");
            }
            Check(true,"all 2600 current/pressure cells match XDT conversion");
            serial="123";Transfer(captured);
            Check(ReferenceEquals(original,Book()) && Header().Contains("123"),"serial changes from adjustment dash to production number in same book");
            model=Activator.CreateInstance(modelType,"AS110-015",588)!;serial="0001234567";date=date.AddDays(1);
            Transfer(captured);
            Check(Header().Contains("AS110-015") && Header().Contains("588") && Header().Contains(serial) && Header().Contains("2026.10.31"),"changed model/serial/pressure/date updated");
            Check(ReferenceEquals(original,Book()),"model change with common template retains workbook");
            Check(ReferenceEquals(app,App()),"own Excel instance reused");
            // 人の書き換えを見つける(2026-11-14、全体見直し・区切り 5)。測定値の書き直しと見出しの書き換えは書き換えに数えない。
            // 規格値などを書き換えると 1 件として見つかり、同じ番号の測り直しではブックをそのまま使い、番号が変わると雛形から作り直す。
            System.Collections.IList Edits()=>(System.Collections.IList)type.GetMethod("FindEdits")!.Invoke(session,null)!;
            var editTimer=Stopwatch.StartNew();
            int untouched=Edits().Count;
            Console.WriteLine($"TIMING FindEdits={editTimer.Elapsed.TotalMilliseconds:F0}ms (sheets={((dynamic)RawBook()!).Worksheets.Count})");
            Check(untouched==0,"transfers and header updates are not reported as edits");
            book=RawBook()!;sheet=book.Worksheets["sheet1"];
            string editedFormula=(string)sheet.Range["E21"].Formula;
            sheet.Range["E21"].Value2="OK";sheet.Range["Z1"].Value2=5;
            var found=Edits();
            Check(found.Count==2 && found.Cast<object>().Select(e=>e.ToString()).SequenceEqual([$"sheet1 の Z1：（空） → 「5」",$"sheet1 の E21：「{editedFormula}」 → 「OK」"]),
                "an overwritten judgment formula and a typed cell are found: "+string.Join(" / ",found.Cast<object>()));
            var needs=type.GetMethod("NeedsApproval")!;
            Check((bool)needs.Invoke(session,[found])!,"edits need a password before saving");
            type.GetMethod("Approve")!.Invoke(session,[found]);
            Check(!(bool)needs.Invoke(session,[Edits()])!,"the same edits are not asked again after the password");
            sheet.Range["Z2"].Value2="x";
            Check((bool)needs.Invoke(session,[Edits()])!,"a further edit is asked again");
            object editedBook=Book()!;
            Transfer(changed);
            Check(ReferenceEquals(editedBook,Book()) && Edits().Count==3,"re-measuring the same serial keeps the edited workbook and its edits");
            serial="0001234568";Transfer(captured);
            Check(!ReferenceEquals(editedBook,Book()) && Edits().Count==0 && logs.Any(x=>x.Contains("雛形から作り直します")) && lastPass,
                "a new serial rebuilds the edited workbook from the template (edits are not carried to the next unit)");
            book=RawBook()!;sheet=book.Worksheets["sheet1"];
            Check((string)sheet.Range["E21"].Formula==editedFormula && sheet.Range["Z1"].Value2 is null,"the rebuilt workbook has the template formula again");
            original=Book()!;
            serial="0001234569";Transfer(captured);
            Check(ReferenceEquals(original,Book()),"a new serial without edits keeps reusing the workbook");
            var freshTimes=new List<double>();var reuseTimes=new List<double>();
            for(int i=0;i<3;i++)
            {
                freshTimes.Add(Transfer(captured,rebuild:true));
                reuseTimes.Add(Transfer(changed));
            }
            Console.WriteLine($"TIMING initial={first:F0}ms firstReuse={reuse:F0}ms warmedRebuildMedian={freshTimes.Order().ElementAt(1):F0}ms reuseMedian={reuseTimes.Order().ElementAt(1):F0}ms");
            // 転送先・点数変更は作り直し。旧データの残りを持ち越さない。
            original=Book()!;
            settings.GetType().GetProperty("Compression")!.SetValue(settings,10);
            Transfer(captured);
            Check(!ReferenceEquals(original,Book()),"compression/count change rebuilds book");
            book=RawBook()!;sheet=book.Worksheets["sheet1"];
            Check(sheet.Range["A654"].Value2 is null,"shorter transfer clears previous tail through template rebuild");
            original=Book()!;
            string otherTemplate=Path.Combine(folder,"other.xlsx");File.Copy(template,otherTemplate);
            template=otherTemplate;
            Transfer(captured);
            Check(!ReferenceEquals(original,Book()),"template change rebuilds book");
            original=Book()!;book=RawBook()!;book.Close(false);
            Transfer(captured);
            Check(!ReferenceEquals(original,Book()),"manually closed workbook recreated");
            // 測定領域に数式が追加された場合は安全に中断。
            book=RawBook()!;sheet=book.Worksheets["sheet1"];sheet.Range["A4"].Formula="=1";
            bool rejected=false;
            try { Transfer(captured); }
            catch(TargetInvocationException ex) when(ex.InnerException is InvalidDataException) { rejected=true; }
            Check(rejected,"formula in measurement range rejected");
        }
        finally
        {
            (session as IDisposable)?.Dispose();
            Directory.Delete(folder,true);
        }
    }
}
