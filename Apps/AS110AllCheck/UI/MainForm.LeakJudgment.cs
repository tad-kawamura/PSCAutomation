using System.Diagnostics;
using System.Globalization;

namespace PSCAutomation.Apps.AS110AllCheck;

// 漏れ試験の実施と自動判定(specs/AS110AllCheck/SPEC.md「漏れ試験の自動判定」、2026-11-12 承認)。
// NR-500(作業用の雛形が xdt)のときは、試験中に圧力 CH の値を読み出し続けて判定し、判定画面を LOGGER の上に出す。
// NR-X(ydt)は換算係数を読めないので自動判定せず、今までどおり人が LOGGER の画面で判断する。
// 結果は保存先の「漏れ試験記録.txt」に 1 行ずつ残す(完了記録.txt は変えない)。
internal sealed partial class MainForm : Form
{
    // 値を読み出す間隔の目安(SPEC: 10 ms から始めて実機で決める)。読み出した点は全点比べるので、間隔は「気づくまでの遅れ」だけに効く。
    private const int LeakPollMilliseconds = 10;

    private readonly TextBox _leakSecondsBox = new() { Width=60,TextAlign=HorizontalAlignment.Right };
    private readonly TextBox _leakThresholdBox = new() { Width=60,TextAlign=HorizontalAlignment.Right };
    private TableLayoutPanel _leakSettingsPanel=null!;
    // 最後に正常に終わった漏れ試験の結果(合格の回避の記録・表示に使う)。
    private LeakTestResult? _lastLeakResult;

    private bool LeakAutoJudgeAvailable =>
        _workingTemplatePath is not null && string.Equals(Path.GetExtension(_workingTemplatePath),".xdt",StringComparison.OrdinalIgnoreCase);

    // 「漏れ試験：試験秒数 [10] 秒　しきい値 [1.0] kPa」の行。前回の値を入れておく。
    private TableLayoutPanel BuildLeakSettingsRow()
    {
        _leakSecondsBox.Text=_transferSettings.LeakTestSeconds.ToString(CultureInfo.InvariantCulture);
        _leakThresholdBox.Text=_transferSettings.LeakThresholdKpa.ToString("0.0",CultureInfo.InvariantCulture);
        var thresholdLabel=new Label { Text="しきい値",AutoSize=true };
        thresholdLabel.Margin=new Padding(16,thresholdLabel.Margin.Top,thresholdLabel.Margin.Right,thresholdLabel.Margin.Bottom);
        _leakSettingsPanel=Row(new Label { Text="漏れ試験：試験秒数",AutoSize=true },_leakSecondsBox,new Label { Text="秒",AutoSize=true },
            thresholdLabel,_leakThresholdBox,new Label { Text="kPa（最大値 − 現在値がこれを超えたら不合格）",AutoSize=true });
        return _leakSettingsPanel;
    }

    // 記入欄の値を確かめて返す。だめなら試験を始めずに知らせる。変わっていれば次の起動のために覚える。
    private (int Seconds,double Threshold) ReadLeakInputs()
    {
        string? problem=LeakTestInput.Validate(_leakSecondsBox.Text,_leakThresholdBox.Text,out int seconds,out double threshold);
        if(problem is not null)throw new InvalidOperationException(problem+"漏れ試験は始めていません。");
        _leakThresholdBox.Text=threshold.ToString("0.0",CultureInfo.InvariantCulture);
        if(seconds!=_transferSettings.LeakTestSeconds || threshold!=_transferSettings.LeakThresholdKpa)
        {
            _transferSettings.LeakTestSeconds=seconds;_transferSettings.LeakThresholdKpa=threshold;
            try { _transferSettings.Save(); }
            catch(Exception ex) { AppendLog("漏れ試験の秒数・しきい値を覚えられませんでした（この試験には使います）："+ex.Message); }
        }
        return (seconds,threshold);
    }

