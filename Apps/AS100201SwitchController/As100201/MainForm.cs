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
    private string? _lastArduinoStatus;
    private readonly As100201ArduinoProtocol _arduino = new();
    private Wf1973ContinuousSession? _wf1973;
    private readonly System.Windows.Forms.Timer _readyHeartbeat = new() { Interval = 500 };
    private readonly Button _deleteTemplateButton = new();
    private readonly Label _arduinoValueLabel = new();
    private readonly Label _wfValueLabel = new();
    private readonly Label _templateValueLabel = new();
    private readonly Label _readyValueLabel = new();
    private readonly Panel _dropPanel = new();
    private bool _templateLoaded;
    private bool _readyLedOn;
    private int _heartbeatCount;
    private string? _templatePath;
    private readonly Button _initializeButton = new();
    private readonly Button _startButton = new();
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
    private As100201Switch? _pendingWaveformSwitch;
    private int _waveformRequestVersion;
    private string _detectedHardwareName = string.Empty;
    // 2 回押しの待ち時間と位相(試用版、画面で変えて設定ファイルに残す)。
    private readonly As100201SwitchSettingsStore _switchSettingsStore = As100201SwitchSettingsStore.CreateDefault();
    private As100201SwitchSettings _switchSettings;

    public MainForm()
    {
        _nr500Connection = new Nr500Connection(_logger);
        _switchSettings = _switchSettingsStore.Load(out string? settingsMessage);
        _arduino.SetDoubleWait(_switchSettings.DoubleWaitMilliseconds);
        _arduino.Disconnected += ArduinoDisconnected;
        _arduino.SwitchPressed += ArduinoSwitchPressed;
        _arduino.StatusChanged += ArduinoStatusChanged;
        _arduino.DoubleWaitApplied += ArduinoDoubleWaitApplied;
        _readyHeartbeat.Tick += StatusHeartbeatTick;
        StartPcDelayObservation();
        _readyHeartbeat.Start();

        Text = As100201Application.WindowTitle;
        ClientSize = new Size(850, 820);
        MinimumSize = new Size(680, 680);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        ShowSwitchSettings();
        ConfigureLoggerSelection();
        if (settingsMessage is not null) AppendLog(settingsMessage);
        UpdateControls();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await CheckConnectionsAsync();
    }

    private bool ConnectionsReady => _arduino.InputReady && _wf1973 is not null &&
        _logger.IsInitialized && _hardwareDetected;

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closing = true;
        ++_waveformRequestVersion;
        _pendingWaveformSwitch = null;
        StopPcDelayObservation();
        _readyHeartbeat.Stop();
        _readyHeartbeat.Dispose();
        try { if (_arduino.IsConnected) _arduino.SetReadyLed(false); } catch { }
        _arduino.Dispose();
        _wf1973?.Dispose();
        _logger.Release();

        base.OnFormClosed(e);
    }
}
