namespace PSCAutomation.Apps.AS110AllCheck;

// データ取りの「供給圧力」の選択と、判定しない出荷データ(データ取り２)の工程(2026-11-12、人と決めた。
// SPEC「AS110-116B350 の出荷データ(データ取り２)」)。
// AS110-116B を参照波形で選ぶと、データ取りの操作欄に「供給圧力」が出る。490 kPa はふつうのデータ取り(判定・保存・漏れ試験)、
// 350 kPa は AS110-116B350 の出荷データ(参照波形なし・116B350 専用の雛形なし・合否判定なし・保存あり・漏れ試験なし)。
// 出荷データは、データ取りの保存先の中の「出荷データ(350kPa)」へ保存し、データ取りの保存先の完了記録で
// 490 kPa の合格(保護中)を確かめた S/N だけを測る。完了記録(S/N の保護)は出荷データの保存先に別に持つ。
internal sealed partial class MainForm : Form
{
    private readonly ComboBox _supplyPressureBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 520 };
    private readonly Label _supplyPressureLabel = new() { Text = "供給圧力", AutoSize = true };
    // 350 kPa を選んでいるあいだの目立つ案内(レギュレーターの合わせ忘れに気づけるように)。
    private readonly Label _shippingNoticeLabel = new() { AutoSize = true, ForeColor = Color.DarkOrange, MaximumSize = new Size(680, 0), Visible = false, Margin = new Padding(0, 0, 0, 6) };
    private TableLayoutPanel _supplyPressurePanel = null!;
    // 人が 350 kPa(出荷データ)を選んだか。型式やモードが替わると 490 kPa に戻す。
    private bool _shippingDataSelected;
    // 出荷データのとき、測定開始 S/N が 490 kPa のデータ取りで合格(データ取りの保存先の完了記録で保護中)しているか。
    private bool _judgedPassed = true;

    private Control BuildSupplyPressureRow()
    {
        _supplyPressurePanel = Row(_supplyPressureLabel, _supplyPressureBox);
        var font = new Font(Font.FontFamily, 12);
        _supplyPressureLabel.Font = _supplyPressureBox.Font = font;
        _shippingNoticeLabel.Font = new Font(Font.FontFamily, 12, FontStyle.Bold);
        _supplyPressurePanel.Visible = false;
        _supplyPressureBox.SelectionChangeCommitted += (_, _) => SupplyPressureSelectionCommitted();
        return _supplyPressurePanel;
    }

    // いま選んでいる工程。データ取りで 350 kPa を選び、型式に出荷データの工程があれば(出荷データの型式, 判定した型式)。ほかは(型式, null)。
    private (ProductModel Model, ProductModel? Judged) SelectedStage =>
        _flow.Mode == TestMode.Production && _shippingDataSelected && ShippingDataModels.For(_selectedModel) is { } shipping
            ? (shipping, _selectedModel)
            : (_selectedModel, null);

    // 保存先(Excel・PDF・LOGGER・完了記録)。出荷データはデータ取りの保存先の中の「出荷データ(350kPa)」。選んでいなければ空。
    // 校正記録・保存先フォルダー名「SN開始-最後」の照合は、人が選んだ保存先(_rootBox)のまま。
    private string SaveRootPath =>
        _rootBox.Text.Length == 0 ? "" :
        _flow.ShippingData ? Path.Combine(_rootBox.Text, ShippingDataModels.FolderName(_flow.Model)) : _rootBox.Text;

    private string JudgedNotPassedText =>
        $"S/N {_flow.Serial} は {_flow.JudgedModel?.Name}（{_flow.JudgedModel?.SupplyPressure} kPa）のデータ取りで合格していません（保存先の完了記録で保護中ではありません）。合格した S/N だけ出荷データを測れます";

    // 出荷データのとき、測定開始 S/N がデータ取りの保存先の完了記録で保護中(合格)かを読み直す。読めないときは合格していない扱い(測らせない)。
    // 出荷データの保存先の完了記録とはロックのファイルが別なので、そちらを持ったまま呼んでよい。
    private void RefreshJudgedPass(bool throwOnError = false)
    {
        _judgedPassed = true;
        _judgedPassReadError = null;
        if (!_flow.ShippingData || _flow.SerialMissing || _rootBox.Text.Length == 0) return;
        try
        {
            var judged = new CompletionStore(_rootBox.Text);
            using var held = judged.Lock();
            _judgedPassed = judged.IsComplete(_flow.Serial);
        }
        catch (Exception ex)
        {
            _judgedPassed = false;
            // 完了記録の「開けません」の案内(FileAccessAdvice)は、下の JudgedReadFailedText が同じことを順に書くので、ここでは元の理由だけを持つ。
            _judgedPassReadError = ex is FileAccessAdviceException ? ex.InnerException!.Message : ex.Message;
            AppendLog("データ取りの完了記録の確認に失敗：" + ex.Message);
            if (throwOnError) throw new IOException(JudgedReadFailedText(afterMeasurement: false), ex);
        }
    }

    // 出荷データで、データ取りの完了記録を読めなかったときの理由(読めた・確かめていないときは null)。_judgedPassed が false のときだけ意味がある。
    private string? _judgedPassReadError;
    private bool JudgedPassUnreadable => !_judgedPassed && _judgedPassReadError is not null;

    // 合格を確かめられなかったときの案内(2026-11-13、人の指示)。オフラインの PC なので、ほかの PC と取り合うことは無い。
    // すぐ再起動すると時間がかかるので、先にその場で確かめられることを出し、それでもだめならアプリ → PC の順に再起動する。
    // afterMeasurement: 測ったあと(「保存」のとき)は「保存」を押し直す。測る前は「保存先選択」で選び直す(選び直すと読み直す)。
    private string JudgedReadFailedText(bool afterMeasurement) =>
        $"{_flow.JudgedModel?.SupplyPressure} kPa のデータ取りの完了記録を読めず、S/N {_flow.Serial} が合格しているか確かめられません。\n" +
        $"（{_judgedPassReadError}）\n\n" +
        "次の順に確かめてください。\n" +
        $"1. 保存先「{_rootBox.Text}」をエクスプローラーで開けるか（USB メモリー・外付けディスクなら差し直す）\n" +
        "2. 保存先の「完了記録.txt」を Excel などで開いていたら閉じる\n" +
        "3. " + (afterMeasurement ? "もう一度「保存」を押す" : "「保存先選択」で同じ保存先を選び直す") + "\n" +
        "4. それでも読めなければアプリを再起動する。だめなら PC を再起動する";

    // 「次にすること」(1 行)に出す短い案内。
    private string JudgedReadFailedNextStep =>
        $"{_flow.JudgedModel?.SupplyPressure} kPa の完了記録を読めず、合格を確かめられません。保存先をエクスプローラーで開けるか（USB なら差し直す）・" +
        "「完了記録.txt」を開いていないかを確かめて、「保存先選択」で同じ保存先を選び直してください（だめならアプリ → PC の順に再起動）";

    // 出荷データを測らせない理由(合格していない、または完了記録を読めない)。ダイアログに出す。
    private string JudgedBlockedText(bool afterMeasurement) =>
        JudgedPassUnreadable ? JudgedReadFailedText(afterMeasurement) : JudgedNotPassedText + "。";

    private void SupplyPressureSelectionCommitted()
    {
        bool shipping = _supplyPressureBox.SelectedIndex == 1;
        if (shipping == _shippingDataSelected) return;
        // 測定結果が残っている間などは変えない(「設定変更（測定を破棄）」を先に押す)。
        if (!CanChangeSetup) { _supplyPressureBox.SelectedIndex = _shippingDataSelected ? 1 : 0; return; }
        _shippingDataSelected = shipping;
        CloseJudgmentNotice();
        RunOperation("供給圧力選択", () =>
        {
            ConfigureProduct(requireSaveRoot: false);
            // 今は手動のレギュレーター。合わせてもらう文を返すだけ(将来アプリから設定できるレギュレーターに差し替える。Production/SupplyPressure.cs)。
            // 手動のレギュレーターは待たずに終わるので、ここでは結果をそのまま待つ。
            // 注意: 通信のある機器に差し替えるときは、この待ち方だと設定・読み返しのあいだ画面が固まる。
            // そのときは RunOperation を RunOperationAsync に替えて await で待つ(SPEC「供給圧力の設定(将来用)」4。2026-11-13、人が承認)。
            string regulator = SupplyPressureSettings.ApplyAsync(new ManualSupplyPressureRegulator(), _flow.Model).GetAwaiter().GetResult();
            return (_flow.ShippingData ? $"{_flow.Model.SupplyPressure} kPa（データ取り２・{_flow.Model.Name} 出荷データ）" : $"{_flow.Model.SupplyPressure} kPa（データ取り）") + " / " + regulator;
        });
        UpdateControls();
    }

    // 「供給圧力」の行と案内の表示を、型式・工程に合わせる(UpdateProductionControls から呼ぶ)。
    private void UpdateSupplyPressureControls()
    {
        ProductModel? shipping = _flow.Mode == TestMode.Production && _modeSelected ? ShippingDataModels.For(_selectedModel) : null;
        _supplyPressurePanel.Visible = shipping is not null;
        if (shipping is not null)
        {
            string[] items =
            [
                $"{_selectedModel.SupplyPressure} kPa（データ取り：合否判定・保存・漏れ試験）",
                $"{shipping.SupplyPressure} kPa（データ取り２：{shipping.Name} 出荷データ。判定・漏れ試験なし）",
            ];
            if (!_supplyPressureBox.Items.Cast<string>().SequenceEqual(items))
            {
                _supplyPressureBox.Items.Clear();
                _supplyPressureBox.Items.AddRange(items);
            }
            int index = _flow.ShippingData ? 1 : 0;
            if (_supplyPressureBox.SelectedIndex != index) _supplyPressureBox.SelectedIndex = index;
            _supplyPressureBox.Enabled = CanChangeSetup;
        }
        _shippingNoticeLabel.Visible = shipping is not null && _flow.ShippingData;
        _shippingNoticeLabel.Text = _flow.ShippingData
            ? $"{_flow.Model.SupplyPressure} kPa の出荷データ（{_flow.Model.Name}）：レギュレーターを {_flow.Model.SupplyPressure} kPa に合わせてください。" +
              $"合否判定と漏れ試験はしません。保存先は「{ShippingDataModels.FolderName(_flow.Model)}」。{_flow.JudgedModel?.SupplyPressure} kPa で合格した S/N だけ測れます。"
            : "";
    }
}
