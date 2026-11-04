using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static PSCAutomation.Wrappers.Excel.ExcelCom;

namespace PSCAutomation.Wrappers.Excel;

/// <summary>名前定義などで読んだ範囲。<see cref="Values"/> は 0 始まり([行, 列])。</summary>
public sealed record ExcelRangeValues(string Sheet, int Row, int Column, object?[,] Values);

/// <summary><see cref="ExcelApplication.Open"/> で開いたブック。</summary>
/// <remarks>元: AS110AllCheck ExcelWorkSession(保存・PDF・閉じたかの確認・前面表示)と AS110NoiseTest NoiseResultWorkbook(シート取得・名前定義・保存)。</remarks>
[SupportedOSPlatform("windows")]
public sealed class ExcelWorkbook : IDisposable
{
    private readonly ExcelApplication _application;
    private object? _book;

    internal ExcelWorkbook(ExcelApplication application, object book)
    {
        _application = application;
        _book = book;
        FullName = (string)((dynamic)book).FullName;
    }

    public ExcelApplication Application => _application;
    /// <summary>開いたときのフルパス。</summary>
    public string FullName { get; }
    /// <summary>閉じた(<see cref="Close"/>・Dispose・<see cref="IsStillOpen"/> で閉じられていると分かった)なら true。</summary>
    public bool IsClosed { get; private set; }

    private dynamic Book => !IsClosed && _book is not null ? _book : throw new InvalidOperationException("ブックは閉じられています。");

    public bool IsReadOnly => (bool)Book.ReadOnly;

    /// <summary>シート名の一覧(左から順)。</summary>
    public IReadOnlyList<string> SheetNames
    {
        get
        {
            var names = new List<string>();
            object? sheets = null;
            try
            {
                sheets = Book.Worksheets;
                int count = (int)((dynamic)sheets).Count;
                for (int i = 1; i <= count; i++)
                {
                    object? sheet = null;
                    try
                    {
                        sheet = ((dynamic)sheets)[i];
                        names.Add((string)((dynamic)sheet).Name);
                    }
                    finally { Release(sheet); }
                }
            }
            finally { Release(sheets); }
            return names;
        }
    }

    /// <summary>シートを名前で取る。無ければ <see cref="InvalidDataException"/>。使い終わったら Dispose する。</summary>
    public ExcelSheet Sheet(string name)
    {
        object? sheets = null;
        try
        {
            sheets = Book.Worksheets;
            object sheet;
            try { sheet = ((dynamic)sheets).Item(name); }
            catch (COMException) { throw new InvalidDataException($"シート「{name}」がありません。ブックの形式を確認してください。"); }
            return new ExcelSheet(_application, sheet);
        }
        finally { Release(sheets); }
    }

    /// <summary>名前定義の範囲と値を読む。無ければ <see cref="InvalidDataException"/>。</summary>
    public ExcelRangeValues ReadNamedRange(string name)
    {
        object? names = null, item = null, range = null, sheet = null;
        try
        {
            names = Book.Names;
            try { item = ((dynamic)names).Item(name); }
            catch (COMException) { throw new InvalidDataException($"名前定義「{name}」がありません。ブックの形式を確認してください。"); }
            range = ((dynamic)item).RefersToRange;
            sheet = ((dynamic)range).Worksheet;
            return new ExcelRangeValues(
                (string)((dynamic)sheet).Name,
                (int)((dynamic)range).Row,
                (int)((dynamic)range).Column,
                ToZeroBased((object?)((dynamic)range).Value2));
        }
        finally { Release(sheet); Release(range); Release(item); Release(names); }
    }

    /// <summary>このブックを Excel の中で前に出す。</summary>
    public void Activate() => Book.Activate();

    /// <summary>上書き保存する。</summary>
    public void Save() => Book.Save();

    /// <summary>開いているブックはそのままに、今の内容を別のファイルに保存する(元: AllCheck)。</summary>
    public void SaveCopyAs(string path) => Book.SaveCopyAs(Path.GetFullPath(path));

