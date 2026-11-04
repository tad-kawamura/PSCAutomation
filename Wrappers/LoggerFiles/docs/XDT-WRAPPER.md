# XDT wrapperの使い方

> 元は XdtConversion リポジトリの `docs/XDT-WRAPPER.md`(`9809ec3a5352`)。PSCAutomation への移植(2026-11-04)で、参照先・namespace・検証コマンドだけを書き換えた。

利用するプロジェクトから `Wrappers/LoggerFiles/LoggerFiles.csproj` を参照する。
namespaceは `PSCAutomation.Wrappers.LoggerFiles`(旧 `XdtConversion`)。対象は.NET 9(`net9.0`、Windows 非依存)。

```csharp
using PSCAutomation.Wrappers.LoggerFiles;

var file = XdtFile.Load(path); // この時点のファイルを一度だけメモリに保持
int n = file.SampleCount;
int usedChannels = file.Channels.Count;
TimeSpan? dt = file.SampleInterval;
DateTime? first = file.Info.Timing.FirstSample;
DateTime? last = file.Info.Timing.LastSample;
TimeSpan? span = file.Info.Timing.SampleSpan;     // (N-1) * dt
TimeSpan? length = file.Info.Timing.RecordLength; // N * dt

var channel = file.GetChannel(5); // CH番号は1始まり。存在しなければ例外
string name = channel.Info.Name;
string unit = channel.Info.Unit;
XdtScalingPoints points = channel.Info.ScalingPoints;
double gain = channel.Info.Gain;
double offset = channel.Info.Offset;
double[] raw = channel.ReadRawValues();             // 保存されたfloat32値をdoubleへ拡張
double[] scaled = channel.ReadScaledValues();       // スケーリングのみ
double[] processed = channel.ReadProcessedValues(); // スケーリング＋移動平均
DateTime? sampleDate = file.GetSampleTime(100);     // 添字は0始まり、範囲外は例外
TimeSpan? elapsed = file.GetElapsedTime(100);       // 最初のサンプルを0とする

foreach (var field in file.Info.BinaryFields)
    Console.WriteLine($"{field.Name}: offset={field.Offset}, bytes={field.Length}, type={field.DataType}");
```

配列は呼び出すたびに新規作成する。取得配列の変更は保持したファイルに影響しない。
大量データでは必要なCH・必要な処理段階だけを読み、返された配列を呼び出し元で保持する。
ロード後に元ファイルが変更されても、読み取る内容はロード時点のもの。

既存アプリは `XdtReader.Inspect(path)` / `XdtReader.Read(path, channelNumbers)` をそのまま使用できる。
後者の `Values` は従来と同じ、スケーリングと移動平均を適用した値。
新規APIではraw/scaled/processedを明示して取り違えを防ぐ。
Excel用の間引き、シート対応はアプリ側(旧 XdtConversion の `ConversionService`)に残す。セル書き込みは `Wrappers/Excel` の `TemplateWriter`。

日時のタイムゾーンは未確認なので `DateTimeKind.Unspecified`。勝手にUTCへ変換しない。
未確認の周期単位・周期0では周期と時間の計算値は `null`。元の数値・単位コードは
`Info.Timing.Sampling.Value` / `UnitCode` で確認できる。
日時値が0・範囲外でも、元の `AcquisitionStartRaw` / `FirstSampleRaw` を取得できる。
構造の不一致、CHの不存在、不正なスケーリング・移動平均、非有限の測定値は例外になる。
詳しい格納位置・制約は [バイナリ項目一覧](XDT-BINARY-LAYOUT.md) を参照。

## 検証

PSCAutomation のルートで実行する。

```powershell
dotnet run --project Wrappers/Tests/LoggerFiles.Tests --configuration Release
```

旧CoreVerification(移植先 `Wrappers/Tests/LoggerFiles.Tests`)は5種類の全点数値照合、メーカーCSVから保存した時刻・周期・CH設定、
raw/scaled/processedの分離、存在しないCH、範囲外添字、未確認単位、破損データ等を検証する。
旧Verificationの5種類の数値照合(`XdtReader.Read`)も同じテストに含めた。
旧VerificationのExcel転送検証(`ConversionService` を使うもの、A6 で停止していた)はアプリ側の処理なので移していない。

## YDTへ拡張する際の境界

今回のライブラリとAPIはXDT専用。拡張子だけ変えたり、XDTのバイト位置でYDTを読むことはしない。
YDTでは専用の識別・解析処理を追加し、実データで情報の意味を確かめてから、
必要に応じてCH情報・時系列値を共通の測定モデルへ変換する。
GUI・Excel処理から解析ライブラリを分けたので、その段階でYDT用の読み取り部を追加できる。
YDTの構造が不明な段階で、形式共通の固定オフセットや未検証の共通インターフェースは作らない。
