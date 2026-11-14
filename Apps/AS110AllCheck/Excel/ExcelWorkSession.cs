using System.Diagnostics;
namespace PSCAutomation.Apps.AS110AllCheck;

// このアプリが作った作業ブックだけを保持する。既存のExcelには接続しない。
// Excel の起動・ブックの開閉・列への書き込み・図形の文字の置き換え・計算待ち・前面表示は Wrappers/Excel の部品を使う。
// ここに残すのは AS110 固有の処理: 作業ブックの再利用と事前準備、圧縮5の図形の S/N・型式・供給圧力・測定日、
// 判定セル E21・L21、保存(Excel の別名保存と圧縮5の PDF)。
internal sealed class ExcelWorkSession : IDisposable
{
    private const string HeaderSheet = "圧縮5";
    private ExcelApplication? _app;
    private ExcelWorkbook? _book;
    private string _dataSheet="sheet1";
    private string _judgmentSheet="sheet1";
    private (string Path, long Length, DateTime Modified, string Sheet, string Current, string Pressure, int Count)? _source;
    private (string Model, string Serial, string Pressure, string Date)? _header;
    // 人の書き換えを見つけるための覚え(WorkbookEdits.cs)。_baseline はブックを雛形から作って開いた直後の中身、_dataArea は比べない測定値の列、
    // _approvedEdits はパスワードで進めた書き換え(同じ書き換えではもう一度聞かない)。
    private Dictionary<string,SheetCells>? _baseline;
    private (string Sheet,int Column1,int Column2,int FirstRow1,int FirstRow2,int Count)? _dataArea;
    private string? _approvedEdits;
    public bool Closed { get; private set; }
    public string JudgmentDescription { get; private set; } = "未確認";
    internal ExcelWorkSession() { }
    public bool BringToFront()
    {
        if (Closed || _book is null || _app is null)
            throw new InvalidOperationException("作業用Excelが閉じられています。再転送してください。");
        return WindowActivation.TryActivate(_book.ShowWindow(_judgmentSheet));
    }

