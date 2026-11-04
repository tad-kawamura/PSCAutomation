namespace PSCAutomation.Apps.AS110NoiseTest.Measurement;

// 測定シーケンスが使う LOGGER の操作。本番は WaveLoggerDevice(WAVE LOGGER COM)、
// 確認ツールでは機器なしの模擬(Tools/MeasurementCheck/SimulatedLogger)に差し替える。
// 戻り値の long はメーカー API の戻り値そのまま(0 が成功)。状態番号は LoggerMeasurementState と同じ
// (1 停止 / 2 開始待ち / 3 トリガ待ち / 4,5 測定中 / 10 読み出し中 / 11 エラー停止 / 12 残処理待ち)。
internal interface ILoggerDevice
{
    // ログ表示用(機種・接続方法)。
    string Description { get; }
    long OpenFile(string path);
    long CloseActiveFile();
    long StartSingleAcquisition();
    long GetState();
    void Stop();
    long SaveXdt(string path);
}

internal static class LoggerState
{
    public const long Stopped = 1;
    public const long ErrorStopped = 11;

    // 次の記録を始められる状態(AS110AllCheck の LoggerMeasurementState.ReadyToStart と同じ)。
    public static bool IsReadyToStart(long state) => state is Stopped or ErrorStopped;

    public static string Describe(long state) => state switch
    {
        1 => "停止",
        2 => "開始待ち",
        3 => "トリガ待ち",
        4 or 5 => "測定中",
        6 => "プリトリガ",
        7 => "ポストトリガ",
        9 => "ゼロ点調整中",
        10 => "収集データ読み出し中(本体停止)",
        11 => "エラー停止",
        12 => "本体停止後の残処理待ち",
        _ => $"不明(GetState={state})",
    };
}
