using PSCAutomation.Apps.AS110AllCheck;

// 供給圧力の設定(将来用。アプリからはまだ呼ばない)。機器なしの模擬レギュレーターで確かめる。
internal static class SupplyPressureTests
{
    private static void Check(bool b, string name) { if (!b) throw new Exception(name); Console.WriteLine("PASS " + name); }

    public static async Task RunAsync()
    {
        var all = SupplyPressureSettings.All;
        Check(all.Count == ProductModel.All.Length && all.Zip(ProductModel.All).All(p => p.First.ModelName == p.Second.Name && p.First.TargetKpa == p.Second.SupplyPressure),
            "supply pressure settings follow the model table");
        Check(SupplyPressureSettings.Find("as110-116b")?.TargetKpa == 490 && SupplyPressureSettings.Find("AS110-116B350")?.TargetKpa == 350 && SupplyPressureSettings.Find("AS110-999") is null,
            "supply pressure found by model name");
        Check(all.All(s => s.Validate() == "" && s.ToleranceKpa is null), "all current supply pressures are valid and have no tolerance yet");
        Check(new SupplyPressureSetting("X", 0).Validate().Length > 0 && new SupplyPressureSetting("X", 1001).Validate().Length > 0 &&
            new SupplyPressureSetting("X", double.NaN).Validate().Length > 0 && new SupplyPressureSetting("X", 490, 0).Validate().Length > 0,
            "invalid supply pressure or tolerance is rejected");
        var withTolerance = new SupplyPressureSetting("X", 490, 5);
        Check(withTolerance.IsWithinTolerance(495) == true && withTolerance.IsWithinTolerance(495.1) == false && withTolerance.IsWithinTolerance(null) is null &&
            new SupplyPressureSetting("X", 490).IsWithinTolerance(400) is null, "tolerance check only when tolerance and value exist");

        var model = ProductModel.All.First(m => m.Name == "AS110-116B");
        var manual = new ManualSupplyPressureRegulator();
        string message = await SupplyPressureSettings.ApplyAsync(manual, model);
        Check(!manual.CanSetPressure && message.Contains("手動") && message.Contains("490 kPa") && await manual.ReadPressureAsync() is null,
            "manual regulator only asks the person to set the pressure");
        bool thrown = false;
        try { await manual.SetPressureAsync(490); } catch (NotSupportedException) { thrown = true; }
        Check(thrown, "manual regulator cannot be set from the app");

        var fake = new FakeRegulator(readBack: 489.6);
        message = await SupplyPressureSettings.ApplyAsync(fake, model);
        Check(fake.Targets.SequenceEqual([490.0]) && message.Contains("489.6"), "settable regulator receives the model pressure and reads back");
        message = await SupplyPressureSettings.ApplyAsync(new FakeRegulator(readBack: null), ProductModel.All.First(m => m.Name == "AS110-116B350"));
        Check(message.Contains("350 kPa") && message.Contains("読み返しなし"), "regulator without read back still sets the pressure");
    }

    private sealed class FakeRegulator(double? readBack) : ISupplyPressureRegulator
    {
        public List<double> Targets { get; } = [];
        public bool CanSetPressure => true;
        public Task SetPressureAsync(double targetKpa, CancellationToken cancellationToken = default) { Targets.Add(targetKpa); return Task.CompletedTask; }
        public Task<double?> ReadPressureAsync(CancellationToken cancellationToken = default) => Task.FromResult(readBack);
    }
}
