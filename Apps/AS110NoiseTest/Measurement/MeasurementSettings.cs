using System.Globalization;
using System.Text.RegularExpressions;

namespace PSCAutomation.Apps.AS110NoiseTest.Measurement;

// 測定シーケンスの設定値。待ち時間・ファイル名規則・終了時の電流などはコードに直書きせず、ここから渡す。
// 既定値は CLAUDE.md / docs/仕様確認メモ.md の決定事項(待ち 1 秒、雛形 20ms×2000 点=40 秒)。
internal sealed record MeasurementSettings
{
    // LOGGER で OpenFile する雛形 xdt(記録設定 20ms・2000 点・4CH。雛形/README.md)。
    public required string TemplateXdtPath { get; init; }
    // 結果 Excel の雛形。出力先に Excel が無いときだけコピーして使う(NoiseResultWorkbook.Open)。
    public required string TemplateExcelPath { get; init; }

    // 電流を合わせてから記録開始(Start)までの待ち [s]。まず 1 秒で始め、実機で調整する。
    public double SettleSeconds { get; init; } = 1.0;
    // Start の後、LOGGER の状態が「停止」から抜けるまでの待ちの上限 [s]。超えたら開始失敗。
    public double StartTimeoutSeconds { get; init; } = 10.0;
    // 記録開始から「停止」に戻るまでの待ちの上限 [s]。雛形の 40 秒 + 本体の読み出し・余裕。
    public double AcquisitionTimeoutSeconds { get; init; } = 90.0;
    // GetState は約 200ms 間隔で更新される(AS110AllCheck の知見)。Start 直後はこの時間だけ読まない。
    public int StateReadDelayMilliseconds { get; init; } = 500;
    // 停止待ちの GetState の間隔 [ms]。
    public int StatePollIntervalMilliseconds { get; init; } = 200;
    // 中断・異常時に Stop を送ってから停止を確認するまでの上限 [s]。
    public double StopTimeoutSeconds { get; init; } = 5.0;

    // 保存する xdt のファイル名。{model} {memo} {sn} {ma} を置き換える(ma は整数)。備忘録が空なら前後の _ を 1 つにする。
    // 型式は - を _ にして入れる(AS110-035 → AS110_035。学習データと同じ名前になる。2026-11-05)。
    public string XdtFileNamePattern { get; init; } = "{model}_{memo}_{sn}_{ma}mA_20ms_440kPa.xdt";
    // 同名の xdt があるとき true なら上書き、false なら _2, _3 … を付けて別名で保存する
    // (LOGGER の SaveFile が上書き確認の画面を出して止まらないように、既定は別名)。
    public bool OverwriteExistingXdt { get; init; } = false;
    // 保存後に LOGGER のアクティブ・ファイルを閉じる(開いたままだと電流ごとにファイルが増える)。
    public bool CloseFileAfterSave { get; init; } = true;
    // 保存した xdt のサンプル数がこの値でなければ異常とする(雛形は 2000 点)。0 なら確認しない。
    public int ExpectedSamples { get; init; } = 2000;

    // 保存した xdt の制御圧力(CH3)の表示レンジを、幅はそのままに平均値中心(DisplayCenterStepKpa 刻み)に直す。
    // 雛形のレンジ(10mA)のままだと他の電流では線が画面の外に出て、人が LOGGER で見直すとき混乱するため
    // (人が測ったファイルは「収集中オートスケール」で中心 = 測定値の 0.5 kPa 刻み、幅 10 kPa になっている)。
    // 直せなくても測定は止めない(ログに警告)。XdtDisplayRange 参照。
    public bool CenterControlPressureDisplay { get; init; } = true;
    public double DisplayCenterStepKpa { get; init; } = 0.5;

    // 試し取り(2026-11-05): 測定中の LOGGER 画面でも制御圧力の線が真ん中に来るように、電流を合わせたあと
    // この雛形(記録 0.5 秒)で短く記録して制御圧力の平均を出し、本番の雛形の写しの表示レンジを
    // 「幅はそのまま、平均が真ん中」に直してから本番を記録する。null なら試し取りしない(雛形の表示レンジのまま記録)。
    // 試し取りの xdt は平均を出したら消す(判定・Excel には使わない)。
    public string? PreviewTemplateXdtPath { get; init; }
    // 表示レンジを直した本番雛形の写しと、試し取りの xdt を置くフォルダー。
    public string WorkFolder { get; init; } = Path.Combine(Path.GetTempPath(), "AS110NoiseTest");
    // 試し取りで、状態が「停止」から抜けたのが見えなかったとき(記録が短く、状態を読む前に終わったとき)に
    // 終わったとみなすまでの、雛形の記録時間に足す余裕 [s]。本当に記録されたかは xdt の測定日時で確かめる。
    public double PreviewMarginSeconds { get; init; } = 1.5;

