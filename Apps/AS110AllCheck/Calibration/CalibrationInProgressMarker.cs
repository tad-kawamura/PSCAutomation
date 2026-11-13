using System.Text;

namespace PSCAutomation.Apps.AS110AllCheck;

// 校正の自動読み取りの途中であることを残すファイル(SPEC 決まったこと 19)。
// 読み取りを始めるときに書き、終わったら(成功・中止・失敗とも、後片付けのあと)消す。
// アプリが落ちた・強制終了したときは残るので、次に起動して接続確認が済んだら、WF1973 の出力を止め、GC16 を RM,0 に戻し、
// ボールバルブを測定の向きに戻すよう知らせてから消す(MainForm.AutoCalibration.cs)。
internal sealed class CalibrationInProgressMarker(string filePath)
{
    public static CalibrationInProgressMarker Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AS110AllCheck", "校正中.txt"));

    public string FilePath { get; } = filePath;

    public bool Exists => File.Exists(FilePath);

    // 書けないときは IOException / UnauthorizedAccessException(呼び出し側で知らせて続ける)。
    public void Begin(DateTimeOffset startedAt, string modelName)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath,
            "AS110AllCheck 校正の自動読み取り中\r\n" +
            $"開始: {startedAt:yyyy/MM/dd HH:mm:ss}\r\n" +
            $"型式: {modelName}\r\n" +
            "このファイルが残っていれば、読み取りの途中でアプリが終わっています。次の起動で後片付けをしてから消します。\r\n",
            new UTF8Encoding(false));
    }

    public string? ReadText()
    {
        try { return File.Exists(FilePath) ? File.ReadAllText(FilePath, Encoding.UTF8) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // 消せなくても止めない(次の起動でもう一度後片付けの知らせが出るだけ)。消せたら true。
    public bool Clear()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
