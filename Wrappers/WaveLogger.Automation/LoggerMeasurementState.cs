namespace PSCAutomation.Wrappers.WaveLogger.Automation;

// LOGGERの状態と、開始直後の古い停止情報を避ける待ち時間を管理する。
// COM操作は呼び出し元のUIスレッド上で行う。

// LOGGERの測定状態を管理する。
// 取得した状態から、測定開始可能か・測定が終了したかを判断する。
// 状態番号を「測定中」「停止」などの表示文字に変換する。
// 開始直後の古い停止情報を誤認しないよう、状態確認の待ち時間も管理する。
public sealed class LoggerMeasurementState
{
    public bool MeasurementStarted { get; set; }
    public long? State { get; set; }
    public long ReadAfter { get; set; }
    public bool ErrorLogged { get; set; }
    public bool ReadyToStart => State is 1 or 11;

    public bool Refresh(Func<long> readState, Action<string> writeLog,
        bool allowManualTriggerWait = false)
    {
        if (Environment.TickCount64 < ReadAfter) return false;
        long state;
        try
        {
            state = readState();
            if (state <= 0)
                throw new InvalidOperationException($"GetState() = {state}");
        }
        catch
        {
            State = null;
            throw;
        }
        long? previousState = State;
        bool wasStarted = MeasurementStarted;
        State = state;
        ErrorLogged = false;
        // 10（データ読み出し）・12（残処理待ち）でも再開始しない。
        bool ready = ReadyToStart || (allowManualTriggerWait && state == 3);
        MeasurementStarted = !ready;
        bool completed = wasStarted && (state == 1 ||
            (allowManualTriggerWait && state == 3));
        if (state == 11 && previousState != 11)
            writeLog("WAVE LOGGER エラー停止（GetState=11）。LOGGER側のエラー内容を確認してください。");
        return completed;
    }

    public string DisplayText => GetDisplayText();

    public string GetDisplayText(bool describeHardwareStop = true) => State switch
    {
        1 => "停止",
        2 => "開始待ち",
        3 => "トリガ待ち",
        4 or 5 => "測定中",
        6 => "プリトリガ",
        7 => "ポストトリガ",
        9 => "ゼロ点調整中",
        10 => describeHardwareStop ? "収集データ読み出し中（本体停止）" : "収集データ読み出し中",
        11 => "エラー停止",
        12 => "本体停止後の残処理待ち",
        null => "状態未確認",
        _ => $"開始できない状態（GetState={State}）"
    };
}
