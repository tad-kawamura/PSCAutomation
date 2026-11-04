# AS110NoiseTest

AS110 のノイズ測定を自動化するアプリ。WF1973 で電流を段階的に変え、ADCMT 7351E の実測で ±0.1mA に合わせてから、KEYENCE NR-500(WAVE LOGGER)で xdt を自動記録・保存し、制御圧力の p-p と合否(黄・赤)を Excel に書く。

- 仕様(機器・固定値・判定・流れ・未確認): [specs/AS110NoiseTest/SPEC.md](../../specs/AS110NoiseTest/SPEC.md)
- 雛形(xdt・結果 Excel)の構成とアプリが書く位置: [雛形/README.md](雛形/README.md)
- 経緯の記録(移植前に書いたもの): `docs/`(仕様確認メモ・赤判定規則メモ・測定シーケンス実機確認・実測比較_SN71363)
- xdt 形式は `Wrappers/LoggerFiles/docs/xdt形式解析メモ.md`、7351E は `Wrappers/Instruments/docs/7351E実機確認.md`

**状態**: 測定シーケンスまで実装済み。**設定画面(WinForms)は未着手**で、`AS110NoiseTest.exe` は「未実装」と出すだけ。測定は確認ツール `Tools/MeasurementCheck` で行う。現場にはまだ投入していない。

## 開く・ビルドする

Visual Studio ではリポジトリのルートの `AS110NoiseTest.slnf` を開く(アプリ・確認ツール 4 つ・使う wrapper 4 つ)。

```powershell
dotnet build AS110NoiseTest.slnf
```

NI-VISA が入っていない PC ではビルドできない(`Wrappers/Instruments` が `Ivi.Visa.Interop` を参照するため)。

## 構成

| フォルダー | 中身 |
|---|---|
| `Analysis/` | 制御圧力ノイズの合否判定(`NoiseJudge`、しきい値 `NoiseJudgeSettings`)、xdt の全チャンネル集計(`XdtNoiseAnalyzer`) |
| `Control/` | 電流フィードバック制御(`CurrentController`、設定 `CurrentControlSettings`)。機器は `ICurrentMeter` / `IOffsetVoltageSource` で受け取り、本番は `InstrumentAdapters.cs` で wrapper の 7351E・WF1973 を包む |
| `Excel/` | 結果ブックへの書き込み(`NoiseResultWorkbook`、位置は `NoiseWorkbookLayout`、書き先の差し替え用に `INoiseResultWriter`)。Excel の操作は `Wrappers/Excel` の部品 |
| `Measurement/` | 測定シーケンス(`MeasurementSequence`、設定 `MeasurementSettings`、1 台分の入力 `MeasurementPlan`、LOGGER の操作 `ILoggerDevice` と本番用 `WaveLoggerDevice`) |
| `雛形/` | 測定用の雛形 xdt と結果 Excel の雛形 |
| `Tools/CurrentControlCheck/` | 電流制御の確認(`--sim` は機器なし) |
| `Tools/NoiseJudgeCheck/` | 判定規則の確認。`Tools/Analysis/` は Python の規則検証スクリプトと照合結果 |
| `Tools/ExcelWriteCheck/` | Excel 書き込みの確認(機器なしで既存の xdt を雛形 Excel に書く) |
| `Tools/MeasurementCheck/` | 測定シーケンスの確認・実機での測定(`--sim` 全部模擬 / `--logger-only` LOGGER だけ実機 / `--real` 全部実機) |
| `Tools/ExcelTemplate/` | 雛形 Excel の生成スクリプト(`python Tools/ExcelTemplate/make_template.py`) |

使う wrapper: `WaveLogger.Automation`(NR-500)、`LoggerFiles`(xdt の読み出し・表示レンジ・測定日時)、`Instruments`(WF1973・7351E)、`Excel`(結果ブック)。

## 確認ツールの使い方

コマンドはこのフォルダー(`Apps/AS110NoiseTest`)で実行する。

```
dotnet run --project Tools/CurrentControlCheck -- --sim              # 機器なしで制御ロジック確認
dotnet run --project Tools/CurrentControlCheck -- --hold 2 1 5 10 20 35   # 実機。終了時に 0V・出力 OFF
dotnet run --project Tools/CurrentControlCheck -- --meter-info              # 7351E の設定を読むだけ
dotnet run --project Tools/CurrentControlCheck -- --meter-config 5 10 20    # DCI・200mA 固定・BUS トリガにしてから制御
dotnet run --project Tools/CurrentControlCheck -- --rate-scan 10            # レートごとの間隔・ばらつき
dotnet run --project Tools/CurrentControlCheck -- --step-test               # 電流変更直後の読み取り(トリガ・ソース別)
```

合否判定の確認(学習データは `学習データ/` に置く。git 対象外):
```
dotnet run --project Tools/NoiseJudgeCheck -- <xdt のパス>   # 1 本を判定して指標を表示
dotnet run --project Tools/NoiseJudgeCheck -- --all [フォルダー]   # 学習データ全部を判定 → Tools/Analysis/noise_judge_cs.csv
python Tools/Analysis/red_rule_eval.py                      # Python 版(規則の検証・調整用)
python Tools/Analysis/compare_cs.py                         # C# 版と Python 版の照合
```

Excel 書き込みの確認(出力 xlsx が無ければ雛形から作る。あれば追記。SN・備忘録・mA はファイル名から):
```
dotnet run --project Tools/ExcelWriteCheck -- 出力.xlsx 学習データ/2026.06.12_不具合返却品/SN60226 [--visible] [--leave-open]
```

