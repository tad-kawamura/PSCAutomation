namespace PSCAutomation.Wrappers.Instruments;

// VISA で接続した 1 台の機器との通信(コマンドの送受信と切断だけ)。
// 元: AS100201SwitchController Generator/VisaInstrumentSession.cs の IInstrumentSession。
// モノレポで Address・TimeoutMilliseconds・Read を足した(AS110NoiseTest の 7351E が応答なしの読み出しを使うため、
// AS110AllCheck の発振器が切断前にタイムアウトを短くするため)。
// 機器クラスはこの interface を受け取るので、模擬に差し替えて機器なしで確かめられる。
public interface IInstrumentSession : IDisposable
{
    // VISA リソース名(例: USB0::0x0D4A::0x000D::1234::INSTR)。
    string Address { get; }
    // 接続時の *IDN? の応答(前後の空白を除いたもの)。
    string Identity { get; }
    // 送受信のタイムアウト(ms)。
    int TimeoutMilliseconds { get; set; }
    // コマンドを 1 行送る(改行付き)。
    void Write(string command);
    // 1 行読む(前後の空白を除く)。
    string Read();
    // コマンドを送って 1 行読む(前後の空白を除く)。
    string Query(string command);
}
