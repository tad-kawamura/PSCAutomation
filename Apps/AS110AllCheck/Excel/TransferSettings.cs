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
    // 圧力の最大値の下限(kPa)。型式ごとに覚える(2026-11-12 続き)。AS110-035 だけ初期値あり、ほかの型式は入れるまで無い。
    [Browsable(false)]
    public Dictionary<string,double> LeakMaximumLowerLimitKpa { get; set; } = new() { ["AS110-035"]=530.0 };
    // 移動平均はLOGGERのチャンネル設定をXdtReaderが適用する。
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AS110BoltCheck","ver3.json");
    public static TransferSettings Load() => File.Exists(SettingsPath)
        ? JsonSerializer.Deserialize<TransferSettings>(File.ReadAllText(SettingsPath)) ?? new() : new();
    public void Save() { Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!); File.WriteAllText(SettingsPath,JsonSerializer.Serialize(this,new JsonSerializerOptions { WriteIndented=true })); }
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
