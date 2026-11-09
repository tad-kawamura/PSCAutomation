using System.Text.Json;

namespace PSCAutomation.Apps.AS100201SwitchController;

// 2 回押し(試用版、2026-11-09)で画面から変える値。exe の隣の設定ファイルに残す。
// 値が決まったら画面の入力欄を外し、As100201WaveformSettings の固定値に戻す予定(人の方針)。
internal sealed record As100201SwitchSettings
{
    public const int MinDoubleWaitMilliseconds = 50;
    public const int MaxDoubleWaitMilliseconds = 1000;
    public const int MinPhaseDegrees = 0;
    public const int MaxPhaseDegrees = 359;
    public const string FileName = "AS100201SwitchController.settings.json";

    public int DoubleWaitMilliseconds { get; init; } = As100201WaveformSettings.DoubleWaitMilliseconds;
    public int PcSinglePhaseDegrees { get; init; } = As100201WaveformSettings.PcPhaseDegrees;
    public int PcDoublePhaseDegrees { get; init; } = As100201WaveformSettings.PcDoublePhaseDegrees;
    public int CrSinglePhaseDegrees { get; init; } = As100201WaveformSettings.CrPhaseDegrees;
    public int CrDoublePhaseDegrees { get; init; } = As100201WaveformSettings.CrDoublePhaseDegrees;

    public static As100201SwitchSettings Defaults { get; } = new();

    public int GetPhase(As100201Switch press) => (press.Button, press.Kind) switch
    {
        ("PC_go", As100201PressKind.Single) => PcSinglePhaseDegrees,
        ("PC_go", As100201PressKind.Double) => PcDoublePhaseDegrees,
        ("CR_go", As100201PressKind.Single) => CrSinglePhaseDegrees,
        ("CR_go", As100201PressKind.Double) => CrDoublePhaseDegrees,
        _ => throw new ArgumentOutOfRangeException(nameof(press), press.Line, "位相を持たないスイッチです。")
    };

    // 範囲外の項目だけ初期値にする。直した項目名を problems に入れる。
    public As100201SwitchSettings Normalize(List<string> problems)
    {
        int Wait(int value, int fallback, string name)
        {
            if (value is >= MinDoubleWaitMilliseconds and <= MaxDoubleWaitMilliseconds) return value;
            problems.Add($"{name}={value}");
            return fallback;
        }
        int Phase(int value, int fallback, string name)
        {
            if (value is >= MinPhaseDegrees and <= MaxPhaseDegrees) return value;
            problems.Add($"{name}={value}");
            return fallback;
        }
        return new As100201SwitchSettings
        {
            DoubleWaitMilliseconds = Wait(DoubleWaitMilliseconds, Defaults.DoubleWaitMilliseconds, nameof(DoubleWaitMilliseconds)),
            PcSinglePhaseDegrees = Phase(PcSinglePhaseDegrees, Defaults.PcSinglePhaseDegrees, nameof(PcSinglePhaseDegrees)),
            PcDoublePhaseDegrees = Phase(PcDoublePhaseDegrees, Defaults.PcDoublePhaseDegrees, nameof(PcDoublePhaseDegrees)),
            CrSinglePhaseDegrees = Phase(CrSinglePhaseDegrees, Defaults.CrSinglePhaseDegrees, nameof(CrSinglePhaseDegrees)),
            CrDoublePhaseDegrees = Phase(CrDoublePhaseDegrees, Defaults.CrDoublePhaseDegrees, nameof(CrDoublePhaseDegrees)),
        };
    }
}

// 設定ファイルの読み書き。exe の隣に書けなければ %LOCALAPPDATA%\AS100201SwitchController に書く(ログと同じ考え方)。
internal sealed class As100201SwitchSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public As100201SwitchSettingsStore(string primaryDirectory, string fallbackDirectory)
    {
        PrimaryPath = Path.Combine(primaryDirectory, As100201SwitchSettings.FileName);
        FallbackPath = Path.Combine(fallbackDirectory, As100201SwitchSettings.FileName);
    }

    public static As100201SwitchSettingsStore CreateDefault() => new(
        AppContext.BaseDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AS100201SwitchController"));

    public string PrimaryPath { get; }
    public string FallbackPath { get; }

    // 両方にあるときは新しい方を読む(exe の隣に書けなくなった後の値を拾うため)。
    // 無ければ初期値。読めない・壊れているときは初期値にして message に理由を返す。
    public As100201SwitchSettings Load(out string? message)
    {
        message = null;
        string? path = new[] { PrimaryPath, FallbackPath }
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (path is null) return As100201SwitchSettings.Defaults;
        try
        {
            var loaded = JsonSerializer.Deserialize<As100201SwitchSettings>(File.ReadAllText(path), JsonOptions)
                ?? throw new JsonException("中身が空です。");
            var problems = new List<string>();
            var normalized = loaded.Normalize(problems);
            if (problems.Count > 0)
                message = $"設定ファイルの範囲外の値を初期値にしました（{string.Join(", ", problems)}）：{path}";
            return normalized;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            message = $"設定ファイルを読めないため初期値で始めます（{ex.Message}）：{path}";
            return As100201SwitchSettings.Defaults;
        }
    }

    // 保存した先のパスを返す。どちらにも書けなければ例外。
    public string Save(As100201SwitchSettings settings)
    {
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        try
        {
            File.WriteAllText(PrimaryPath, json);
            return PrimaryPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FallbackPath)!);
            File.WriteAllText(FallbackPath, json);
            return FallbackPath;
        }
    }
}
