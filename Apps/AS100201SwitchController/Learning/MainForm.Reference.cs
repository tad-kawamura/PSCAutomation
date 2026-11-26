using System.Diagnostics;

namespace PSCAutomation.Apps.AS100201SwitchController;

// 学習モードの参照波形(2026-11-26 人の決定。SPEC_調整操作の記録.md「参照波形を LOGGER に出す」)。
// 雛形を開いた直後に、exe の隣の「雛形」フォルダーの参照波形(.xyb、PC と CR を 1 本につないだもの)を 1 回だけ読み込む。
// PC_go / CR_go で切り替えない(1 本に PC と CR の両方が入っているため)。
// 見つからない・読み込めないときは READY LED を消して測定を止める(LearningReady)。直すには参照波形を置いて「確認」。
// 通常モードでは読み込まない(人がドロップする雛形は 1盲栓 以外もあるため)。
internal sealed partial class MainForm : Form
{
    private bool _referenceLoaded;
    // 読み込めなかった理由(「次にすること」に出す)。読み込む前・読み込めたときは null。
    private string? _referenceProblem;

    // 雛形を開いたあとに呼ぶ。読み込めなくても例外にはせず、理由を残して止める(接続確認・雛形の読み込みは成功のまま)。
    private async Task<string> LoadLearningReferenceAsync()
    {
        _referenceLoaded = false;
        _referenceProblem = null;
        string folder = Path.Combine(AppContext.BaseDirectory, TemplateFolderName);
        if (FindLearningReference(folder, out string reason) is not string path)
            return ReferenceFailed(reason);
        var timer = Stopwatch.StartNew();
        try
        {
            await _logger.LoadReferenceAsync(path, _measurementState.State, AppendLog, () =>
            {
                // LOGGER X のトリガ待ちを止めて読み込んだとき(旧 LOGGER では呼ばれない)。
                _measurementState.MeasurementStarted = false;
                _measurementState.State = 1;
                _measurementState.ReadAfter = 0;
            });
        }
        catch (Exception ex)
        {
            return ReferenceFailed(ex.Message);
        }
        timer.Stop();
        _referenceLoaded = true;
        AppendLog($"処理時間：参照波形の読み込み = {timer.Elapsed.TotalSeconds:F3} 秒（{Path.GetFileName(path)}）");
        return "参照波形 " + Path.GetFileName(path);
    }

    private string ReferenceFailed(string reason)
    {
        _referenceProblem = reason;
        AppendLog("参照波形を読み込めません：" + reason + "（読み込めるまで READY LED を消して測定を受け付けません。参照波形を置いて「確認」を押してください）");
        return "参照波形なし";
    }

    private void ResetLearningReference()
    {
        _referenceLoaded = false;
        _referenceProblem = null;
    }

    // 「雛形」フォルダーの .xyb がちょうど 1 つならそれを返す。無い・2 つ以上なら null と理由。
    public static string? FindLearningReference(string templateFolder, out string reason)
    {
        reason = "";
        if (!Directory.Exists(templateFolder))
        {
            reason = "雛形フォルダーがありません：" + templateFolder;
            return null;
        }
        string[] files = Directory.GetFiles(templateFolder, "*.xyb");
        if (files.Length == 1) return files[0];
        reason = files.Length == 0
            ? "雛形フォルダーに参照波形（.xyb）がありません：" + templateFolder
            : $"雛形フォルダーに参照波形（.xyb）が {files.Length} つあり、どれを使うか決められません：" + templateFolder;
        return null;
    }
}
