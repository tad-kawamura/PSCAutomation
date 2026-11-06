namespace PSCAutomation.Apps.AS110AllCheck;

internal sealed partial class MainForm : Form
{
    private readonly CheckBox _developerMode = new() { Text = "開発者モード（接点模擬）", AutoSize = true, Margin = new Padding(24, 5, 0, 0) };
    private readonly Button _developerContact = new() { AutoSize = true };
    private bool _simulatedContactEnergized;
    private bool MotionContactEnergized => _flow.Mode == TestMode.Motion && _developerMode.Checked ? _simulatedContactEnergized : _arduino.NfEnergized;

    private void BuildDeveloperContactControls(FlowLayoutPanel panel)
    {
        _developerMode.CheckedChanged += (_, _) =>
        {
            _simulatedContactEnergized = false;
            AppendLog(_developerMode.Checked ? "開発者モード：外部接点を模擬（非通電）" : "開発者モード終了：実接点を使用");
            UpdateControls();
        };
        _developerContact.Click += (_, _) =>
        {
            _simulatedContactEnergized = !_simulatedContactEnergized;
            AppendLog("開発者モード：外部接点を模擬／" + (_simulatedContactEnergized ? "通電" : "非通電"));
            UpdateControls();
        };
        panel.Controls.AddRange([_developerMode, _developerContact]);
    }

    private void UpdateDeveloperContactControls()
    {
        bool idle = !_operationInProgress && !_flow.Measuring && !_leakRunning;
        if (_flow.Mode != TestMode.Motion && _developerMode.Checked) _developerMode.Checked = false;
        _developerMode.Enabled = idle && _flow.Mode == TestMode.Motion;
        _developerContact.Visible = _developerMode.Checked && _flow.Mode == TestMode.Motion;
        _developerContact.Enabled = idle;
        _developerContact.Text = "模擬接点：" + (_simulatedContactEnergized ? "通電 → 非通電にする" : "非通電 → 通電にする");
        if (_flow.Mode == TestMode.Motion && _developerMode.Checked)
            _flowLabel.Text += "／開発者・模擬接点 " + (_simulatedContactEnergized ? "通電" : "非通電");
    }
}
