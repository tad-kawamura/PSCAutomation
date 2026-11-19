namespace PSCAutomation.Wrappers.Instruments;

// SetDcOutput で DC を出したあと、出力を切らずに電圧だけ変える(2026-11-19 に足した。AS110AllCheck の校正で電流を 30 mA に合わせるため)。
// SetDcOutput は毎回 出力 OFF→ON し、上限が 5 V(元のまま変えない)。こちらは本体の範囲(DC・負荷 High-Z で ±10 V)まで受け付ける。
// アプリ固有の上限(例: AS110 の 5.5 V)はアプリ側で決めて守る。
// IFunctionGeneratorSession に足すと、それを実装しているアプリの模擬が全部書き換えになるので、別の interface にした。
// 今は WF1973(Wf1973BurstSession)だけが実装する。SDG1032X は実機で確かめていないので実装しない。
public interface IDcOutputAdjustable
{
    // 本体が受け付ける DC の電圧の範囲(±V)。
    double DcAdjustLimitVolts { get; }

    // SetDcOutput のあと(DC を出している間)だけ使える。それ以外(バーストの準備後・出力停止後)は InvalidOperationException。
    // 範囲外は ArgumentOutOfRangeException。本体がエラーを返したら InvalidOperationException。
    void AdjustDcOutput(double volts);
}
