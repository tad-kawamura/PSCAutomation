namespace PSCAutomation.Apps.AS110NoiseTest.Measurement;

// 1 回の測定(1 台分)の入力。画面で入れる値がそのまま入る。
// 電流は整数 mA だけ(雛形 Excel の列が 1〜35 の整数。NoiseResultWorkbook は小数の指令を受け付けない)。
internal sealed record MeasurementPlan(
    string SerialNumber,      // SN(Excel の行のキー)
    string Memo,              // 備忘録(空でもよい。Excel の行のキーとファイル名に入る)
    int StartMilliamps,       // 開始 mA
    int StepMilliamps,        // 刻み mA(正の整数。開始 > 終了なら下げていく)
    int EndMilliamps,         // 終了 mA(刻みで届かなければ、超えない最後の値まで)
    string OutputFolder,      // xdt の保存先
    string ExcelPath)         // 結果 Excel(無ければ雛形から作る。あれば追記)
{
    public void Validate(double maxMilliamps)
    {
        if (string.IsNullOrWhiteSpace(SerialNumber)) throw new ArgumentException("SN を入れてください。");
        if (StepMilliamps < 1) throw new ArgumentException("刻みは 1mA 以上の整数にしてください。");
        foreach (var (name, value) in new[] { ("開始", StartMilliamps), ("終了", EndMilliamps) })
        {
            if (value < 1 || value > maxMilliamps)
                throw new ArgumentException($"{name} mA は 1〜{maxMilliamps:0.#}mA にしてください({value}mA)。");
        }
        if (string.IsNullOrWhiteSpace(OutputFolder)) throw new ArgumentException("保存先を指定してください。");
        if (string.IsNullOrWhiteSpace(ExcelPath)) throw new ArgumentException("結果 Excel を指定してください。");
    }

    // 測る電流の並び(開始 → 終了。終了を超えない)。
    public IReadOnlyList<int> Currents()
    {
        var list = new List<int>();
        if (StartMilliamps <= EndMilliamps)
            for (int ma = StartMilliamps; ma <= EndMilliamps; ma += StepMilliamps) list.Add(ma);
        else
            for (int ma = StartMilliamps; ma >= EndMilliamps; ma -= StepMilliamps) list.Add(ma);
        return list;
    }
}
