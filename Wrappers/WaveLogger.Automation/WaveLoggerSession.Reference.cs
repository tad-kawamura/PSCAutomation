using System.Diagnostics;

namespace PSCAutomation.Wrappers.WaveLogger.Automation;

public sealed partial class WaveLoggerSession
{
    /// <summary>
    /// 参照波形の読み込み操作を実行する。X版のトリガ待ちは停止してから操作する。
    /// 停止した場合は読み込み操作の前にstoppedを通知する。波形追加の成否は画面で確認する。
    /// </summary>
    public async Task LoadReferenceAsync(string path, long? state, Action<string> log, Action? stopped = null)
    {
        if (!(state is 1 or 11 || (ActiveEdition == WaveLoggerEdition.WaveLoggerX && state == 3)))
            throw new InvalidOperationException(
                $"LOGGERが停止してから参照波形を選択してください（GetState={state}）。");

        if (ActiveEdition == WaveLoggerEdition.WaveLoggerX && state == 3)
        {
            log("参照波形：LOGGER Xのトリガ待ちを停止してから読み込みます。");
            ResetMeasurementStart();
            Stop();
            var timer = Stopwatch.StartNew();
            long current;
            do
            {
                await Task.Delay(100);
                current = GetState();
                if (current <= 0 || current == 11)
                    throw new InvalidOperationException(
                        $"参照波形読み込み前の停止に失敗しました（GetState={current}）。");
                if (timer.Elapsed >= TimeSpan.FromSeconds(10))
                    throw new TimeoutException(
                        $"参照波形読み込み前にLOGGERが停止しません（GetState={current}）。");
            } while (current != 1);
            stopped?.Invoke();
            log("参照波形：LOGGER X停止確認（GetState=1）。");
        }

        if (!File.Exists(path)) throw new FileNotFoundException("参照波形が見つかりません。", path);
        log("参照波形ファイル：" + path);
        string moduleName = GetLoggerModuleName();
        if (ActiveEdition == WaveLoggerEdition.Legacy)
            await LoggerReferenceLoader.OpenAsync(moduleName, path, log);
        else
            LoggerFileDrop.Post(moduleName, path, log);
        log("参照波形：現在の雛形へ追加されたかLOGGER画面で確認してください。");
    }

    /// <summary>メニューから横軸を拡大する。各操作完了時に呼び出し元へ通知する。</summary>
    public Task ZoomHorizontalAsync(int steps, Action<string> log, Action stepCompleted) =>
        LoggerHorizontalZoom.ZoomInAsync(GetLoggerModuleName(), steps, log, stepCompleted);
}