    /// <summary>
    /// 全シートを再計算する。<paramref name="firstSheet"/> を指定すると、そのシートを先に計算する
    /// (元: AllCheck。測定値のシートを先に計算してから判定のシートを計算する)。
    /// </summary>
    public void CalculateSheets(string? firstSheet = null)
    {
        object? sheets = null, first = null;
        try
        {
            sheets = Book.Worksheets;
            if (firstSheet is not null)
            {
                try { first = ((dynamic)sheets).Item(firstSheet); }
                catch (COMException) { throw new InvalidDataException($"シート「{firstSheet}」がありません。ブックの形式を確認してください。"); }
                ((dynamic)first).Calculate();
            }
            int count = (int)((dynamic)sheets).Count;
            for (int i = 1; i <= count; i++)
            {
                object? sheet = null;
                try
                {
                    sheet = ((dynamic)sheets)[i];
                    // 先に計算したシートは同じ参照なので、二度計算しない。
                    if (!ReferenceEquals(sheet, first)) ((dynamic)sheet).Calculate();
                }
                finally { Release(sheet); }
            }
        }
        finally { Release(first); Release(sheets); }
    }

    /// <summary>
    /// このブックがまだ Excel で開いているか(人が閉じた・Excel を終了したなら false にして、以後 <see cref="IsClosed"/> が true)。
    /// 閉じたブック自体には触らず、Excel のブック一覧で確かめる(元: AllCheck PrepareForTransfer)。
    /// Excel が編集中・応答待ちのときは例外がそのまま出る。
    /// </summary>
    public bool IsStillOpen()
    {
        if (IsClosed || _book is null) return false;
        try
        {
            dynamic books = _application.Books;
            int count = (int)books.Count;
            for (int i = 1; i <= count; i++)
            {
                object? other = null;
                try
                {
                    other = books.Item(i);
                    if (ReferenceEquals(other, _book)) return true;
                }
                // 自分のブックでも、ここで増えた参照の分だけ返す(_book は使えるまま)。
                finally { Release(other); }
            }
        }
        catch (Exception ex) when (IsDisconnected(ex)) { }
        catch (ObjectDisposedException) { }
        MarkClosed();
        return false;
    }

    /// <summary>
    /// ブックを閉じ、Excel のブック一覧から消えたことを確かめる。消えていなければ <see cref="IOException"/>(元: AllCheck CloseVerified)。
    /// </summary>
    public void Close(bool save = false)
    {
        if (IsClosed) return;
        Book.Close(save);
        dynamic books = _application.Books;
        int count = (int)books.Count;
        for (int i = 1; i <= count; i++)
        {
            object? other = null;
            try
            {
                other = books.Item(i);
                if (string.Equals((string)((dynamic)other).FullName, FullName, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("ブックが閉じられていません。");
            }
            finally { Release(other); }
        }
        MarkClosed();
    }

    /// <summary>
    /// Excel を画面に出し、このブック(と <paramref name="sheetName"/> のシート)を表示する。最小化されていれば元に戻す。
    /// 戻り値はブックの窓のハンドル。Windows の前面にまで出すには、アプリ側で
    /// <c>PSCAutomation.Wrappers.Common.WindowActivation.TryActivate</c> に渡す(元: AllCheck BringToFront)。
    /// </summary>
    public IntPtr ShowWindow(string? sheetName = null)
    {
        object? sheets = null, sheet = null, windows = null, window = null;
        try
        {
            _application.Visible = true;
            Book.Activate();
            if (sheetName is not null)
            {
                sheets = Book.Worksheets;
                try { sheet = ((dynamic)sheets).Item(sheetName); }
                catch (COMException) { throw new InvalidDataException($"シート「{sheetName}」がありません。ブックの形式を確認してください。"); }
                ((dynamic)sheet).Activate();
            }
            windows = Book.Windows;
            window = ((dynamic)windows)[1];
            ((dynamic)window).Visible = true;
            if ((int)((dynamic)window).WindowState == XlMinimized) ((dynamic)window).WindowState = XlNormal;
            ((dynamic)window).Activate();
            return new IntPtr((int)((dynamic)window).Hwnd);
        }
        finally { Release(window); Release(windows); Release(sheet); Release(sheets); }
    }

    private void MarkClosed()
    {
        IsClosed = true;
        Release(_book);
        _book = null;
        _application.Forget(this);
    }

    /// <summary>保存せずに閉じて参照を解放する。Excel を人に渡したあと(<see cref="ExcelApplication.HandOverToUser"/>)は閉じない。</summary>
    public void Dispose()
    {
        if (IsClosed) return;
        if (_book is not null && !_application.IsHandedOver)
        {
            try { ((dynamic)_book).Close(false); } catch { }
        }
        MarkClosed();
    }
}