    public static ExcelWorkSession Open(string template,string output,string captured,TransferSettings settings,
        ProductModel model,string serial,DateTime measuredAt, ExcelWorkSession? reusable = null,
        Action<string>? log = null)
    {
        var timer = Stopwatch.StartNew();
        void Timing(string step) { log?.Invoke($"Excel転送時間：{step}={timer.Elapsed.TotalMilliseconds:F0}ms"); timer.Restart(); }
        double[] current,pressure;
        if(Path.GetExtension(captured).Equals(".ydt",StringComparison.OrdinalIgnoreCase))
            (current,pressure)=settings.ReadNrxCsv(captured+".csv");
        else
        {
            var values=XdtReader.Read(captured,[settings.CurrentChannel,settings.PressureChannel]);
            if(values.Info.Samples!=settings.ExpectedSamples) throw new InvalidDataException($"測定点数が{values.Info.Samples}点です。設定{settings.ExpectedSamples}点と一致しません。");
            current=values.Values[settings.CurrentChannel];pressure=values.Values[settings.PressureChannel];
        }
        Timing("測定データ読込");
        current=TransferSettings.Compress(current,settings.Compression);
        pressure=TransferSettings.Compress(pressure,settings.Compression);
        var file=new FileInfo(template);
        var source=(file.FullName,file.Length,file.LastWriteTimeUtc,settings.DataSheet,settings.CurrentCell,settings.PressureCell,current.Length);
        var session=reusable ?? new ExcelWorkSession();
        session.PrepareForTransfer();
        Timing("圧縮・設定・前回ブック確認");
        bool reuseBook=session._book is not null && !session.Closed && session._source==source;
        // 人が書き換えたブックは、その番号の間だけ使う。番号が変わったら雛形から作り直す(書き換えを次の台に持ち越さない。2026-11-14、人と決めた)。
        // 比べるのは番号が変わったときだけ(同じ番号の測り直しでは、書き換えたブックをそのまま使う)。
        if(reuseBook && session._header is { } previousHeader && previousHeader.Serial!=serial)
        {
            int edited=session.FindEdits().Count;
            Timing("書き換え確認（番号が変わったため）");
            if(edited>0)
            {
                reuseBook=false;
                log?.Invoke($"Excel転送：前の番号（S/N {previousHeader.Serial}）で作業用 Excel が {edited} か所書き換えられていたので、雛形から作り直します。");
            }
        }
        session._dataSheet=settings.DataSheet;
        session._judgmentSheet=settings.JudgmentSheet;
        try
        {
            if(reuseBook)
            {
                session.UpdateMeasurements(settings,current,pressure);
                Timing("測定値更新（ブック再利用）");
            }
            else
            {
                if(session._book is not null && !session.Closed)session.CloseVerified();
                if(File.Exists(output))File.Delete(output);
                EnsureTemplateReadable(template);
                TemplateWriter.Save(template,output,[
                    new(settings.DataSheet,settings.CurrentCell,current),
                    new(settings.DataSheet,settings.PressureCell,pressure)]);
                Timing("作業ブック作成");
            }
            bool reuseApp=session.EnsureApp();
            session._app!.Visible=true; session._app.DisplayAlerts=true;
            Timing(reuseApp ? "Excel再利用" : "Excel起動");
            if(!reuseBook)
            {
                session.OpenBook(output);
                Timing("ブック表示");
                session.TakeBaseline(settings,current.Length);
                Timing("書き換え確認の基準");
            }
            bool headerChanged=session.UpdateHeader(model,serial,measuredAt);
            session._app.SetAutomaticCalculation();
            session._book!.Activate();
            Timing(headerChanged ? "ヘッダ更新" : "ヘッダ変更なし");
            session._source=source;
            return session;
        }
        // 応答待ち・編集中の失敗で所有参照を捨てると、次回別のExcelを起動してしまう。
        catch { if(reusable is null)session.Dispose(); throw; }
    }
    // 測定前に、転送と同じ雛形・設定の作業ブックを0の測定値で開いておく。
    // 次の転送はブック再利用として全点を上書きする。Excelは表示せず、LOGGERの前面を奪わない。
    // 転送で使う操作も一度呼び、初回転送時のCOM呼び出しの準備時間を先に済ませる。
    public static async Task<ExcelWorkSession> PrepareAsync(string template,string output,TransferSettings settings,
        ProductModel model,string serial,ExcelWorkSession? reusable=null,Action<string>? log=null)
    {
        var timer=Stopwatch.StartNew();
        double[] zeros=TransferSettings.Compress(new double[settings.ExpectedSamples],settings.Compression);
        var file=new FileInfo(template);
        var source=(file.FullName,file.Length,file.LastWriteTimeUtc,settings.DataSheet,settings.CurrentCell,settings.PressureCell,zeros.Length);
        var session=reusable ?? new ExcelWorkSession();
        session.PrepareForTransfer();
        if(session._book is not null && !session.Closed && session._source==source)
        {
            log?.Invoke("Excel事前準備：作業ブックは準備済みです。");
            return session;
        }
        session._dataSheet=settings.DataSheet;
        session._judgmentSheet=settings.JudgmentSheet;
        try
        {
            if(session._book is not null && !session.Closed)session.CloseVerified();
            await Task.Run(()=>{
                if(File.Exists(output))File.Delete(output);
                EnsureTemplateReadable(template);
                TemplateWriter.Save(template,output,[
                    new(settings.DataSheet,settings.CurrentCell,zeros),
                    new(settings.DataSheet,settings.PressureCell,zeros)]);
            });
            bool reuseApp=session.EnsureApp();
            session.OpenBook(output);
            session.TakeBaseline(settings,zeros.Length);
            session.UpdateMeasurements(settings,zeros,zeros);
            session.UpdateHeader(model,serial,DateTime.Now);
            session._app!.SetAutomaticCalculation();
            await session.IsPassAsync();
            session._source=source;
            log?.Invoke($"Excel事前準備：完了（{(reuseApp?"Excel再利用":"Excel起動")}）={timer.Elapsed.TotalMilliseconds:F0}ms");
            return session;
        }
        catch { if(reusable is null)session.Dispose(); throw; }
    }
    // Excel 雛形を人が Excel で開いていて読めないときは、閉じるファイルの名前を日本語で出す(2026-11-14、全体見直し・区切り 5)。
    // 雛形への書き込み(TemplateWriter)と同じ開き方で確かめる。ファイルが無いときは、部品の文(FileNotFoundException)のまま。
    private static void EnsureTemplateReadable(string template) =>
        FileAccessAdvice.Run("Excel 雛形",template,()=>{ using var probe=new FileStream(template,FileMode.Open,FileAccess.Read,FileShare.Read); },
            "閉じたあと、もう一度「Excel転送」を押してください。",$"「{Path.GetFileName(template)}」を Excel で開いていたら閉じてください");
    // 起動直後の Excel は画面に出さない(事前準備で LOGGER の前面を奪わないため)。確認ダイアログは Excel の既定どおり出す。
    private bool EnsureApp()
    {
        bool reuse=_app is not null;
        _app ??= ExcelApplication.Start(visible:false,displayAlerts:true);
        return reuse;
    }
    private void OpenBook(string output)
    {
        _book=_app!.Open(output);
        Closed=false;
        _header=null;
        _baseline=null;_approvedEdits=null;
    }
    // ブックを雛形から作って開いた直後に、全シートの中身を覚える(人の書き換えと比べる相手)。測定値の 2 列(開始セルから点数ぶん)は比べない。
    private void TakeBaseline(TransferSettings settings,int count)
    {
        var current=TemplateWriter.ParseCell(settings.CurrentCell);
        var pressure=TemplateWriter.ParseCell(settings.PressureCell);
        _dataArea=(settings.DataSheet,current.Column,pressure.Column,current.Row,pressure.Row,count);
        _approvedEdits=null;
        _baseline=ReadAllCells();
    }
    private bool IsMeasurementCell(string sheet,int row,int column) =>
        _dataArea is { } area && string.Equals(sheet,area.Sheet,StringComparison.OrdinalIgnoreCase)
        && ((column==area.Column1 && row>=area.FirstRow1 && row<area.FirstRow1+area.Count) || (column==area.Column2 && row>=area.FirstRow2 && row<area.FirstRow2+area.Count));
    private Dictionary<string,SheetCells> ReadAllCells()
    {
        var result=new Dictionary<string,SheetCells>(StringComparer.Ordinal);
        foreach(string name in _book!.SheetNames)
        {
            using var sheet=_book.Sheet(name);
            var used=sheet.ReadUsedFormulas();
            result[name]=SheetCells.From(used.Row,used.Column,used.Values,(row,column)=>IsMeasurementCell(name,row,column));
        }
        return result;
    }
    // 人が書き換えたセル(ブックを作った直後と違うところ)。無ければ空。Excel が編集中・応答待ちのときは例外がそのまま出る。
    public IReadOnlyList<WorkbookEdit> FindEdits()
    {
        if(Closed || _book is null) throw new InvalidOperationException("作業用Excelが閉じられています。再転送してください。");
        return _baseline is null ? [] : WorkbookEdits.Compare(_baseline,ReadAllCells());
    }
    // パスワードで進めた書き換えと同じなら、もう一度聞かない(保存をやり直したとき)。そのあとでさらに書き換えたら、また聞く。
    public bool NeedsApproval(IReadOnlyList<WorkbookEdit> edits) => edits.Count>0 && WorkbookEdits.Signature(edits)!=_approvedEdits;
    public void Approve(IReadOnlyList<WorkbookEdit> edits) => _approvedEdits=WorkbookEdits.Signature(edits);
    private void UpdateMeasurements(TransferSettings settings,double[] current,double[] pressure)
    {
        if(_book!.IsReadOnly)throw new IOException("作業用Excelが読み取り専用です。");
        if(TemplateWriter.ParseCell(settings.CurrentCell).Column==TemplateWriter.ParseCell(settings.PressureCell).Column)
            throw new ArgumentException("同一列へ重複転送できません。");
        // 測定値の転送先に数式があれば何も書かずに止める。隣接する電流・圧力列は一度に更新し、途中状態での再計算・再描画を減らす。
        using var sheet=_book.Sheet(settings.DataSheet);
        sheet.WriteColumns([new(settings.CurrentCell,current),new(settings.PressureCell,pressure)]);
    }
    private bool UpdateHeader(ProductModel model,string serial,DateTime measuredAt)
    {
        var header=(model.Name,serial,model.SupplyPressure.ToString(),measuredAt.ToString("yyyy.MM.dd"));
        if(_header==header)return false;
        using var sheet=_book!.Sheet(HeaderSheet);
        // 全文代入は文字ごとのフォント・下線・段落書式を失う。値だけを後ろから置換する(部品の動作)。
        int shapes=sheet.ReplaceShapeText(text=>text.Contains("S/N") && text.Contains("測定日"),[
            new(@"(?<value>AS110-[A-Za-z0-9]+)",model.Name),
            new(@"S/N[ \t　]*(?<value>\d+|-)",serial),
            new(@"供給圧力[ \t　]*(?<value>\d+(?:\.\d+)?)",model.SupplyPressure.ToString()),
            new(@"測定日[ \t　]*(?<value>\d{4}[./-]\d{1,2}[./-]\d{1,2})",measuredAt.ToString("yyyy.MM.dd"))]);
        if(shapes==0) throw new InvalidDataException("圧縮5のS/N・測定日を含む図形が見つかりません。");
        _header=header;
        return true;
    }
    public bool IsPass()
    {
        if(Closed || _book is null) throw new InvalidOperationException("作業用Excelが閉じられています。再転送してください。");
        _book.CalculateSheets(_dataSheet);
        if(_app!.IsCalculating) throw new InvalidOperationException("Excelが計算中です。しばらく待って再操作してください。");
        return ReadPass();
    }
    public async Task<bool> IsPassAsync()
    {
        if(Closed || _book is null) throw new InvalidOperationException("作業用Excelが閉じられています。再転送してください。");
        // 入力シートを先に計算してから全シートを計算する。再計算待ちは計算実行中ではない。
        _book.CalculateSheets(_dataSheet);
        await _app!.WaitForCalculationAsync(TimeSpan.FromSeconds(30));
        return ReadPass();
    }
    // 転送後の監視では再計算を強制せず、計算中は保存を保留する。
    public bool? ReadCurrentPass()
    {
        if(Closed || _book is null) throw new InvalidOperationException("作業用Excelが閉じられています。再転送してください。");
        if(_app!.IsCalculating) return null;
        return ReadPass();
    }
    private bool ReadPass()
    {
        using var sheet=_book!.Sheet(_judgmentSheet);
        string left=sheet.GetText("E21");
        string right=sheet.GetText("L21");
        JudgmentDescription=$"{_judgmentSheet}!E21=[{left}]・L21=[{right}]";
        return string.Equals(left,"OK",StringComparison.OrdinalIgnoreCase)
            && string.Equals(right,"OK",StringComparison.OrdinalIgnoreCase);
    }
    public void SaveCopies(string excel,string pdf)
    {
        if(!IsPass()) throw new InvalidOperationException("E21・L21の両方がOKのときだけ保存できます。");
        SaveCopiesCore(excel,pdf);
    }
    // 判定しない出荷データ(AS110-116B350)の保存。計算は終わらせるが、E21・L21 は見ない(2026-11-12、SPEC「AS110-116B350 の出荷データ」)。
    public void SaveCopiesWithoutJudgment(string excel,string pdf)
    {
        if(Closed || _book is null) throw new InvalidOperationException("作業用Excelが閉じられています。再転送してください。");
        _book.CalculateSheets(_dataSheet);
        if(_app!.IsCalculating) throw new InvalidOperationException("Excelが計算中です。しばらく待って再操作してください。");
        SaveCopiesCore(excel,pdf);
    }
    private void SaveCopiesCore(string excel,string pdf)
    {
        _book!.SaveCopyAs(excel);
        using var sheet=_book.Sheet(HeaderSheet);
        sheet.ExportAsPdf(pdf);
    }
    public void CloseVerified()
    {
        if(Closed) return;
        if(_book is null) throw new InvalidOperationException("対象Excelを確認できません。");
        _book.Close(false);
        Closed=true;_book=null;
    }
    // 合格確定用。作業ブックがまだ開いていれば閉じて false を返す。人がブックや Excel を先に閉じていた(ブック一覧に無い・Excel が切断)なら、
    // 閉じる操作をせずに閉じた扱いにして true を返す(2026-11-06、人の判断: 保存できたから Excel を閉じるのは自然な操作で、合格を止めない)。
    // Excel 自体が切断されていれば参照も返す(次の番号は新しい Excel を起動する)。編集中・応答待ちのエラーは例外のまま返す(切断とは区別する)。
    public bool CloseVerifiedOrAlreadyClosed()
    {
        if(Closed) return false;
        if(_book is null) throw new InvalidOperationException("対象Excelを確認できません。");
        if(_book.IsStillOpen()) { _book.Close(false); Closed=true;_book=null; return false; }
        _book=null;Closed=true;_source=null;_header=null;
        bool connected;
        try { connected=_app?.IsConnected() ?? false; }
        catch { connected=true; }
        if(!connected) ReleaseApplication();
        return true;
    }
    // 自分で起動したExcelだけを再利用し、他のExcelには接続しない。
    public void PrepareForTransfer()
    {
        if(_book is null || Closed)return;
        // 閉じたブック自体へアクセスせず、所有Excelのブック一覧で確認する。
        // Excelの編集中・応答待ちは例外をそのまま返し、同じ参照で再試行する。
        if(_book.IsStillOpen())return;
        _book=null;Closed=true;_source=null;_header=null;
        // サーバーとの切断が確定した場合だけ、新しいExcelを起動できる状態にする。
        // 人がブックだけを閉じた場合は、同じExcelを使い続ける。
        bool connected;
        try { connected=_app?.IsConnected() ?? false; }
        catch { connected=true; }
        if(!connected) ReleaseApplication();
    }
    public void Dispose()
    {
        try { _book?.Dispose(); } catch { }
        ReleaseApplication();
        Closed=true;_source=null;_header=null;
    }
    // ブックを閉じたあとほかにブックが無ければ Excel を終了し、参照を返す(部品の既定 ExcelQuitMode.WhenNoWorkbooks)。
    private void ReleaseApplication()
    {
        try { _app?.Dispose(); } catch { }
        _app=null;_book=null;Closed=true;_source=null;_header=null;
    }
}
