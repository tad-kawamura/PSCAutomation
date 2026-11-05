namespace PSCAutomation.Apps.AS110AllCheck;

// LOGGERの種類だけでなく、接続確認で検出した本体から雛形を選ぶ。
// LOGGER XでNR-500を使用するときもXDTを選択する。
internal sealed partial class MainForm : Form
{
    private string GetDetectedTemplatePath()
    {
        if (!_hardwareDetected)
            throw new InvalidOperationException("NR本体の接続確認後に雛形を選択してください。");

        string extension;
        if (string.Equals(_detectedHardwareName, "NR-500", StringComparison.OrdinalIgnoreCase))
        {
            extension = ".xdt";
        }
        else if (_detectedHardwareName.StartsWith("NR-X", StringComparison.OrdinalIgnoreCase))
        {
            if (_logger.ActiveEdition != WaveLoggerEdition.WaveLoggerX)
                throw new InvalidOperationException("NR-XのYDT雛形にはWAVE LOGGER Xが必要です。");
            extension = ".ydt";
        }
        else
        {
            throw new InvalidOperationException("雛形の選択に未対応の本体です：" + _detectedHardwareName);
        }

        string path = GetAutomaticTemplatePath(extension);
        AppendLog($"使用雛形：本体={_detectedHardwareName} / {Path.GetFileName(path)}");
        return path;
    }
}