    // 1 電流ごとに Excel を保存する(途中で止まっても結果が残る)。false なら最後に 1 回。
    public bool SaveExcelAfterEachStep { get; init; } = true;

    // 全電流が終わった後の電流 [mA]。null なら WF1973 を 0V・出力 OFF にする(CurrentController.TurnOff)。
    // 値を入れると、その電流に合わせて出力 ON のまま終わる(宿題「開始前・終了後に電流を何 mA にするか」の置き場所)。
    public double? FinalMilliamps { get; init; } = null;

    public void Validate()
    {
        static bool Positive(double v) => double.IsFinite(v) && v > 0;
        if (string.IsNullOrWhiteSpace(TemplateXdtPath)) throw new ArgumentException("TemplateXdtPath を指定してください。");
        if (!File.Exists(TemplateXdtPath)) throw new FileNotFoundException("雛形 xdt が見つかりません。", TemplateXdtPath);
        if (string.IsNullOrWhiteSpace(TemplateExcelPath)) throw new ArgumentException("TemplateExcelPath を指定してください。");
        if (!double.IsFinite(SettleSeconds) || SettleSeconds < 0) throw new ArgumentException("SettleSeconds は 0 以上にしてください。");
        if (!Positive(StartTimeoutSeconds)) throw new ArgumentException("StartTimeoutSeconds は正の値にしてください。");
        if (!Positive(AcquisitionTimeoutSeconds)) throw new ArgumentException("AcquisitionTimeoutSeconds は正の値にしてください。");
        if (!Positive(StopTimeoutSeconds)) throw new ArgumentException("StopTimeoutSeconds は正の値にしてください。");
        if (StateReadDelayMilliseconds < 0) throw new ArgumentException("StateReadDelayMilliseconds は 0 以上にしてください。");
        if (StatePollIntervalMilliseconds < 1) throw new ArgumentException("StatePollIntervalMilliseconds は 1 以上にしてください。");
        if (string.IsNullOrWhiteSpace(XdtFileNamePattern) || !XdtFileNamePattern.Contains("{ma}") || !XdtFileNamePattern.Contains("{sn}"))
            throw new ArgumentException("XdtFileNamePattern には {sn} と {ma} を含めてください。");
        if (ExpectedSamples < 0) throw new ArgumentException("ExpectedSamples は 0 以上にしてください。");
        if (!Positive(DisplayCenterStepKpa)) throw new ArgumentException("DisplayCenterStepKpa は正の値にしてください。");
        if (PreviewTemplateXdtPath is not null && !File.Exists(PreviewTemplateXdtPath))
            throw new FileNotFoundException("試し取り用の雛形 xdt が見つかりません。", PreviewTemplateXdtPath);
        if (PreviewTemplateXdtPath is not null && string.IsNullOrWhiteSpace(WorkFolder))
            throw new ArgumentException("WorkFolder を指定してください。");
        if (!double.IsFinite(PreviewMarginSeconds) || PreviewMarginSeconds < 0) throw new ArgumentException("PreviewMarginSeconds は 0 以上にしてください。");
        if (FinalMilliamps is double f && (!double.IsFinite(f) || f < 0)) throw new ArgumentException("FinalMilliamps は 0 以上にしてください。");
    }

    // xdt のファイル名(フォルダなし)。ファイル名に使えない文字は _ にする。
    public string BuildXdtFileName(string model, string serialNumber, string memo, int milliamps)
    {
        string name = XdtFileNamePattern
            .Replace("{model}", ModelForFileName(model))
            .Replace("{sn}", Sanitize(serialNumber))
            .Replace("{ma}", milliamps.ToString(CultureInfo.InvariantCulture))
            .Replace("{memo}", Sanitize(memo));
        if (string.IsNullOrWhiteSpace(memo)) name = name.Replace("__", "_");
        return name;
    }

    // ファイル名に入れるときの型式の表記(- を _ に。例 AS110-035 → AS110_035)。結果 Excel の名前の初期値にも使う。
    public static string ModelForFileName(string? model) => Sanitize(model).Replace('-', '_');

    private static readonly Regex InvalidChars =
        new("[" + Regex.Escape(new string(Path.GetInvalidFileNameChars())) + "]", RegexOptions.Compiled);

    private static string Sanitize(string? text) => InvalidChars.Replace((text ?? "").Trim(), "_");
}
