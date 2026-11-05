using System;
using System.Windows.Forms;

namespace PSCAutomation.Apps.AS110AllCheck;

internal sealed partial class MainForm : Form
{
    private IDisposable? _pcDelayObserver;
    private readonly System.Windows.Forms.Timer _observationUiTimer = new() { Interval = 500 };

    private void StartPcDelayObservation()
    {
        try
        {
            _observationUiTimer.Tick += ObservationUiTick;
            _pcDelayObserver = PcDelayObservation.Start(ArduinoDiagnosticLog.Write);
            _observationUiTimer.Start();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine("PC遅延観測を開始できません: " + ex);
        }
    }

    private void ObservationUiTick(object? sender, EventArgs e) => PcDelayObservation.UiTick();

    private void StopPcDelayObservation()
    {
        _observationUiTimer.Stop();
        _observationUiTimer.Dispose();
        _pcDelayObserver?.Dispose();
    }
}
