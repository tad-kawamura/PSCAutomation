using System.Text.Json;
using PSCAutomation.Wrappers.Excel;
using PSCAutomation.Wrappers.LoggerFiles;

namespace PSCAutomation.Apps.XdtConversion;

// 元: XdtConversion XdtConversion/MainForm.cs。変更は namespace と using(設定の保存先 %LOCALAPPDATA%\XdtConversion も元のまま)と、
// Excel の表示を共通部品 ExcelViewer に乗り換えたこと(2026-11-04。元はアプリ内の ExcelPresenter)。

public sealed class MainForm : Form
{
    private readonly TextBox pcFile = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly TextBox crFile = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly TextBox output = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown stride = new() { Minimum = 1, Maximum = 1000000, Value = 10, Width = 100 };
    private readonly DataGridView grid = new() { Dock = DockStyle.Fill, AllowUserToAddRows = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersWidth = 30 };
    private readonly TextBox log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly Button run = new() { Text = "変換・保存 → Excel表示", AutoSize = true };
    private readonly Label status = new() { Text = "XDTファイルを選択してください。", AutoSize = true };
    private readonly string template = Path.Combine(AppContext.BaseDirectory, "雛形", "雛形.xlsx");
    private readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XdtConversion", "settings.json");
    private readonly TableLayoutPanel root;
    private bool busy;

