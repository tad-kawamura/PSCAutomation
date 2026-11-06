using System.Reflection;
using System.Runtime.InteropServices;

// Excelの編集中・応答待ちを模擬し、制御参照を失って別Excelを起動する経路を検証する。
internal static class SessionOwnershipTests
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    public static void Run(Assembly assembly,Type type,Type modelType,string template,string captured)
    {
        foreach(int error in new[]{unchecked((int)0x80010001),unchecked((int)0x8001010A),unchecked((int)0x800AC472),unchecked((int)0x80004005)})
        {
            object session=Activator.CreateInstance(type,nonPublic:true)!;
            var app=new BusyExcel { Error=error };
            SessionAccess.Attach(session,app,app.Workbooks,app.Workbooks.Book);
            bool rejected=false;
            try { type.GetMethod("PrepareForTransfer")!.Invoke(session,null); }
            catch(TargetInvocationException ex) when(ex.InnerException is COMException com && com.ErrorCode==error) { rejected=true; }
            if(!rejected || !ReferenceEquals(SessionAccess.RawApp(session),app) ||
                !ReferenceEquals(SessionAccess.RawBook(session),app.Workbooks.Book) || app.CloseAttempts!=0)
                throw new Exception($"Busy Excel ownership was lost during preparation: 0x{error:X8}");
            // 準備後に応答不可になった場合も、Openのcatchで参照を捨てない。
            app.Error=0;app.CloseError=error;
            object settings=Activator.CreateInstance(assembly.GetType("PSCAutomation.Apps.AS110AllCheck.TransferSettings")!)!;
            object model=Activator.CreateInstance(modelType,"AS110-035",539)!;
            rejected=false;
            try { type.GetMethod("Open")!.Invoke(null,[template,"unused.xlsx",captured,settings,model,"-",DateTime.Now,session,null]); }
            catch(TargetInvocationException ex) when(ex.InnerException is COMException com && com.ErrorCode==error) { rejected=true; }
            if(!rejected || !ReferenceEquals(SessionAccess.RawApp(session),app) || app.CloseAttempts!=1)
                throw new Exception($"Busy Excel ownership was lost during transfer: 0x{error:X8}");
            app.CloseError=0;
            type.GetMethod("PrepareForTransfer")!.Invoke(session,null);
            if(!ReferenceEquals(SessionAccess.RawBook(session),app.Workbooks.Book))
                throw new Exception("Recovery failed to retain the original workbook");
            Console.WriteLine($"PASS rejected Excel call retains original application/book and recovers: 0x{error:X8}");
            ((IDisposable)session).Dispose();
        }
        // 0x800706BE は Wrappers/Excel が切断とみなすようにした(Excel が落ちた直後の最初の呼び出しで出る)。
        foreach(int error in new[]{0,unchecked((int)0x80010108),unchecked((int)0x800401FD),unchecked((int)0x800706BA),unchecked((int)0x800706BE)})
        {
            object session=Activator.CreateInstance(type,nonPublic:true)!;
            var app=new BusyExcel { Error=error };
            app.Workbooks.Present=false;
            SessionAccess.Attach(session,app,app.Workbooks,app.Workbooks.Book);
            type.GetMethod("PrepareForTransfer")!.Invoke(session,null);
            if(SessionAccess.Workbook(session) is not null || app.CloseAttempts!=0 ||
                (SessionAccess.Application(session) is null)!=(error!=0))
                throw new Exception("Closed book / disconnected server was not distinguished");
            ((IDisposable)session).Dispose();
            Console.WriteLine($"PASS closed workbook versus disconnected server: 0x{error:X8}");
        }
        // 合格確定の閉じ方(CloseVerifiedOrAlreadyClosed): ブックが開いていれば閉じて false。人がブックを閉じていれば閉じずに true(Excel は残す)。
        // Excel ごと閉じられて切断していれば閉じずに true で Excel の参照も返す。編集中・応答待ち(呼び出し拒否)は例外のまま(切断と区別する)。
        var closeOrGone=type.GetMethod("CloseVerifiedOrAlreadyClosed")!;
        {
            object session=Activator.CreateInstance(type,nonPublic:true)!;
            var app=new BusyExcel();
            SessionAccess.Attach(session,app,app.Workbooks,app.Workbooks.Book);
            bool gone=(bool)closeOrGone.Invoke(session,null)!;
            if(gone || app.CloseAttempts!=1 || SessionAccess.Workbook(session) is not null)throw new Exception("Open workbook must be closed normally at completion");
            ((IDisposable)session).Dispose();
        }
        foreach(int error in new[]{0,unchecked((int)0x80010108),unchecked((int)0x800706BA)})
        {
            object session=Activator.CreateInstance(type,nonPublic:true)!;
            var app=new BusyExcel { Error=error };
            app.Workbooks.Present=false;
            SessionAccess.Attach(session,app,app.Workbooks,app.Workbooks.Book);
            bool gone=(bool)closeOrGone.Invoke(session,null)!;
            if(!gone || app.CloseAttempts!=0 || SessionAccess.Workbook(session) is not null || (SessionAccess.Application(session) is null)!=(error!=0))
                throw new Exception($"Workbook closed by hand / Excel gone must count as closed at completion: 0x{error:X8}");
            ((IDisposable)session).Dispose();
        }
        {
            object session=Activator.CreateInstance(type,nonPublic:true)!;
            var app=new BusyExcel { Error=unchecked((int)0x80010001) };
            SessionAccess.Attach(session,app,app.Workbooks,app.Workbooks.Book);
            bool rejected=false;
            try { closeOrGone.Invoke(session,null); }
            catch(TargetInvocationException ex) when(ex.InnerException is COMException com && com.ErrorCode==unchecked((int)0x80010001)) { rejected=true; }
            if(!rejected || app.CloseAttempts!=0 || SessionAccess.Workbook(session) is null)throw new Exception("Busy Excel at completion must be reported, not treated as closed");
            ((IDisposable)session).Dispose();
        }
        Console.WriteLine("PASS completion closes an open workbook, treats a hand-closed workbook or gone Excel as closed, and reports busy Excel");
    }
}

public sealed class BusyExcel
{
    public int Error,CloseError,CloseAttempts;
    public BusyBooks Workbooks { get; }
    public BusyExcel()=>Workbooks=new BusyBooks(this);
    public void Quit() { }
}
public sealed class BusyBooks
{
    private readonly BusyExcel _app;
    public bool Present=true;
    public BusyBook Book { get; }
    public BusyBooks(BusyExcel app) { _app=app;Book=new BusyBook(app); }
    public int Count=>_app.Error==0 ? (Present ? 1 : 0) : throw new COMException("Excel busy",_app.Error);
    public BusyBook Item(int index)=>Book;
}
public sealed class BusyBook
{
    private readonly BusyExcel _app;
    public BusyBook(BusyExcel app)=>_app=app;
    public string Name=>_app.Error==0 ? "work.xlsx" : throw new COMException("Excel busy",_app.Error);
    public string FullName=>"work.xlsx";
    public void Close(bool save)
    {
        _app.CloseAttempts++;
        if(_app.Error!=0 || _app.CloseError!=0)throw new COMException("Excel busy",_app.Error!=0 ? _app.Error : _app.CloseError);
        // 閉じられたらブック一覧から消える(本物の Excel と同じ。閉じたことの確認に使う)。
        _app.Workbooks.Present=false;
    }
}
