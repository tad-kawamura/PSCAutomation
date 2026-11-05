using PSCAutomation.Wrappers.LoggerFiles;

namespace PSCAutomation.Apps.AS110AllCheck;

// AS110の実測による2点校正。ゼロ点の電圧・換算値も固定しない。
internal sealed record As110Calibration(ScalingPoints Current, ScalingPoints Pressure)
{
    public static As110Calibration FromTemplate(As110StandSettings stand, XdtScalingEditor editor) =>
        new(new(0, 0, stand.InputVoltage, stand.CurrentMilliAmps), editor.GetScaling(2));

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