測定シーケンス。xdt の保存先は既定で `%TEMP%\AS110NoiseTest_MeasurementCheck`、Excel は保存先の `AS110ノイズ測定_確認.xlsx`(`--out` / `--excel` で変更):
```
dotnet run --project Tools/MeasurementCheck -- --sim                          # 機器なし。5,10,15,20mA。10mA が黄・20mA が赤の波形で Excel まで書く
dotnet run --project Tools/MeasurementCheck -- --sim --no-excel --cancel-after 3   # Excel なし。記録中に中断して後始末を確認
dotnet run --project Tools/MeasurementCheck -- --logger-only --start 5 --step 5 --end 10 --out C:\AS110Noise\確認 --no-excel   # LOGGER だけ実機
dotnet run --project Tools/MeasurementCheck -- --real --sn SN60226 --memo 実機確認 --start 5 --step 5 --end 10 --out C:\AS110Noise\確認 --visible   # 全部実機
```
オプション一覧は `Tools/MeasurementCheck/Program.cs` の先頭。

## 実機 PC 用の発行

`Tools\MeasurementCheck\発行.cmd` を実行する(中身は `dotnet publish Tools/MeasurementCheck/MeasurementCheck.csproj -p:PublishProfile=FolderProfile`。Release / win-x64 / 自己完結 / 単一ファイル)。`Tools\MeasurementCheck\bin\publish` に次の 3 つだけが出る。フォルダーごと実機 PC にコピーし、`実機確認 logger` → `実機確認 real <SN>` → `実機確認 full <SN>` の順に流す(`docs/測定シーケンス実機確認.md`)。

- `MeasurementCheck.exe`(約 110MB。.NET の入っていない PC でも動く。`.pdb` は exe に入れてある)
- `雛形\`(雛形 xdt・雛形 Excel。exe には入れないので、差し替えても発行し直さなくてよい)
- `実機確認.cmd`

実機 PC に要るもの: NI-VISA(`Ivi.Visa.Interop` は exe に入っているが VISA 本体は要る)、WAVE LOGGER(NR-500 は旧 WAVE LOGGER で使う)、Excel。

## 移植前との対応

元は旧リポジトリ AS110NoiseTest(`c3e75b3`、2026-11-03)。出どころと変えたことの詳細は `docs/移行記録.md`(リポジトリのルート)。

| 移植前 | 移植後 |
|---|---|
| `Libraries/WaveLogger.Automation/`(AS110AllCheck からのコピー) | `Wrappers/WaveLogger.Automation` |
| `Conversion/XdtReader.cs`・`XdtDisplayRange.cs`・`XdtTimestamp.cs` | `Wrappers/LoggerFiles` |
| `Devices/Wf1973DcSource.cs`・`Adcmt7351ESession*.cs`・`Adcmt7351ESettings.cs`・`VisaUtil.cs` | `Wrappers/Instruments`(`VisaUtil` は `VisaInstrumentSession` に吸収)。アプリの interface への包みは `Control/InstrumentAdapters.cs` |
| `Excel/NoiseResultWorkbook.cs`(Excel COM を直接操作) | 同じ場所。Excel の操作を `Wrappers/Excel` の部品に置き換えた |
| `CLAUDE.md`(作業ルールと決定事項) | 決定事項は `specs/AS110NoiseTest/SPEC.md`、作業ルールはリポジトリの `AGENTS.md` |
| `docs/xdt形式解析メモ.md`・`docs/7351E実機確認.md` | `Wrappers/LoggerFiles/docs/`・`Wrappers/Instruments/docs/` |
| namespace `AS110NoiseTest.*` | `PSCAutomation.Apps.AS110NoiseTest.*` |

## 確認したこと・していないこと

- 確認(2026-11-04、SDK 9.0.318、NI-VISA・Excel 16 のある PC。機器はつないでいない):
  - 全体 `PSCAutomation.sln` の Release ビルドと `AS110NoiseTest.slnf` の Debug ビルド(警告 0・エラー 0)
  - `CurrentControlCheck --sim`(制御ロジック・7351E の応答の判定): すべて OK
  - `MeasurementCheck --sim`(模擬の LOGGER・電流で 5〜20mA。実際の Excel に書いて再計算した集計まで)と `--cancel-after 3`(中断で LOGGER 停止・出力 OFF): すべて OK。Excel のプロセスは残らなかった
  - `NoiseJudgeCheck --all` で学習データ 428 本を判定し、`Tools/Analysis/noise_judge_cs.csv` が移植前の結果とバイト単位で一致(xdt の読み出しを wrapper に替えても値・判定が変わらない)
  - `ExcelWriteCheck` を移植前と移植後のツールで同じ xdt(SN60226・SN60231・ヨーク165 SN1、46 本)に対して実行し、できた xlsx の全シートの値・数式が一致(2,415 セル)。103 台分(雛形の 100 行を超えて行を足す場合)も一致(3,929 セル)
  - `FolderProfile` で発行し、発行フォルダーが `MeasurementCheck.exe`・`雛形\`・`実機確認.cmd` だけになること、発行した exe の `--sim` が Excel まで通ること
- 未確認: **実機すべて**(WF1973・7351E・NR-500・WAVE LOGGER を移植後のコードで動かすこと)。移植前は 2026-11-03 に実機で通しているが、移植後は VISA の探し方・LOGGER の wrapper・Excel の部品が変わっているので、`実機確認 logger` → `real` → `full` → `cancel` を流し直す。発行した exe を別の PC で動かすこと
