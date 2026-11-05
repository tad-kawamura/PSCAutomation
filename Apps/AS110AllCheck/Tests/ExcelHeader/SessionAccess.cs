using System.Collections;
using System.Reflection;

// ExcelWorkSession は Excel を Wrappers/Excel の部品(ExcelApplication・ExcelWorkbook)越しに持つ(移植前は COM の参照を直接持っていた)。
// 検査から中身を見るときと、検査用の Excel(本物・模擬)を持たせるときに使う。
internal static class SessionAccess
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;

    // ExcelWorkSession が持つ部品。同じブック・同じ Excel を使い続けているかの比較に使う。
    public static object? Workbook(object? session)=>Field(session,"_book");
    public static object? Application(object? session)=>Field(session,"_app");

    // 部品の中の Excel の COM オブジェクト。セルや図形を直接確かめるときに使う。
    public static object? RawBook(object? session)=>Field(Workbook(session),"_book");
    public static object? RawApp(object? session)=>Field(Application(session),"_app");

    // すでにある Excel(本物の COM、または模擬のオブジェクト)を部品で包み、ExcelWorkSession に持たせる。
    // books は Excel のブック一覧(閉じたかの確認に使う)。使わない検査では null でよい。
    public static void Attach(object session,object app,object? books,object book)
    {
        var type=session.GetType();
        var appType=type.GetField("_app",Private)!.FieldType;
        var bookType=type.GetField("_book",Private)!.FieldType;
        object wrapper=appType.GetConstructor(Private,[typeof(object)])!.Invoke([app]);
        appType.GetField("_books",Private)!.SetValue(wrapper,books);
        object workbook=bookType.GetConstructor(Private,[appType,typeof(object)])!.Invoke([wrapper,book]);
        // ExcelApplication.Open と同じく、後始末で閉じるブックの一覧に入れる。
        ((IList)appType.GetField("_workbooks",Private)!.GetValue(wrapper)!).Add(workbook);
        type.GetField("_app",Private)!.SetValue(session,wrapper);
        type.GetField("_book",Private)!.SetValue(session,workbook);
    }

    private static object? Field(object? target,string name)=>target?.GetType().GetField(name,Private)!.GetValue(target);
}
