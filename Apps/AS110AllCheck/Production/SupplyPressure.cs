using System.Globalization;

namespace PSCAutomation.Apps.AS110AllCheck;

// 供給圧力の設定(2026-11-12、人の指示で将来に備えて用意したもの)。
// 今は手動のレギュレーターで人が合わせており、アプリからは変えられない。呼んでいるのは、データ取りで 350 kPa(AS110-116B350 の出荷データ)を
// 選んだときに「レギュレーターを 350 kPa に合わせてください」の文をログに残す所だけ(MainForm.ShippingData.cs。2026-11-12)。
// バルブ(型式)ごとに供給圧力が違うので、将来、電空レギュレーターなどでアプリから供給圧力を設定できるようになったら、
// 型式を選んだとき・測定を始める前にここを呼ぶ。そのときの使い方は specs/AS110AllCheck/SPEC.md「供給圧力の設定(将来用)」。
//
// ・型式ごとの供給圧力の値は ProductModel.All(Excel の見出しの「供給圧力」に書く値)を正本にし、ここでは値を二重に持たない。
//   型式の表を直せば、ここの目標値も同じ値になる。
// ・レギュレーターとの通信(どの機器・どの通信方式か)は決まっていない。決まったら通信は wrapper に置き、アプリ側は
//   ISupplyPressureRegulator で包む(機器なしの模擬に差し替えられる形。AS110NoiseTest の ILoggerDevice と同じ考え方)。
// ・許容差(目標から何 kPa ずれたら測定しないか)は決まっていないので、値を持たない(null)。

// 1 つの型式の供給圧力の設定。ToleranceKpa が null のあいだは、読んだ値との比べはしない。
internal sealed record SupplyPressureSetting(string ModelName, double TargetKpa, double? ToleranceKpa = null)
{
    // 電空レギュレーターに渡せる範囲として仮に置く上限(今の型式の最大は 588 kPa)。機器が決まったらその仕様に合わせる。
    public const double MaximumKpa = 1000.0;

    public static SupplyPressureSetting For(ProductModel model) => new(model.Name, model.SupplyPressure);

    // 設定値として使えるか。使えなければ人に出す文を返す(使えるときは空文字)。
    public string Validate()
    {
        if (!double.IsFinite(TargetKpa) || TargetKpa <= 0 || TargetKpa > MaximumKpa)
            return $"{ModelName} の供給圧力 {TargetKpa.ToString(CultureInfo.InvariantCulture)} kPa は設定できません(0 より大きく {MaximumKpa:0} kPa 以下)。";
        if (ToleranceKpa is { } tolerance && (!double.IsFinite(tolerance) || tolerance <= 0))
            return $"{ModelName} の供給圧力の許容差 {tolerance.ToString(CultureInfo.InvariantCulture)} kPa は使えません(0 より大きい値)。";
        return "";
    }

    // 読んだ供給圧力が許容差に入っているか。許容差が決まっていない(null)・値が読めない(null)ときは null(判断しない)。
    public bool? IsWithinTolerance(double? measuredKpa) =>
        ToleranceKpa is { } tolerance && measuredKpa is { } measured && double.IsFinite(measured)
            ? Math.Abs(measured - TargetKpa) <= tolerance
            : null;
}

internal static class SupplyPressureSettings
{
    // すべての型式の供給圧力(ProductModel.All と同じ順・同じ値)。
    public static IReadOnlyList<SupplyPressureSetting> All => ProductModel.All.Select(SupplyPressureSetting.For).ToArray();

    // 型式名から探す(大文字小文字は区別しない)。無ければ null。
    public static SupplyPressureSetting? Find(string modelName) =>
        All.FirstOrDefault(s => string.Equals(s.ModelName, modelName, StringComparison.OrdinalIgnoreCase));

    // 型式の供給圧力をレギュレーターに設定する。手動のレギュレーター(今)では何もせず、人に合わせてもらう文を返す。
    // アプリから設定できるレギュレーターでは、設定して読み返した値を文にして返す(許容差の外なら例外)。
    public static async Task<string> ApplyAsync(ISupplyPressureRegulator regulator, ProductModel model, CancellationToken cancellationToken = default)
    {
        var setting = SupplyPressureSetting.For(model);
        string problem = setting.Validate();
        if (problem.Length > 0) throw new InvalidOperationException(problem);
        if (!regulator.CanSetPressure)
            return $"供給圧力は手動です。レギュレーターを {setting.TargetKpa:0.#} kPa に合わせてください（{setting.ModelName}）。";
        await regulator.SetPressureAsync(setting.TargetKpa, cancellationToken);
        double? measured = await regulator.ReadPressureAsync(cancellationToken);
        if (setting.IsWithinTolerance(measured) == false)
            throw new InvalidOperationException($"供給圧力が {measured:0.#} kPa で、{setting.ModelName} の {setting.TargetKpa:0.#} kPa ±{setting.ToleranceKpa:0.#} kPa に入りません。");
        return measured is { } value
            ? $"供給圧力を {setting.TargetKpa:0.#} kPa に設定しました（読み返し {value:0.#} kPa、{setting.ModelName}）。"
            : $"供給圧力を {setting.TargetKpa:0.#} kPa に設定しました（読み返しなし、{setting.ModelName}）。";
    }
}

// 供給圧力のレギュレーター。将来の電空レギュレーターはこれを実装する(通信は wrapper)。機器なしの確認は模擬を実装して差し替える。
internal interface ISupplyPressureRegulator
{
    // アプリから供給圧力を変えられるか。手動のレギュレーターは false。
    bool CanSetPressure { get; }
    // 目標の供給圧力(kPa)にする。CanSetPressure が false の実装では呼ばない。
    Task SetPressureAsync(double targetKpa, CancellationToken cancellationToken = default);
    // 今の供給圧力(kPa)を読む。読めない機器は null を返す。
    Task<double?> ReadPressureAsync(CancellationToken cancellationToken = default);
}

// 今の現場: 手動のレギュレーター。アプリからは変えられず、読むこともできない。
internal sealed class ManualSupplyPressureRegulator : ISupplyPressureRegulator
{
    public bool CanSetPressure => false;
    public Task SetPressureAsync(double targetKpa, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("供給圧力は手動のレギュレーターで合わせてください（アプリからは設定できません）。");
    public Task<double?> ReadPressureAsync(CancellationToken cancellationToken = default) => Task.FromResult<double?>(null);
}
