namespace PSCAutomation.Apps.AS100201SwitchController;

// 画面上部の「次にすること」(2026-11-26、人の要望)。準備完了 LED が消えている理由と直し方を大きい文字で出す。
// 中身の決まりは As100201NextStep。ここは今の状態を集めて表示するだけ。
internal sealed partial class MainForm : Form
{
    private readonly Label _nextStepLabel = new();

    private Label BuildNextStepLabel()
    {
        _nextStepLabel.AutoSize = false;
        _nextStepLabel.Dock = DockStyle.Top;
        _nextStepLabel.Font = new Font(Font.FontFamily, 14, FontStyle.Bold);
        _nextStepLabel.Padding = new Padding(18, 6, 18, 6);
        _nextStepLabel.TextAlign = ContentAlignment.MiddleLeft;
        _nextStepLabel.SizeChanged += (_, _) => FitNextStepLabel();
        return _nextStepLabel;
    }

    private void UpdateNextStep()
    {
        var (text, kind) = As100201NextStep.Describe(new As100201NextStepState
        {
            Learning = IsLearningMode,
            CheckingConnections = _checkingConnections,
            LoggerAppMissing = _installedEditions.Count == 0,
            LoggerReady = _logger.IsInitialized && _hardwareDetected,
            ArduinoReady = _arduino.InputReady,
            Wf1973Ready = _wf1973 is not null,
            TemplateLoaded = _templateLoaded,
            PanelReady = _panel is { InputReady: true },
            SaveBlocked = _learning is { Blocked: true },
            ReferenceLoaded = _referenceLoaded,
            ReferenceProblem = _referenceProblem,
            SerialMissing = _learning is { SerialNumber: null },
            SerialDialogOpen = _serialDialogOpen,
            PanelSelecting = _panelSelecting,
            Busy = _operationInProgress || _waveformRestartInProgress,
            Measuring = _measurementStarted,
            ReadyLedOn = _readyLedOn
        });
        text = As100201NextStep.Prefix + text;
        (Color fore, Color back) = kind switch
        {
            As100201NextStepKind.Ready => (Color.DarkGreen, Color.Honeydew),
            As100201NextStepKind.Wait => (Color.Navy, Color.AliceBlue),
            _ => (Color.DarkRed, Color.MistyRose)
        };
        if (_nextStepLabel.Text != text) _nextStepLabel.Text = text;
        _nextStepLabel.ForeColor = fore;
        _nextStepLabel.BackColor = back;
        FitNextStepLabel();
    }

    // 窓の幅で折り返した高さに合わせる(長い文でも切れないように)。
    private void FitNextStepLabel()
    {
        int width = Math.Max(100, _nextStepLabel.Width - _nextStepLabel.Padding.Horizontal);
        Size text = TextRenderer.MeasureText(_nextStepLabel.Text, _nextStepLabel.Font, new Size(width, 0),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        int height = text.Height + _nextStepLabel.Padding.Vertical + 4;
        if (_nextStepLabel.Height != height) _nextStepLabel.Height = height;
    }
}
