using System.Text;

namespace PSCAutomation.Apps.AS110NoiseTest.Ui;

// exe の隣(開発中はアプリのフォルダー)の 雛形 フォルダーにあるファイル。exe には入れない(差し替えても発行し直さずに済むように)。
//   AS110ノイズ測定用.xdt      LOGGER の記録設定の雛形
//   AS110ノイズ測定_雛形.xlsx  結果 Excel の雛形
//   型式一覧.txt               画面の「型式」の一覧(1 行 1 型式。空行と # で始まる行は読まない)
//   AS110ノイズ試し取り用.xdt  記録 0.5 秒の雛形。測定中の画面で制御圧力を真ん中に出すための試し取りに使う(無くてもよい)
internal sealed class TemplateFiles
{
    public const string FolderName = "雛形";
    public const string XdtName = "AS110ノイズ測定用.xdt";
    public const string ExcelName = "AS110ノイズ測定_雛形.xlsx";
    public const string ModelListName = "型式一覧.txt";
    // 試し取り用(記録 0.5 秒)。無くても測れる(測定中の画面の制御圧力が雛形の表示レンジのままになるだけ)。2026-11-05
    public const string PreviewXdtName = "AS110ノイズ試し取り用.xdt";

    private TemplateFiles(string folder) => Folder = folder;

    public string Folder { get; }
    public string Xdt => Path.Combine(Folder, XdtName);
    public string PreviewXdt => Path.Combine(Folder, PreviewXdtName);
    public string? PreviewXdtIfExists => File.Exists(PreviewXdt) ? PreviewXdt : null;
    public string Excel => Path.Combine(Folder, ExcelName);
    public string ModelList => Path.Combine(Folder, ModelListName);

    // exe のフォルダーから上へ、雛形 xdt と雛形 Excel がそろった 雛形 フォルダーを探す。
    public static TemplateFiles Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, FolderName);
            if (File.Exists(Path.Combine(candidate, XdtName)) && File.Exists(Path.Combine(candidate, ExcelName)))
                return new TemplateFiles(candidate);
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"{FolderName} フォルダー({XdtName} と {ExcelName})が見つかりません。exe の隣に {FolderName} フォルダーを置いてください。");
    }

    // 型式の一覧。ファイルが無い・空なら例外(画面は開始できない旨を出す)。
    public IReadOnlyList<string> ReadModels()
    {
        if (!File.Exists(ModelList))
            throw new FileNotFoundException($"型式の一覧 {ModelListName} が {Folder} にありません。", ModelList);
        var models = File.ReadAllLines(ModelList, Encoding.UTF8)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (models.Count == 0)
            throw new InvalidDataException($"型式の一覧 {ModelListName} に型式が書かれていません({ModelList})。");
        return models;
    }
}
