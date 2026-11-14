using System.Text.Json;
using System.ComponentModel;
using PSCAutomation.Wrappers.LoggerFiles;
namespace PSCAutomation.Apps.AS110AllCheck;
internal sealed class TransferSettings
{
    [DisplayName("電流チャンネル（1始まり）")]
    public int CurrentChannel { get; set; } = 1;
    [DisplayName("圧力チャンネル（1始まり）")]
    public int PressureChannel { get; set; } = 2;
    [DisplayName("平均する点数（圧縮）")]
    public int Compression { get; set; } = 5;
    [DisplayName("NR-X 電流CSV列（0は自動）")]
    public int NrxCurrentCsvColumn { get; set; } = 0;
    [DisplayName("NR-X 圧力CSV列（0は自動）")]
    public int NrxPressureCsvColumn { get; set; } = 0;
    [DisplayName("測定点数")]
    public int ExpectedSamples { get; set; } = 6500;
    [DisplayName("データ転送先シート")]
    public string DataSheet { get; set; } = "sheet1";
    [DisplayName("判定セルのシート（E21・L21）")]
    public string JudgmentSheet { get; set; } = "sheet1";
    [DisplayName("電流の開始セル")]
    public string CurrentCell { get; set; } = "A4";
    [DisplayName("圧力の開始セル")]
    public string PressureCell { get; set; } = "B4";
    [DisplayName("保存先フォルダ")]
    public string SaveRoot { get; set; } = "";
    // 漏れ試験の自動判定(2026-11-12)。PC ごとに覚える。古い版はこの項目を読み飛ばす。
    // 回避のチェックは転送設定の画面の下に置く(表の中には出さない)。秒数・しきい値はメイン画面の記入欄。
    [Browsable(false)]
    public bool AllowLeakOverride { get; set; }
    [Browsable(false)]
    public int LeakTestSeconds { get; set; } = 10;
    [Browsable(false)]
    public double LeakThresholdKpa { get; set; } = 1.0;
    // 不合格・判定できないときに判定画面へ結果を出しておく秒数(合格は 0.5 秒のまま)。全型式で共通(2026-11-12 続き)。
    [Browsable(false)]
    public double LeakFailDisplaySeconds { get; set; } = 2.0;
    // 圧力の最大値の下限(kPa)。型式ごとに覚える(2026-11-12 続き)。初期値があるのは下の型式だけ、ほかの型式は入れるまで無い。
    // 初期値: AS110-035 は 530 kPa、AS110-015 は 550 kPa、AS110-006 は 550 kPa(いずれも 2026-11-12、人の指示)。
    public static IReadOnlyDictionary<string,double> DefaultLeakMaximumLowerLimitKpa { get; } =
        new Dictionary<string,double> { ["AS110-035"]=530.0, ["AS110-015"]=550.0, ["AS110-006"]=550.0 };
    [Browsable(false)]
    public Dictionary<string,double> LeakMaximumLowerLimitKpa { get; set; } = new(DefaultLeakMaximumLowerLimitKpa);
    // 移動平均はLOGGERのチャンネル設定をXdtReaderが適用する。
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AS110BoltCheck","ver3.json");
    public static TransferSettings Load() => LoadFrom(SettingsPath);
    // 設定ファイルを読めなかったときの知らせ(読めたときは null)。起動したら画面に出す。ファイルには書かない。
    [Browsable(false)]
    [System.Text.Json.Serialization.JsonIgnore]
    public string? LoadWarning { get; private set; }
    // 設定ファイルが壊れていても(書いている一瞬に電源が落ちると、空・途中までになる)、初期値で起動して知らせる
    // (2026-11-14、全体見直し・区切り 5。それまでは起動時の読み込みで落ち、人がファイルを探して消すまで起動できなかった)。
    // 壊れたファイルは名前を変えて残す(次の保存で上書きしない)。ほかで開いていて読めないだけのときは、ファイルに触らない。
    internal static TransferSettings LoadFrom(string path)
    {
        if(!File.Exists(path)) return new();
        try { return FromJson(File.ReadAllText(path)); }
        catch(JsonException ex)
        {
            string kept=Path.Combine(Path.GetDirectoryName(path)!,$"{Path.GetFileNameWithoutExtension(path)}_読めなかった設定_{DateTime.Now:yyyyMMdd_HHmmss}{Path.GetExtension(path)}");
            string keptNote;
            try { File.Move(path,kept); keptNote=$"読めなかったファイルは、名前を変えて残しました：{kept}"; }
            catch(Exception moveEx) when(moveEx is IOException or UnauthorizedAccessException) { keptNote=$"読めなかったファイルは、次に設定を保存したときに上書きされます：{path}"; }
            return new TransferSettings { LoadWarning=
                "設定ファイルの中身が壊れていて読めなかったので、設定を初期値に戻して起動しました。\n" +
                "「転送設定」・漏れ試験の欄（試験秒数・しきい値・最大値の下限）を変えていた場合は、入れ直してください。保存先は選び直せば元どおりです。\n" +
                keptNote+$"\n（詳細：{ex.Message}）" };
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            return new TransferSettings { LoadWarning=
                "設定ファイルを開けなかったので、設定を初期値にして起動しました。\n" +
                "設定を変えていた場合は、アプリを閉じて、このファイルをほかで開いていないか確かめてから起動し直してください（このまま設定を保存すると、前の設定は上書きされます）。\n" +
                $"{path}\n（詳細：{ex.Message}）" };
        }
    }
    // 保存済みの設定ファイルは下限の表ごと読み込まれ、後から初期値を足した型式が抜ける。無い型式にだけ初期値を足す(人が入れた値は変えない)。
    internal static TransferSettings FromJson(string json)
    {
        var settings=JsonSerializer.Deserialize<TransferSettings>(json) ?? new();
        settings.LeakMaximumLowerLimitKpa ??= new();
        foreach(var (model,limit) in DefaultLeakMaximumLowerLimitKpa) settings.LeakMaximumLowerLimitKpa.TryAdd(model,limit);
        return settings;
    }
    // その型式・モードで使う転送の設定。測る時間が違う型式(AS110-006 のデータ取りは 2 倍。MeasurementProfile)は、測定点数と平均する点数をその倍率にする
    // (6500 点・5 点平均 → 13000 点・10 点平均。Excel へは同じ 1300 点)。ほかの型式・データ調整は同じ値の写し。写しは保存しない(画面で直す値は元のまま)。
    public TransferSettings ForModel(ProductModel model,TestMode mode)
    {
        var copy=(TransferSettings)MemberwiseClone();
        int factor=MeasurementProfile.LengthFactor(model,mode);
        copy.ExpectedSamples=ExpectedSamples*factor;
        copy.Compression=Compression*factor;
        return copy;
    }
    // 転送設定の画面の表で直すための写し。表で直した値はこの写しに入り、元の設定は「設定保存」を押すまで変わらない
    // (2026-11-13、全体見直し。以前は表が元の設定をその場で書き換え、× で閉じても戻らなかった)。
    public TransferSettings CreateDraft() => (TransferSettings)MemberwiseClone();
    // 写しで直した値を取り込む。取り込むのは表に出る項目だけ(表に出さない漏れ試験の設定は、メイン画面の欄や回避のチェックが別に持つ)。
    // 項目を足しても漏れないよう、名前を並べずに表に出る項目([Browsable(false)] でないもの)を全部写す。
    public void ApplyEdited(TransferSettings draft)
    {
        foreach(var property in typeof(TransferSettings).GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
        {
            if(!property.CanRead || !property.CanWrite) continue;
            if(property.GetCustomAttributes(typeof(BrowsableAttribute),true).Cast<BrowsableAttribute>().Any(a=>!a.Browsable)) continue;
            property.SetValue(this,property.GetValue(draft));
        }
    }
    // 「設定保存」の前の確かめ(2026-11-14、全体見直し・区切り 5)。問題が無ければ空。あれば人に見せる文を 1 件ずつ返す(保存しない)。
    // それまでは何でも保存でき、0 や書き間違いは測定したあとの転送で初めて止まった。電流と圧力に同じチャンネルを入れると、
    // NR-500 では両方の列に同じ値がエラーなしで転送された。
    // excelTemplates: いま雛形フォルダーにある Excel 雛形(フルパス)。シート名がその雛形にあるかを確かめる。読めない雛形は飛ばす(転送のときに分かる)。
    public IReadOnlyList<string> FindProblems(IEnumerable<string>? excelTemplates=null)
    {
        var problems=new List<string>();
        if(CurrentChannel<1 || PressureChannel<1) problems.Add("電流チャンネル・圧力チャンネルは 1 以上にしてください。");
        else if(CurrentChannel==PressureChannel) problems.Add($"電流チャンネルと圧力チャンネルが同じ（CH{CurrentChannel}）です。別のチャンネルにしてください。");
        if(Compression<1) problems.Add("平均する点数（圧縮）は 1 以上にしてください。");
        if(ExpectedSamples<1) problems.Add("測定点数は 1 以上にしてください。");
        if(Compression>=1 && ExpectedSamples>=1 && ExpectedSamples%Compression!=0) problems.Add($"測定点数 {ExpectedSamples} を、平均する点数 {Compression} で割り切れません。");
        if(NrxCurrentCsvColumn<0 || NrxPressureCsvColumn<0) problems.Add("NR-X の CSV 列は 0（自動）か 1 以上にしてください。");
        else if(NrxCurrentCsvColumn>0 && NrxCurrentCsvColumn==NrxPressureCsvColumn) problems.Add($"NR-X の電流 CSV 列と圧力 CSV 列が同じ（{NrxCurrentCsvColumn}）です。");
        if(string.IsNullOrWhiteSpace(DataSheet)) problems.Add("データ転送先シートが空です。");
        if(string.IsNullOrWhiteSpace(JudgmentSheet)) problems.Add("判定セルのシートが空です。");
        (int Column,int Row)? Cell(string name,string address)
        {
            try { return PSCAutomation.Wrappers.Excel.TemplateWriter.ParseCell(address ?? ""); }
            catch(ArgumentException) { problems.Add($"{name}「{address}」をセル番地として読めません（例 A4）。"); return null; }
        }
        var current=Cell("電流の開始セル",CurrentCell);
        var pressure=Cell("圧力の開始セル",PressureCell);
        if(current is not null && pressure is not null && current.Value.Column==pressure.Value.Column) problems.Add("電流の開始セルと圧力の開始セルが同じ列です。別の列にしてください。");
        if(Compression>=1 && ExpectedSamples>=1)
            foreach(var cell in new[]{current,pressure})
                if(cell is not null && (long)cell.Value.Row+ExpectedSamples/Compression-1>1048576) { problems.Add("開始セルの行が下すぎて、測定値が Excel の最後の行を超えます。"); break; }
        foreach(string template in excelTemplates ?? [])
        {
            string[] sheets;
            try { sheets=PSCAutomation.Wrappers.Excel.TemplateWriter.GetSheets(template); }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidDataException) { continue; }
            foreach(var (name,sheet) in new[]{("データ転送先シート",DataSheet),("判定セルのシート",JudgmentSheet)}.DistinctBy(s=>s.Item2))
                if(!string.IsNullOrWhiteSpace(sheet) && !sheets.Contains(sheet,StringComparer.Ordinal))
                    problems.Add($"{name}「{sheet}」が {Path.GetFileName(template)} にありません（あるシート：{string.Join("・",sheets)}）。");
        }
        return problems;
    }
    public void Save() => SaveTo(SettingsPath);
    // 別の名前で書き終えてから置き換える。書いている途中で電源が落ちても、前の設定ファイルがそのまま残る(2026-11-14)。
    internal void SaveTo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary=path+".tmp";
        File.WriteAllText(temporary,JsonSerializer.Serialize(this,new JsonSerializerOptions { WriteIndented=true }));
        File.Move(temporary,path,true);
    }
    // NR-X は LOGGER の標準 CSV を読む。列番号は上の NR-X CSV 列(0 は見出しの「電流」「圧力」・CH 番号から自動検出)。
    // 読み取りは Wrappers/LoggerFiles の LoggerCsvReader(移植前は Conversion/LoggerCsvReader.cs。判定の中身は同じ)。
    public (double[] Current,double[] Pressure) ReadNrxCsv(string path)
    {
        try
        {
            var values=LoggerCsvReader.Read(path,[
                new(CurrentChannel,"電流",NrxCurrentCsvColumn),
                new(PressureChannel,"圧力",NrxPressureCsvColumn)],ExpectedSamples);
            return (values[0],values[1]);
        }
        // 共通部品の文言には転送設定の項目名が無いので、どこを直すかを足す。
        catch(InvalidDataException ex) { throw new InvalidDataException($"NR-X {ex.Message}（転送設定のNR-X 電流・圧力CSV列）",ex); }
    }
    public static double[] Compress(double[] input,int count)
    {
        if(count<1 || input.Length%count!=0) throw new InvalidDataException("測定点数を圧縮点数で割り切れません。");
        var output=new double[input.Length/count];
        for(int i=0;i<output.Length;i++) { double sum=0; for(int j=0;j<count;j++) sum+=input[i*count+j]; output[i]=(float)(sum/count); }
        return output;
    }
}
