using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PSCAutomation.Apps.AS110AllCheck;

// MainFormを役割別に分割。すべて同じFormの一部です。
// MainFormのうち、雛形と参照波形の選択・読み込みを担当する。
// 雛形フォルダから.xdtを探し、LOGGERへ読み込む。
// 参照波形.xybの候補一覧と表示名を作り、選択された波形を読み込む。
// LOGGER側でファイルが開かれた・閉じられた状態を画面へ反映する。
// 「雛形を削除」はLOGGER内のファイルを閉じる操作で、元ファイルは消さない。
internal sealed partial class MainForm : Form
{
    private static string TemplateFolderPath => Path.Combine(AppContext.BaseDirectory, "雛形");
    private bool _templateStateErrorLogged;
    private bool _templateLoaded;
    private string? _selectedReferencePath;

    private void TemplateDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = DragDropEffects.None;
    }

    private void TemplateDragDrop(object? sender, DragEventArgs e)
    {
        AppendLog("雛形は校正値の適用時に読み込みます。校正値を入力してください。");
    }

    // データ取りは型式でも選ぶ。測る時間が違う型式(AS110-006)は型式専用の雛形(「…_AS110-006」。MeasurementProfile)。データ調整は共用の雛形。
    private string GetAutomaticTemplatePath(string extension, ProductModel model)
    {
        string folder = TemplateFolderPath;
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException(
                "「雛形」フォルダがありません。exeと同じ場所に作成してください。" +
                Environment.NewLine + folder);

        string fileName = (_flow.Mode == TestMode.Motion
            ? (extension == ".ydt" ? "AS110動き検査_NRX100" : "AS110動き検査用")
            : MeasurementProfile.LoggerTemplateName(
                extension == ".ydt" ? "AS110データ調整・データ取り_NRX100" : "AS110データ調整・データ取り用", model, _flow.Mode)) + extension;
        // 用途と本体の両方で選ぶ。別用途の雛形へはフォールバックしない(型式専用の雛形が無いときも共用の雛形では測らない)。
        string[] files = Directory.GetFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(Path.GetFileName(path), fileName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (files.Length != 1)
            throw new InvalidOperationException(
                (MeasurementProfile.HasOwnLoggerTemplate(model, _flow.Mode)
                    ? $"{model.Name} はデータ取りで {_transferSettings.ForModel(model, _flow.Mode).ExpectedSamples} 点で測るため、専用の LOGGER 雛形「{fileName}」が要ります。" +
                      "WAVE LOGGER で共用の雛形の点数だけを変えて作り、「雛形」フォルダへ配置してください。"
                    : $"このモードに必要な雛形「{fileName}」が見つかりません。「雛形」フォルダへ配置してください。") +
                Environment.NewLine + folder);
        return files[0];
    }

    private Task LoadReferenceFileAsync(string path) =>
        _logger.LoadReferenceAsync(path, _measurementState.State, AppendLog, () =>
        {
            _measurementState.MeasurementStarted = false;
            _measurementState.State = 1;
            _measurementState.ReadAfter = 0;
        });

    private sealed record ReferenceWaveform(string FilePath, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private static string GetReferenceDisplayName(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        name = Regex.Replace(name, @"^参照(?:ﾃﾞｰﾀ|データ)[\s_＊*：:・-]*", "");
        name = Regex.Replace(name, @"\s+", "");
        return string.IsNullOrEmpty(name) ? Path.GetFileNameWithoutExtension(path) : name;
    }

    private void RefreshReferenceCandidates()
    {
        // 一覧を開くたびに読み直すので、起動後に追加した.xybも候補になる。
        string[] paths = Directory.Exists(TemplateFolderPath)
            ? Directory.GetFiles(TemplateFolderPath, "*", SearchOption.TopDirectoryOnly)
                .Where(path => string.Equals(Path.GetExtension(path), ".xyb",
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(GetReferenceDisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        _referenceComboBox.BeginUpdate();
        try
        {
            _referenceComboBox.Items.Clear();
            _referenceComboBox.Items.Add("未選択（クリックして選択）");
            foreach (string path in paths)
                _referenceComboBox.Items.Add(new ReferenceWaveform(path, GetReferenceDisplayName(path)));
            RestoreReferenceSelection();
        }
        finally { _referenceComboBox.EndUpdate(); }
        UpdateControls();
    }

    private void RestoreReferenceSelection()
    {
        _referenceComboBox.SelectedIndex = 0;
        foreach (object item in _referenceComboBox.Items)
        {
            if (item is ReferenceWaveform reference && string.Equals(reference.FilePath,
                _selectedReferencePath, StringComparison.OrdinalIgnoreCase))
            {
                _referenceComboBox.SelectedItem = item;
                break;
            }
        }
    }

    private void ReferenceDropDown(object? sender, EventArgs e)
    {
        try { RefreshReferenceCandidates(); }
        catch (Exception ex)
        {
            AppendLog("参照波形一覧の更新に失敗：" + ex.Message);
            ErrorNotification.Show(this, ex.Message, "参照波形一覧",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void ReferenceSelectionCommitted(object? sender, EventArgs e)
    {
        if (!ConnectionsReady || !_calibrationApplied || _operationInProgress ||
            _referenceComboBox.SelectedItem is not ReferenceWaveform reference)
        {
            // 未選択の項目を選んでも、LOGGER内の波形削除は行わない。
            RestoreReferenceSelection();
            return;
        }
        await RunOperationAsync("参照波形読み込み", async () =>
        {
            // 型式は参照波形のファイル名から決める(2026-11-06 に「型式」の行を廃止。人の判断)。動き検査では型式を使わない。
            ProductModel? model = FindModelForReference(reference.FilePath);
            if (_flow.Mode != TestMode.Motion)
            {
                if (model is null)
                    throw new InvalidOperationException($"参照波形「{reference.DisplayName}」に対応する型式がありません。ファイル名に型式名（{string.Join("・", ProductModel.All.Select(m => m.Name))}）を含めてください。");
                // 出荷データは工程の型式(AS110-116B350)ではなく、参照波形で選んだ型式(AS110-116B)と比べる(同じ参照波形の選び直しを止めない)。
                if (!CanChangeSetup && model != _flow.TemplateModel)
                    throw new InvalidOperationException("測定結果が残っている間は型式（参照波形）を変えられません。「設定変更（測定を破棄）」を先に押してください。");
                // Excel 雛形の無い型式は、測る前に止める(転送してから気づかないように)。
                ExcelTemplatePathFor(model);
                // LOGGER 雛形も型式とモードで決まる(AS110-006 のデータ取りは専用の 13000 点。MeasurementProfile)。専用の雛形が無ければここで止める。
                string loggerTemplate = GetDetectedTemplatePath(model);
                // 型式が替わったら供給圧力は型式の値(データ取り)に戻す(出荷データの 350 kPa は型式ごとの工程のため)。
                if (model != _selectedModel) _shippingDataSelected = false;
                _selectedModel = model;
                // いま読み込んでいる雛形と違えば(データ取りで AS110-006 とほかの型式の間を替えたとき)、同じ校正値で読み直す。
                if (!string.Equals(loggerTemplate, _workingTemplateSource, StringComparison.OrdinalIgnoreCase))
                    ReloadTemplateForModel(loggerTemplate);
                if (CanChangeSetup) ConfigureProduct(requireSaveRoot: false);
            }
            RefreshTemplateState();
            RefreshLoggerState();
            if (!_templateLoaded)
                throw new InvalidOperationException("雛形を読み込んでから参照波形を選択してください。");
            if (!LoggerReadyToStart)
                throw new InvalidOperationException("LOGGERが停止してから参照波形を選択してください。");
            if (!File.Exists(reference.FilePath))
                throw new FileNotFoundException("参照波形が見つかりません。", reference.FilePath);
            await LoadReferenceFileAsync(reference.FilePath);
            _selectedReferencePath = reference.FilePath;
            // 横軸の拡大はしない(2026-11-06 にスタンドごとの拡大を廃止。必要なら人が LOGGER の画面で拡大する)。
            return reference.DisplayName + (model is null ? "" : $" / 型式={model}") + " / 読み込み操作済み（LOGGER画面で確認）";
        });
        // エラー時は、読み込み前の選択に戻す。
        RestoreReferenceSelection();
        // 読み込み後はLOGGERを手動操作できるよう固定を解除する。
        if (_selectedReferencePath is not null) CompletePreparationDisplay();
    }

    // 参照波形のファイル名に含まれる型式名から型式を決める。「AS110-116B」と「AS110-116B350」のように前方が同じ型式があるので、
    // 型式名の直後が英数字でないものだけを合致とし、長い名前を優先する。合う型式が無ければ null。
    private static ProductModel? FindModelForReference(string path)
    {
        return ProductModel.All
            .Where(m => ReferenceNameMatches(path, m))
            .OrderByDescending(m => m.Name.Length)
            .FirstOrDefault();
    }

    // 参照波形のファイル名にその型式名が入っているか(型式名の直後が英数字でないものだけ。「AS110-116B」は「AS110-116B350」のファイルに合わない)。
    private static bool ReferenceNameMatches(string path, ProductModel model) =>
        Regex.IsMatch(Path.GetFileNameWithoutExtension(path), Regex.Escape(model.Name) + @"(?![A-Za-z0-9])", RegexOptions.IgnoreCase);

    // 測定開始のときの確かめ: いまの工程で使う型式の参照波形を読み込んでいるか。
    // 出荷データ(AS110-116B350)は専用の参照波形を作らず、判定した型式(AS110-116B)の参照波形のまま測る(SPEC「AS110-116B350 の出荷データ」6)。
    // 工程の型式(_flow.Model)で照合すると出荷データは必ず合わず、測定を始められなかった(2026-11-13、全体見直しで見つけた)。
    private bool ReferenceLoadedForFlowModel =>
        _selectedReferencePath is not null && ReferenceNameMatches(_selectedReferencePath, _flow.TemplateModel);

    private string LoadTemplateFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("雛形が見つかりません。", path);
        // 対応形式は、選択したLOGGERのOpenFileの戻り値で判定する。
        _logger.LogSessionState("雛形読み込み直前", AppendLog);
        long result = _logger.OpenFile(path);
        AppendLog($"雛形読み込み：COM OpenFile / 戻り値={result}");
        _logger.LogSessionState("COM OpenFile呼び出し後", AppendLog);
        if (result != 0)
            throw new InvalidOperationException($"WAVE LOGGER が雛形を開けませんでした（戻り値 {result}）。");
        RefreshTemplateState();
        if (!_templateLoaded)
            throw new InvalidOperationException("LOGGER のアクティブな雛形を確認できません。");
        UpdateControls();
        return $"成功 / {Path.GetFileName(path)}";
    }

    private void DeleteTemplateClicked(object? sender, EventArgs e)
    {
        RunOperation("雛形削除", () =>
        {
            if (_measurementState.MeasurementStarted)
            {
                _logger.Stop();
                _measurementState.MeasurementStarted = false;
            }
            long result = _logger.CloseActiveFile();
            if (result != 0)
                throw new InvalidOperationException($"LOGGER の雛形を閉じられませんでした（戻り値 {result}）。");
            ResetTemplate();
            // 別のファイルが残っている場合は、LOGGERがアクティブにしたファイルを反映する。
            RefreshTemplateState();
            return _templateLoaded ? "閉じました。LOGGERの別のファイルが選択されています。" : "登録を解除しました。";
        });
    }

    private void ResetTemplate()
    {
        // 停止失敗後はここで繰り返さず、「確認」または再測定へ戻るで再試行する。
        if(_leakOutputReady && !_generatorOutputUnknown)
        {
            try { StopLeakOutput(); }
            catch(Exception ex) { WarnGeneratorOutputUnknown("漏れ試験出力停止失敗："+ex.Message); }
        }
        // LOGGERの手動終了・再接続で中断した測定を、後から完成データとして保存しない。
        // 一時保存済み・正式保存済みの工程は保持する。
        if (_flow.Measuring)
        {
            try { StopLeakOutput(); } catch { }
            _flow.Invalidate();
            _measurementState.MeasurementStarted = false;
            AppendLog("LOGGERの接続・雛形が変わったため、測定を中断しました。再測定してください。");
        }
        _measurementState.MeasurementStarted = false;
        _measurementState.State = null;
        _measurementState.ReadAfter = 0;
        _measurementState.ErrorLogged = false;
        _logger.ResetMeasurementStart();
        _templateLoaded = false;
        _calibrationApplied = false;
        _workingTemplateSource = null;
        _appliedCalibration = null;
        _appliedCalibrationRecord = null;
        _calibrationRecordedRoot = null;
        KeepPreparationInFront();
        _selectedReferencePath = null;
        if (_referenceComboBox.Items.Count > 0) RestoreReferenceSelection();
        UpdateControls();
    }

    private void RefreshTemplateState()
    {
        bool loaded = _logger.HasActiveFile();
        _templateStateErrorLogged = false;
        if (_templateLoaded == loaded) return;
        _templateLoaded = loaded;
        if (!loaded)
        {
            ResetTemplate();
        }
        AppendLog(loaded
            ? "雛形読み込み済み：LOGGERで開いているファイルを使用します。"
            : "LOGGERのファイルが閉じられました。雛形待ちに戻ります。");
        UpdateControls();
    }
}
