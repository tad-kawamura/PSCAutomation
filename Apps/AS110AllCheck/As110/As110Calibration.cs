using PSCAutomation.Wrappers.LoggerFiles;

namespace PSCAutomation.Apps.AS110AllCheck;

// AS110の実測による2点校正。ゼロ点の電圧・換算値も固定しない。
internal sealed record As110Calibration(ScalingPoints Current, ScalingPoints Pressure)
{
    // 校正画面の初期値。雛形(xdt)に入っている CH1 電流・CH2 圧力の値をそのまま出す(前回の適用値は呼び出さない。2026-11-06、人の判断)。
    public static As110Calibration FromTemplate(XdtScalingEditor editor) =>
        new(editor.GetScaling(1), editor.GetScaling(2));

    public void Validate()
    {
        ValidatePoints(Current, "電流");
        ValidatePoints(Pressure, "圧力");
    }

    private static void ValidatePoints(ScalingPoints points, string name)
    {
        if (!float.IsFinite(points.Input1) || !float.IsFinite(points.Output1) ||
            !float.IsFinite(points.Input2) || !float.IsFinite(points.Output2))
            throw new ArgumentException($"{name}の校正値には有限の数値を入力してください。");
        if (points.Input2 <= points.Input1)
            throw new ArgumentException($"{name}の高点電圧は低点電圧より大きくしてください。");
        if (points.Output2 <= points.Output1)
            throw new ArgumentException($"{name}の高点実測値は低点実測値より大きくしてください。");
    }

    // ふつうの値から外れている点(適用の前に人へ聞く。2026-11-14、全体見直し・区切り 4)。空なら聞かない。
    // Validate は「高点 > 低点」しか見ないので、小数点の打ち忘れ(5.524 → 5524)・V と表示値の欄の入れ違い・桁違い(540 → 54)が
    // そのまま通り、その日の測定がすべて違う換算になった。止めずに聞くだけ(実際にその値なら「はい」で適用できる)。
    // 電流はアンプの設定で電圧が変わる(1 V = 30 mA・5 V = 30 mA など)ので雛形とは比べず、値そのものの範囲で見る。
    // 圧力はセンサーが同じなので、雛形(template)があれば 1 V あたりの kPa を雛形と比べる。
    public IReadOnlyList<string> FindUnusual(As110Calibration? template)
    {
        var found = new List<string>();
        if (Math.Abs(Current.Output1) > UnusualCurrentLowMilliamps)
            found.Add($"電流の低点の表示値 {Current.Output1:G} mA（ふつうは 0 mA 付近）");
        if (Current.Output2 < UnusualCurrentHighMinMilliamps || Current.Output2 > UnusualCurrentHighMaxMilliamps)
            found.Add($"電流の高点の表示値 {Current.Output2:G} mA（ふつうは 30 mA 付近）");
        if (Current.Input2 < UnusualCurrentHighMinVolts || Current.Input2 > UnusualCurrentHighMaxVolts)
            found.Add($"電流の高点の計測値 {Current.Input2:G} V（ふつうは {UnusualCurrentHighMinVolts:G}〜{UnusualCurrentHighMaxVolts:G} V）");
        if (Math.Abs(Pressure.Output1) > UnusualPressureLowKilopascals)
            found.Add($"圧力の低点の表示値 {Pressure.Output1:G} kPa（ふつうは 0 kPa 付近）");
        if (template is not null)
        {
            double slope = Slope(Pressure), expected = Slope(template.Pressure);
            if (double.IsFinite(slope) && double.IsFinite(expected) && expected > 0 &&
                Math.Abs(slope / expected - 1) > UnusualPressureSlopeRatio)
                found.Add($"圧力の 1 V あたり {slope:0.#} kPa（雛形は {expected:0.#} kPa。±{UnusualPressureSlopeRatio:P0} を超える差）");
        }
        return found;
    }

    // 聞く基準(AI が決めた値。実機・現場に合わせて変えてよい)。
    public const float UnusualCurrentLowMilliamps = 3, UnusualCurrentHighMinMilliamps = 20, UnusualCurrentHighMaxMilliamps = 40;
    public const float UnusualCurrentHighMinVolts = 0.3f, UnusualCurrentHighMaxVolts = 6;
    public const float UnusualPressureLowKilopascals = 20;
    public const double UnusualPressureSlopeRatio = 0.20;

    private static double Slope(ScalingPoints p) => ((double)p.Output2 - p.Output1) / ((double)p.Input2 - p.Input1);

    public void Apply(XdtScalingEditor editor)
    {
        Validate();
        editor.SetScaling(1, Current.Input1, Current.Output1, Current.Input2, Current.Output2);
        editor.SetScaling(2, Pressure.Input1, Pressure.Output1, Pressure.Input2, Pressure.Output2);
    }

    public string Summary => $"電流：{Describe(Current, "mA")}／圧力：{Describe(Pressure, "kPa")}";
    private static string Describe(ScalingPoints p, string unit) =>
        $"{p.Input1:G} V＝{p.Output1:G} {unit}、{p.Input2:G} V＝{p.Output2:G} {unit}";
}
