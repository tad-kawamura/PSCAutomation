# Wrappers(共通 wrapper)

どのアプリでも同じ機器操作・ファイル操作を置く場所。下の各フォルダーが 1 つの csproj(箱)で、アプリは必要な箱だけを `ProjectReference` する。
アプリ固有の数値(電流の上限、しきい値、SSID、保存先、セル位置)は置かない。何ができるかの一覧は `docs/capabilities.md`、移植の出どころは `docs/移行記録.md`。

## 箱の一覧

| 箱(フォルダー) | namespace | 対象 | 外部依存 | 状態 |
|---|---|---|---|---|
| `WaveLogger.Automation` | `PSCAutomation.Wrappers.WaveLogger.Automation` | WAVE LOGGER / WAVE LOGGER X の COM 操作(`net9.0-windows`) | なし(COM は実行時に探す) | 移植済み(2026-11-04)。詳しくは同フォルダーの README |
| `LoggerFiles` | `PSCAutomation.Wrappers.LoggerFiles` | ロガーの出力ファイル(xdt / ydt / CSV)の読み書き(`net9.0`) | なし | 移植済み(2026-11-04)。形式の資料は `LoggerFiles/docs/` |
| `Common` | `PSCAutomation.Wrappers.Common` | エラー表示、PC 遅延観測、窓の前面化、NR-X 接続補助・Wi-Fi 切替(`net9.0-windows`、WinForms) | なし(WaveLogger.Automation を参照) | 移植済み(2026-11-04) |
| `Excel` | `PSCAutomation.Wrappers.Excel` | xlsx 雛形への直接書き込み(`net9.0`) | なし | `TemplateWriter` のみ移植済み。Excel を起動する操作は範囲の判断待ち |
| `Arduino` | `PSCAutomation.Wrappers.Arduino` | Arduino のシリアル通信・異常ログ(`net9.0-windows`) | `System.IO.Ports`(NuGet。Common を参照) | 移植済み(2026-11-04) |
| `Instruments` | `PSCAutomation.Wrappers.Instruments` | VISA 機器(WF1973 / SDG1032X / ADCMT 7351E)(`net9.0-windows`) | NI-VISA(COM 参照。ビルドにも要る) | 移植済み(2026-11-04)。WF1973 のクラスの選び方は同フォルダーの README |

## アプリから使う

```xml
<ItemGroup>
  <ProjectReference Include="..\..\Wrappers\WaveLogger.Automation\WaveLogger.Automation.csproj" />
  <ProjectReference Include="..\..\Wrappers\LoggerFiles\LoggerFiles.csproj" />
</ItemGroup>
```

```csharp
using PSCAutomation.Wrappers.WaveLogger.Automation;
using PSCAutomation.Wrappers.LoggerFiles;
```

機器を使う箱は、アプリ側で薄い interface に包んで機器なしの模擬に差し替えられる形にする(AGENTS.md「wrapper とアプリ側の切り分け」)。

## テスト

`Tests/<箱>.Tests` はコンソールアプリで、失敗すると例外で止まる(終了コード 0 以外)。実機・WAVE LOGGER 本体ソフト・Excel・COM ポートは使わない(`Instruments.Tests` は NI-VISA を、どの機器にも当たらない探索だけに使う)。リポジトリのルートで実行する。

```powershell
dotnet build PSCAutomation.sln -c Release
dotnet run --no-build --project Wrappers/Tests/WaveLogger.Automation.Tests -c Release
dotnet run --no-build --project Wrappers/Tests/LoggerFiles.Tests -c Release
dotnet run --no-build --project Wrappers/Tests/Common.Tests -c Release
dotnet run --no-build --project Wrappers/Tests/Excel.Tests -c Release
dotnet run --no-build --project Wrappers/Tests/Arduino.Tests -c Release
dotnet run --no-build --project Wrappers/Tests/Instruments.Tests -c Release
```

wrapper を変えたときは、全体 `.sln` のビルドと上の 6 つをすべて実行する(AGENTS.md「wrapper を変更するとき」)。テストが通っても、機器依存の動作(COM・画面操作・本体との通信・Wi-Fi 切替・シリアル通信・VISA 機器)は実機で確認するまで「未確認」と書く。
