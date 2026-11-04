using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static PSCAutomation.Wrappers.Excel.ExcelCom;

namespace PSCAutomation.Wrappers.Excel;

/// <summary>後始末(Dispose)のときに Excel を終了する条件。</summary>
public enum ExcelQuitMode
{
    /// <summary>このアプリが開いたブックを閉じたあと、ほかにブックが開いていなければ終了する(元: AllCheck)。</summary>
    WhenNoWorkbooks,
    /// <summary>ほかにブックが開いていても終了する(元: NoiseTest)。</summary>
    Always,
}

/// <summary>
/// このアプリが起動した Excel 1 つを持つ。ほかの人が開いている Excel には接続しない。
/// </summary>
/// <remarks>
/// 元: AS110AllCheck Excel/ExcelWorkSession.cs と AS110NoiseTest Excel/NoiseResultWorkbook.cs の、起動・開く・終了・COM 解放の共通部分。
/// どのシート・セルに何を書くかはアプリ側で決める。呼び出しは同じスレッド(ふつうは画面のスレッド)から行う。
/// Excel は実行時に ProgID で探すので、ビルドに Excel は要らない。
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ExcelApplication : IDisposable
{
    private object? _app;
    private object? _books;
    private readonly List<ExcelWorkbook> _workbooks = new();
    private bool _disposed;

    private ExcelApplication(object app) => _app = app;

    /// <summary>Dispose で Excel を終了する条件。既定は「ほかにブックが無ければ終了」。</summary>
    public ExcelQuitMode QuitMode { get; set; } = ExcelQuitMode.WhenNoWorkbooks;

    /// <summary><see cref="HandOverToUser"/> を呼んだあとは true。Dispose でブックも Excel も閉じない。</summary>
    public bool IsHandedOver { get; private set; }

    /// <summary>新しい Excel を起動する。</summary>
    /// <param name="visible">画面に出すか(元: AllCheck は true、NoiseTest は既定 false)。</param>
    /// <param name="displayAlerts">Excel の確認ダイアログを出すか(元: AllCheck・XdtConversion は true、NoiseTest は false)。</param>
    /// <exception cref="InvalidOperationException">Excel がインストールされていない、または起動できない。</exception>
    public static ExcelApplication Start(bool visible = false, bool displayAlerts = false)
    {
        Type type = Type.GetTypeFromProgID("Excel.Application")
            ?? throw new InvalidOperationException("Excel がインストールされていません。");
        object app = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Excel を起動できません。");
        var result = new ExcelApplication(app) { QuitMode = ExcelQuitMode.Always };
        try
        {
            dynamic d = app;
            d.Visible = visible;
            d.DisplayAlerts = displayAlerts;
            result._books = d.Workbooks;
            result.QuitMode = ExcelQuitMode.WhenNoWorkbooks;
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private dynamic App => !_disposed && _app is not null ? _app : throw new ObjectDisposedException(nameof(ExcelApplication));
    internal dynamic Books => !_disposed && _books is not null ? _books : throw new ObjectDisposedException(nameof(ExcelApplication));

    public bool Visible { get => (bool)App.Visible; set => App.Visible = value; }
    public bool DisplayAlerts { get => (bool)App.DisplayAlerts; set => App.DisplayAlerts = value; }
    /// <summary>画面の描き直しをするか。まとめて書き込む間だけ false にすると速い。</summary>
    public bool ScreenUpdating { get => (bool)App.ScreenUpdating; set => App.ScreenUpdating = value; }

    /// <summary>この Excel で開いているブックの数(人が開いたものも含む)。</summary>
    public int WorkbookCount => (int)Books.Count;

    /// <summary>この Excel のプロセス ID。</summary>
    public int ProcessId => ProcessIdOf(App);

    /// <summary>計算方法を「自動」にする。</summary>
    public void SetAutomaticCalculation() => App.Calculation = XlCalculationAutomatic;

    /// <summary>開いているブックをすべて再計算する。</summary>
    public void Calculate() => App.Calculate();

    /// <summary>Excel が計算中か(再計算待ちは含めない)。</summary>
    public bool IsCalculating => (int)App.CalculationState == XlCalculating;

    /// <summary>計算が終わるまで待つ。<paramref name="timeout"/> を過ぎたら <see cref="TimeoutException"/>。</summary>
    public async Task WaitForCalculationAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        while (IsCalculating)
        {
            if (timer.Elapsed >= timeout)
                throw new TimeoutException($"Excel の計算が {timeout.TotalSeconds:0.#} 秒以内に終わりませんでした。Excel の状態を確認してください。");
            await Task.Delay(100, cancellationToken);
        }
    }

    /// <summary>ブックを開く。</summary>
    /// <param name="readOnly">読み取り専用で開くか。false なのに読み取り専用で開かれた(ほかで開いている、ファイルが読み取り専用)ときは閉じて <see cref="IOException"/>。</param>
    public ExcelWorkbook Open(string path, bool readOnly = false)
    {
        string full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("Excel ファイルが見つかりません。", full);
        object book = Books.Open(full, UpdateLinks: 0, ReadOnly: readOnly);
        var workbook = new ExcelWorkbook(this, book);
        _workbooks.Add(workbook);
        if (!readOnly && workbook.IsReadOnly)
        {
            workbook.Dispose();
            throw new IOException($"{full} は読み取り専用で開かれました。ほかで開いていないか、ファイルが読み取り専用になっていないか確認してください。");
        }
        return workbook;
    }

    /// <summary>
    /// Excel とまだつながっているか。人が Excel を終了した・Excel が落ちたときは false。
    /// Excel が編集中・応答待ちのときは例外がそのまま出る(つながってはいるので、少し待って再試行する)。
    /// </summary>
    public bool IsConnected()
    {
        if (_disposed || _app is null || _books is null) return false;
        try
        {
            _ = (int)((dynamic)_books).Count;
            return true;
        }
        catch (Exception ex) when (IsDisconnected(ex))
        {
            return false;
        }
    }

    /// <summary>Excel を画面に出して人に渡す。以後 Dispose してもブックと Excel は閉じない。</summary>
    public void HandOverToUser()
    {
        App.Visible = true;
        App.UserControl = true;
        IsHandedOver = true;
    }

    internal void Forget(ExcelWorkbook workbook) => _workbooks.Remove(workbook);

    /// <summary>
    /// このアプリが開いたブックを保存せずに閉じ、<see cref="QuitMode"/> に従って Excel を終了し、COM の参照を解放する。
    /// <see cref="HandOverToUser"/> のあとは閉じずに参照だけ解放する。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        foreach (var workbook in _workbooks.ToArray()) workbook.Dispose();
        _workbooks.Clear();
        _disposed = true;
        if (!IsHandedOver && _app is not null)
        {
            try
            {
                bool quit = QuitMode == ExcelQuitMode.Always
                    || _books is not null && (int)((dynamic)_books).Count == 0;
                if (quit) ((dynamic)_app).Quit();
            }
            catch { }
        }
        Release(_books);
        Release(_app);
        _books = _app = null;
        // dynamic の途中で作られた参照も解放し、Excel が裏に残らないようにする(元: NoiseTest)。
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}
