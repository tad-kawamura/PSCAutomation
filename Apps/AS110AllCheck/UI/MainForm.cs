using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PSCAutomation.Apps.AS110AllCheck;

// MainFormを役割別に分割。すべて同じFormの一部です。
internal sealed partial class MainForm : Form
{
    // 別アプリで雛形操作をデバッグするときは true にしてGUIを再表示します。


    private readonly WaveLoggerSession _logger = new() { ObserveOperation = name => PcDelayObservation.Begin(name) };

    private readonly As110ArduinoProtocol _arduino = new();
    private IFunctionGeneratorSession? _generator;
    private readonly System.Windows.Forms.Timer _statusHeartbeat = new() { Interval = 1000 };

    public MainForm()
    {
        // LOGGER 本体ソフトの起動(約 7 秒)だけ先に始め、画面準備・発振器確認と重ねる。
        BeginLoggerStartup();
        _nr500Connection = new Nr500Connection(_logger);
        _nrXConnection = new NrXConnection(_logger, _wifiManager);
        _arduino.Disconnected += ArduinoDisconnected;
        _arduino.MeasurementRequested += ArduinoMeasurementRequested;
        _arduino.ButtonRequested += BoardButtonRequested;
        _arduino.MeasurementBlocked += ShowNfNotEnergizedAlert;
        _arduino.NfStatusChanged += ArduinoNfStatusChanged;
        _statusHeartbeat.Tick += StatusHeartbeatTick;
        _statusHeartbeat.Start();

        Text = Application.ProductName + " / 通電確認・測定アプリ";
        // 操作欄(型式・S/N・保存先の行まで)がログ欄の上に収まる高さにする。足りないときは操作欄だけがスクロールする。
        // 幅はスタンド選択の 3 つのボタン(220×3)が 1 行に並ぶ最小に近い値。
        ClientSize = new Size(760, 800);
        MinimumSize = new Size(680, 570);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        ConfigureLoggerSelection();
        ConfigureConnectionSelection();
        UpdateControls();
        StartPcDelayObservation();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await CheckConnectionsAsync();
    }


}
