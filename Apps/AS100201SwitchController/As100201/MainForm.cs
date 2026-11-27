using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PSCAutomation.Apps.AS100201SwitchController;

internal sealed partial class MainForm : Form
{
    private readonly WaveLoggerSession _logger = new()
    {
        Lifecycle = WaveLoggerLifecycle.ReferenceOnly,
        ObserveOperation = name => PcDelayObservation.Begin(name)
    };
    private readonly LoggerMeasurementState _measurementState = new();
    private bool _measurementStarted
    {
        get => _measurementState.MeasurementStarted;
        set => _measurementState.MeasurementStarted = value;
    }
    private bool _closing, _heartbeatBusy;
    // 版 6(2026-11-27、人の決定): Arduino のスイッチ箱は使わない。スイッチはタッチパネル(Tab5)にまとめ、アプリは Arduino を探さない。
    private Wf1973ContinuousSession? _wf1973;
    private readonly System.Windows.Forms.Timer _readyHeartbeat = new() { Interval = 500 };
    private readonly Button _deleteTemplateButton = new();
    private readonly Label _wfValueLabel = new();
    private readonly Label _templateValueLabel = new();
    private readonly Label _readyValueLabel = new();
    private readonly Panel _dropPanel = new();
    private bool _templateLoaded;
    // 測定を始められる(前の READY LED が点く条件)。版 6 で LED はなくなり、PC の画面と Tab5 に出す。
    private bool _readyToMeasure;
    private int _heartbeatCount;
    private string? _templatePath;
    private readonly Button _initializeButton = new();
    private readonly Button _startButton = new();
    // 版 6: Tab5 が切れたとき(電池切れ・Wi-Fi 切れなど)にも止められるよう、いつも押せる「測定停止」。
    private readonly Button _stopButton = new();
    private readonly Button _getStateButton = new();
    private readonly ComboBox _loggerComboBox = new();
    private readonly Label _loggerSelectionValueLabel = new();
    private readonly Label _statusValueLabel = new();
    private readonly Label _hardwareValueLabel = new();
    private readonly TextBox _logTextBox = new();
    private IReadOnlyList<WaveLoggerEdition> _installedEditions = [];
    private readonly Nr500Connection _nr500Connection;
    private bool _hardwareDetected;
    private bool _operationInProgress;
    private bool _waveformRestartInProgress;
    private int _waveformRequestVersion;
    private string _detectedHardwareName = string.Empty;
    // 位相 4 つ(Tab5 の PC 位相1・2、CR 位相1・2)。画面で変えて設定ファイルに残す。
    private readonly As100201SwitchSettingsStore _switchSettingsStore = As100201SwitchSettingsStore.CreateDefault();
    private As100201SwitchSettings _switchSettings;

    // mode: 起動時に選んだモード(通常 / 学習)。通常モードでは学習モードの部品を作らず、今までと同じ動き。
    // virtualPanel: 開発用。学習モードでタッチパネルの代わりを画面に出す(--virtual-panel のときだけ)。
    public MainForm(As100201StartupMode mode, bool virtualPanel = false)
    {
        _mode = mode;
        _useVirtualPanel = virtualPanel && mode == As100201StartupMode.Learning;
        _nr500Connection = new Nr500Connection(_logger);
        _switchSettings = _switchSettingsStore.Load(out string? settingsMessage);
        _readyHeartbeat.Tick += StatusHeartbeatTick;
        StartPcDelayObservation();
        _readyHeartbeat.Start();

        Text = As100201Application.GetWindowTitle(mode) + (_useVirtualPanel ? As100201Application.VirtualPanelTitleSuffix : "");
        ClientSize = new Size(850, 820);
        MinimumSize = new Size(680, 680);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        BuildLearningUi();
        ShowSwitchSettings();
        ConfigureLoggerSelection();
        if (settingsMessage is not null) AppendLog(settingsMessage);
        InitializePanel();
        InitializeLearningMode();
        UpdateControls();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await CheckConnectionsAsync();
    }

    // 発振器と LOGGER(NR 本体)。タッチパネルは別(PanelReady)。止めるのはパネルが切れていてもできるようにするため。
    private bool ConnectionsReady => _wf1973 is not null && _logger.IsInitialized && _hardwareDetected;

    // タッチパネル(Tab5)の定期応答が安定している(版 6 からは通常モードでも要る)。
    private bool PanelReady => _panel is { InputReady: true };

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closing = true;
        ++_waveformRequestVersion;
        _panelSequenceCancel?.Cancel();
        StopPcDelayObservation();
        _readyHeartbeat.Stop();
        _readyHeartbeat.Dispose();
        DisposeLearningMode();
        _wf1973?.Dispose();
        _logger.Release();

        base.OnFormClosed(e);
    }
}