    private async Task LeakCheckAsync()
    {
        if(!_leakButton.Enabled)return;
        LeakJudgeWindow? window=null;
        await RunOperationAsync("漏れチェック",async ()=> {
            if(!_flow.HasSaved || !_leakOutputReady)throw new InvalidOperationException("最新データ一式を保存し、+30mAを押してください。");
            if(_logger.GetState()!=1)throw new InvalidOperationException("LOGGERの完全停止を確認してから漏れチェックしてください。");
            var (seconds,threshold)=ReadLeakInputs();
            string root=_rootBox.Text;
            if(root.Length==0)throw new InvalidOperationException("保存先を選択してください（漏れ試験記録を保存先に残します）。");
            LeakMonitor? monitor=null;
            if(LeakAutoJudgeAvailable)
            {
                try { monitor=new LeakMonitor(LeakPressureChannel.FromXdt(_workingTemplatePath!,_transferSettings.PressureChannel),threshold,_logger.ReadRawData); }
                catch(Exception ex) when(ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
                { throw new InvalidDataException($"雛形から圧力（CH{_transferSettings.PressureChannel}）の換算係数を読めません。漏れ試験は始めていません。\n{ex.Message}",ex); }
            }
            else AppendLog("漏れ判定：NR-X（YDT）は自動判定しません。LOGGERの画面で判断してください。");
            _flow.LeakStarted();_lastLeakResult=null;
            _leakRunning=true;_leakCancel=new CancellationTokenSource();UpdateControls();
            try
            {
                ShowLoggerInFront("漏れチェック",()=>_leakRunning);
                if(monitor is not null)
                {
                    // LOGGER を前に出した後に出す(入力先は奪わない)。最前面なので LOGGER の上に残る。
                    window=new LeakJudgeWindow(seconds,threshold,Font);
                    window.Show();
                    AppendLog($"漏れ判定：圧力 CH{monitor.Channel.Number}（係数 {monitor.Channel.Gain.ToString(CultureInfo.InvariantCulture)} / {monitor.Channel.Offset.ToString(CultureInfo.InvariantCulture)}、移動平均 {monitor.Channel.AverageWindow} 点）を {seconds} 秒、しきい値 {threshold:F1} kPa で判定します。");
                }
                Stopwatch? sinceStart=null;
                try
                {
                    await LeakCheckSequence.RunAsync(
                        ()=>{long result=_logger.StartSingleAcquisition();sinceStart=Stopwatch.StartNew();AppendLog($"漏れチェック LOGGER Start：戻り値={result}");return result;},
                        StopLeakOutput,
                        StopLeakLoggerAsync,
                        TimeSpan.FromSeconds(seconds),
                        monitor is null?TimeSpan.FromSeconds(seconds):TimeSpan.FromMilliseconds(LeakPollMilliseconds),
                        ()=>PollLeak(monitor,window,sinceStart?.Elapsed ?? TimeSpan.Zero),
                        _leakCancel.Token);
                }
                catch(OperationCanceledException) when(_closeAfterLeak) { return "アプリ終了のため発振器出力とLOGGER収集を停止しました。"; }
                if(!ConnectionsReady || !_arduino.MeasurementInputReady)throw new IOException("通信を確認できません。漏れチェックをやり直してください。");
                var judge=monitor?.Judge;
                LeakOutcome outcome=monitor?.Finish() ?? LeakOutcome.NotJudged;
                if(window is not null){window.ShowResult(outcome);window.CloseAfterResult();}
                var record=new LeakTestResult(DateTime.Now,outcome,seconds,threshold,
                    judge?.Maximum ?? double.NaN,judge?.Latest ?? double.NaN,judge?.MaxDifference ?? double.NaN,judge?.Points ?? 0,
                    judge?.Problem ?? (monitor is null?"NR-X は自動判定なし":""));
                string path;
                try { path=LeakTestRecordFile.Append(root,_flow.Serial,_flow.Model.Name,record); }
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    throw new IOException($"漏れ試験記録を書けないため「合格」に進めません（判定：{LeakOutcomeText.Of(outcome)}）。保存先を確認して、「+30mA」から漏れ試験をやり直してください。\n{LeakTestRecordFile.PathOf(root)}\n{ex.Message}",ex);
                }
                _flow.LeakFinished(outcome);_lastLeakResult=record;
                AppendLog("漏れ試験記録："+path);
                return "漏れ判定："+record.Summary;
            }
            finally
            {
                _leakRunning=false;_leakCancel?.Dispose();_leakCancel=null;
                // 結果を出さずに終わった(途中の異常・終了要求)ときは、「合格」を出したまま残さずすぐ閉じる。
                if(window is { IsDisposed:false,ResultShown:false })window.Close();
            }
        });
        if(_closeAfterLeak && !IsDisposed) BeginInvoke((Action)Close);
    }

    // 試験中の読み出し 1 回。判定画面を更新し、不合格・判定できないが決まったら打ち切る(true)。
    private bool PollLeak(LeakMonitor? monitor,LeakJudgeWindow? window,TimeSpan elapsed)
    {
        if(monitor is null)return false;
        bool stop=monitor.Poll(elapsed);
        var judge=monitor.Judge;
        window?.ShowValues(judge.Latest,judge.Maximum,judge.Difference,elapsed);
        if(judge.Decided is LeakOutcome decided)
        {
            window?.ShowResult(decided);
            window?.CloseAfterResult();
            AppendLog($"漏れ判定：{LeakOutcomeText.Of(decided)} / {judge.Problem}（{elapsed.TotalSeconds:F2} 秒、{judge.Points} 点）");
        }
        return stop;
    }

    // 最後の漏れ試験の結果を短く(状態表示・次にすること用)。
    private string LastLeakText()
    {
        if(_flow.LastLeakOutcome is not LeakOutcome outcome)return "";
        if(_lastLeakResult is { } r && r.Outcome==outcome)return r.Summary;
        return LeakOutcomeText.Of(outcome);
    }

    // 不合格・判定できなかった後に、人の判断で合格にするか聞く(設定で回避を許したときだけ呼ぶ)。合格にするなら記録して true。
    private bool ConfirmLeakOverride()
    {
        string result=LastLeakText();
        UseWaitCursor=false;
        DialogResult answer;
        try
        {
            answer=ErrorNotification.Show(this,
                $"漏れ判定は{LeakOutcomeText.Of(_flow.LastLeakOutcome ?? LeakOutcome.Fail)}です。\n{result}\n\nそれでも合格にしますか？\n「はい」：人の判断で合格にする（漏れ試験記録に残します）\n「いいえ」：合格にしない（「+30mA」から漏れ試験をやり直せます）",
                "漏れ判定：人の判断で合格",MessageBoxButtons.YesNo,MessageBoxIcon.Warning);
        }
        finally { UseWaitCursor=true; }
        if(answer!=DialogResult.Yes)return false;
        var last=_lastLeakResult ?? new LeakTestResult(DateTime.Now,_flow.LastLeakOutcome ?? LeakOutcome.Fail,0,double.NaN,double.NaN,double.NaN,double.NaN,0,"");
        try
        {
            string path=LeakTestRecordFile.AppendOverride(_rootBox.Text,_flow.Serial,_flow.Model.Name,last,DateTime.Now);
            AppendLog("漏れ試験記録（人の判断で合格）："+path);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new IOException($"漏れ試験記録を書けないため合格にしませんでした。保存先を確認してください。\n{LeakTestRecordFile.PathOf(_rootBox.Text)}\n{ex.Message}",ex);
        }
        return true;
    }
}