    public MainForm()
    {
        Text = "XdtConversion — XDT直接変換";
        ClientSize = new Size(980, 700); MinimumSize = new Size(840, 600);
        Font = new Font("Yu Gothic UI", 10);
        StartPosition = FormStartPosition.CenterScreen;
        root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 9 };
        foreach (int size in new[] { 30, 42, 42, 42, 42, 30 }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, size));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        root.Controls.Add(new Label { Text = "雛形：" + template, AutoSize = true }, 0, 0);
        root.Controls.Add(FileRow("PC用 XDT", pcFile, () => SelectXdt(pcFile, "PC")), 0, 1);
        root.Controls.Add(FileRow("CR用 XDT", crFile, () => SelectXdt(crFile, "CR")), 0, 2);
        root.Controls.Add(FileRow("別名保存先", output, SelectOutput), 0, 3);
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill };
        options.Controls.Add(new Label { Text = "間引き数", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
        options.Controls.Add(stride);
        options.Controls.Add(new Label { Text = "1＝全点。10＝先頭、11点目、21点目…を転送。", AutoSize = true, Padding = new Padding(10, 5, 0, 0) });
        root.Controls.Add(options, 0, 4);
        root.Controls.Add(new Label { Text = "転送設定（チェックした行を転送。開始セルから下の列は測定値専用として使用）", AutoSize = true }, 0, 5);
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Enabled", HeaderText = "転送", FillWeight = 45 });
        grid.Columns.Add(new DataGridViewComboBoxColumn { Name = "Source", HeaderText = "入力", DataSource = new[] { "PC", "CR" }, FillWeight = 60 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Channel", HeaderText = "CH番号", FillWeight = 65 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Sheet", HeaderText = "シート名", FillWeight = 110 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "StartCell", HeaderText = "開始セル", FillWeight = 65 });
        grid.Rows.Add(true, "PC", 5, "Sheet1", "A6");
        grid.Rows.Add(true, "PC", 6, "Sheet1", "B6");
        grid.Rows.Add(true, "CR", 5, "Sheet1", "H6");
        grid.Rows.Add(true, "CR", 6, "Sheet1", "I6");
        root.Controls.Add(grid, 0, 6);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
        actions.Controls.Add(run); actions.Controls.Add(status);
        run.Click += async (_, _) => await RunAsync();
        root.Controls.Add(actions, 0, 7); root.Controls.Add(log, 0, 8);
        Controls.Add(root);
        LoadSettings();
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; return; } SaveSettings(); };
        Shown += (_, _) =>
        {
            try { Append("雛形のシート：" + string.Join(" / ", TemplateWriter.GetSheets(template))); }
            catch (Exception ex) { Error(ex.Message); }
        };
    }

    private Control FileRow(string title, TextBox box, Action action)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        panel.Controls.Add(new Label { Text = title, AutoSize = true, Padding = new Padding(0, 4, 0, 0) });
        panel.Controls.Add(box);
        var button = new Button { Text = "選択…", Dock = DockStyle.Fill };
        button.Click += (_, _) => action(); panel.Controls.Add(button);
        if (box.ReadOnly)
        {
            box.AllowDrop = true;
            box.DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
            box.DragDrop += (_, e) =>
            {
                if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length == 1) SetFile(box, files[0], box == pcFile ? "PC" : "CR");
            };
        }
        return panel;
    }
    private void SelectXdt(TextBox box, string source)
    {
        using var dialog = new OpenFileDialog { Filter = "XDTファイル (*.xdt)|*.xdt", Title = source + "用の保存済みXDTを選択" };
        if (dialog.ShowDialog(this) == DialogResult.OK) SetFile(box, dialog.FileName, source);
    }
    private void SetFile(TextBox box, string path, string source)
    {
        try
        {
            var info = XdtReader.Inspect(path);
            box.Text = Path.GetFullPath(path);
            Append($"{source}：{info.Format} / {info.Samples:N0}点 / 使用CH：{string.Join(", ", info.Channels.Select(c => c.Number))}");
            foreach (var ch in info.Channels) Append($"  CH{ch.Number}：移動平均 {(ch.MovingAverage && ch.Window > 1 ? ch.Window + "点" : "なし")}");
            status.Text = "転送設定を確認して実行してください。";
            if (string.IsNullOrWhiteSpace(output.Text)) output.Text = SuggestedOutput(path);
        }
        catch (Exception ex) { Error(ex.Message); }
    }
    private static string SuggestedOutput(string path) => Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "_変換_" + DateTime.Now.ToString("yyyyMMdd_HHmmssfff") + ".xlsx");
    private void SelectOutput()
    {
        using var dialog = new SaveFileDialog { Filter = "Excelブック (*.xlsx)|*.xlsx", FileName = string.IsNullOrWhiteSpace(output.Text) ? "XDT変換.xlsx" : output.Text, OverwritePrompt = false };
        if (dialog.ShowDialog(this) == DialogResult.OK) output.Text = dialog.FileName;
    }
    private List<Mapping> ReadMappings()
    {
        grid.EndEdit();
        var mappings = new List<Mapping>();
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.IsNewRow || row.Cells["Enabled"].Value is not true) continue;
            string source = row.Cells["Source"].Value?.ToString() ?? "";
            string sheet = row.Cells["Sheet"].Value?.ToString()?.Trim() ?? "";
            string cell = row.Cells["StartCell"].Value?.ToString()?.Trim().ToUpperInvariant() ?? "";
            if (!int.TryParse(row.Cells["Channel"].Value?.ToString(), out int channel) || channel < 1 || sheet.Length == 0 || source.Length == 0)
                throw new ArgumentException($"転送設定の{row.Index + 1}行目を確認してください。");
            TemplateWriter.ParseCell(cell);
            mappings.Add(new(source, channel, sheet, cell));
        }
        if (mappings.Count == 0) throw new ArgumentException("転送する行にチェックを入れてください。");
        return mappings;
    }
    private const string ExcelViewerFailure = "Excelの起動・表示処理に失敗しました。";
    private async Task RunAsync()
    {
        if (busy) return;
        bool saved = false;
        try
        {
            var mappings = ReadMappings();
            string destination = Path.GetFullPath(output.Text.Trim());
            if (!destination.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("保存先は.xlsxにしてください。");
            if (!File.Exists(template)) throw new FileNotFoundException("雛形/雛形.xlsxがありません。", template);
            if (File.Exists(destination)) throw new IOException("保存先に同名ファイルがあります。別名を指定してください。");
            var sources = new Dictionary<string, string> { ["PC"] = pcFile.Text, ["CR"] = crFile.Text };
            int step = (int)stride.Value;
            SaveSettings(); busy = true; root.Enabled = false;
            status.Text = "変換・保存中…";
            var report = await Task.Run(() => ConversionService.Convert(template, destination, sources, mappings, step));
            saved = true;
            foreach (var detail in report.Details) Append(detail);
            Append($"① 変換・保存時間：{report.Total.TotalSeconds:F3} 秒");
            Append($"   XDT読込・換算・移動平均・間引き：{report.ReadConvert.TotalSeconds:F3} 秒 / 雛形への書込・別名保存：{report.Save.TotalSeconds:F3} 秒");
            Append("保存完了：" + destination);
            status.Text = "Excel起動・表示中…";
            TimeSpan display;
            try
            {
                display = await ExcelViewer.OpenForUserAsync(destination, message =>
                {
                    // Excel専用スレッドからGUIへ途中経過を渡す。
                    if (!IsDisposed && IsHandleCreated)
                        BeginInvoke(new Action(() => { Append(message); status.Text = message; }));
                });
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith(ExcelViewerFailure, StringComparison.Ordinal))
            {
                // 共通部品のメッセージに、元の ExcelPresenter にあったこのアプリ固有の一文を戻す(表示される文は乗り換え前と同じ)。
                throw new InvalidOperationException(
                    ExcelViewerFailure + "変換済みのExcelファイルは保存されています。" + ex.Message[ExcelViewerFailure.Length..], ex);
            }
            Append($"② Excel起動・表示時間：{display.TotalSeconds:F3} 秒（Excel起動・ブック読込・自動計算設定・表示要求まで。計算完了は監視しません）");
            status.Text = "転送・表示完了";
        }
        catch (Exception ex)
        {
            status.Text = saved ? "変換保存済み / Excel表示失敗" : "停止";
            Append((saved ? "② Excel起動・表示：失敗（時間は未確定） " : "エラー：") + ex.Message);
            Error(ex.Message);
        }
        finally { busy = false; root.Enabled = true; }
    }
    private void Append(string message) => log.AppendText(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
    private void Error(string message)
    {
        using var owner = new Form { TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterScreen, Size = new Size(1, 1), Opacity = 0 };
        owner.Show(); MessageBox.Show(owner, message, "XdtConversion", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
    private sealed class Settings
    {
        public Settings() { }
        public int Stride { get; set; } = 10;
        public List<Mapping> Mappings { get; set; } = new();
    }
    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(settingsPath)) return;
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath));
            if (settings == null) return;
            stride.Value = Math.Clamp(settings.Stride, (int)stride.Minimum, (int)stride.Maximum);
            if (settings.Mappings.Count == 0) return;
            grid.Rows.Clear();
            foreach (var m in settings.Mappings) grid.Rows.Add(true, m.Source, m.Channel, m.Sheet, m.StartCell);
        }
        catch (Exception ex) { Append("設定読込をスキップ：" + ex.Message); }
    }
    private void SaveSettings()
    {
        try
        {
            var mappings = ReadMappings();
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(new Settings { Stride = (int)stride.Value, Mappings = mappings }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Append("設定保存をスキップ：" + ex.Message); }
    }
}
