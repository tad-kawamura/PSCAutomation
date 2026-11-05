# AS110NoiseTest

AS110 のノイズ測定を自動化するアプリ。WF1973 で電流を段階的に変え、ADCMT 7351E の実測で ±0.1mA に合わせてから、KEYENCE NR-500(WAVE LOGGER)で xdt を自動記録・保存し、制御圧力の p-p と合否(黄・赤)を Excel に書く。

元は GitHub `tad-kawamura/AS110NoiseTest`(`c3e75b3b0c05`、2026-11-03)。旧リポジトリは 2026-11-04 にアーカイブした(読み取り専用)。移植前の履歴はそちらで見る。旧リポジトリの README・CLAUDE.md の冒頭には、ここへの案内を書いてある(`26edc29`)。旧リポジトリをクローンした PC は、PSCAutomation をクローンし直して作業する(`学習データ/`・`試験データ/` は git 対象外なので、旧クローンからこのフォルダーへコピーする)。

- 仕様(機器・固定値・判定・流れ・未確認): [specs/AS110NoiseTest/SPEC.md](../../specs/AS110NoiseTest/SPEC.md)
- 雛形(xdt・結果 Excel)の構成とアプリが書く位置: [雛形/README.md](雛形/README.md)
- 経緯の記録(移植前に書いたもの): `docs/`(仕様確認メモ・赤判定規則メモ・測定シーケンス実機確認・実測比較_SN71363)
- xdt 形式は `Wrappers/LoggerFiles/docs/xdt形式解析メモ.md`、7351E は `Wrappers/Instruments/docs/7351E実機確認.md`

**状態**: 測定シーケンスと画面(2026-11-05)まで実装済み。画面は機器なしの模擬(`--sim`)で確認済みで、**実機ではまだ動かしていない**(実機の測定は、これまでどおり確認ツール `Tools/MeasurementCheck` でも行える)。現場にはまだ投入していない。使っている人がいないので、修正で動きが変わってもよい(元の動きとの互換より、wrapper に揃えることを優先する。変わった点は記録する)。現場に投入したら、この扱いをやめる。

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
| `Ui/` | 画面(`MainForm`)、測定を専用の STA スレッドで動かす部分(`MeasurementRunner`、実機の LOGGER の設定 `DeviceSettings`)、雛形フォルダーの場所と型式の一覧(`TemplateFiles`) |
| `Simulation/` | 機器なしの模擬(LOGGER `SimulatedLogger`、WF1973 + 7351E `SimulatedPlant`)。画面の `--sim` と `Tools/MeasurementCheck --sim` で使う |
| `雛形/` | 測定用の雛形 xdt、結果 Excel の雛形、画面の型式の一覧 `型式一覧.txt`(1 行 1 型式。型式が増えたら書き足す) |
| `Tools/CurrentControlCheck/` | 電流制御の確認(`--sim` は機器なし) |
| `Tools/NoiseJudgeCheck/` | 判定規則の確認。`Tools/Analysis/` は Python の規則検証スクリプトと照合結果 |
| `Tools/ExcelWriteCheck/` | Excel 書き込みの確認(機器なしで既存の xdt を雛形 Excel に書く) |
| `Tools/MeasurementCheck/` | 測定シーケンスの確認・実機での測定(`--sim` 全部模擬 / `--logger-only` LOGGER だけ実機 / `--real` 全部実機) |
| `Tools/ExcelTemplate/` | 雛形 Excel の生成スクリプト(`python Tools/ExcelTemplate/make_template.py`) |

使う wrapper: `WaveLogger.Automation`(NR-500)、`LoggerFiles`(xdt の読み出し・表示レンジ・測定日時)、`Instruments`(WF1973・7351E)、`Excel`(結果ブック)。

## 画面で測る(2026-11-05)

```
AS110NoiseTest.exe          # 実機(WF1973・7351E・NR-500。LOGGER は旧 WAVE LOGGER)
AS110NoiseTest.exe --sim    # 機器を使わずに模擬で測る(記録 2 秒/本、10mA が黄・20mA が赤の波形。Excel は本物に書く)
```
開発中は `dotnet run -- --sim`(このフォルダーで)。画面の仕様(入力・測定中・終わったとき・異常時)は [SPEC.md「画面」](../../specs/AS110NoiseTest/SPEC.md)。

- 入れるもの: 型式(一覧から)・SN・備忘録・開始/刻み/終了 mA・xdt 保存先。結果 Excel は xdt 保存先の 1 つ上のフォルダーに置き、「新規」(名前を入れる。初期値は `<型式>_ノイズ測定.xlsx`)か「追記」(そのフォルダーの xlsx から選ぶ)を選ぶ
- 測定中は状態欄に「12mA 測定中」だけ出し、LOGGER の画面を前に出す。終わったら画面を前に出して「終了しました」と知らせ、結果 Excel を開いたまま渡す
- 前回の結果 Excel を開いたまま次を始めると「開けません」で止まる(閉じてから開始する)
- 細かい記録は xdt 保存先の `AS110NoiseTest_<日時>.log`(開始を押すたびに 1 つ)
- `雛形\AS110ノイズ試し取り用.xdt` があれば、各電流の本番の前に 0.5 秒の試し取りをして、測定中の LOGGER 画面でも制御圧力の線を真ん中(幅 10 kPa)に出す。無ければ試し取りせず、ログにその旨を出す。作り方は [雛形/README.md](雛形/README.md)「試し取り用の雛形の作り方」

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

Excel 書き込みの確認(出力 xlsx が無ければ雛形から作る。あれば追記。型式・SN・備忘録・mA はファイル名から。型式はファイル名の `AS110_035` を `AS110-035` に戻す):
```
dotnet run --project Tools/ExcelWriteCheck -- 出力.xlsx 学習データ/2026.06.12_不具合返却品/SN60226 [--model AS110-035] [--visible] [--leave-open]
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

画面のアプリ: `dotnet publish AS110NoiseTest.csproj -p:PublishProfile=FolderProfile`(このフォルダーで)。`bin\Release\publish` に `AS110NoiseTest.exe`(約 110MB、自己完結・単一ファイル)と `雛形\`(雛形 xdt・雛形 Excel・型式一覧)だけが出る。フォルダーごと実機 PC にコピーする。

確認ツール: `Tools\MeasurementCheck\発行.cmd` を実行する(中身は `dotnet publish Tools/MeasurementCheck/MeasurementCheck.csproj -p:PublishProfile=FolderProfile`。Release / win-x64 / 自己完結 / 単一ファイル)。`Tools\MeasurementCheck\bin\publish` に次の 3 つだけが出る。フォルダーごと実機 PC にコピーし、`実機確認 logger` → `実機確認 real <SN>` → `実機確認 full <SN>` の順に流す(`docs/測定シーケンス実機確認.md`)。

- `MeasurementCheck.exe`(約 110MB。.NET の入っていない PC でも動く。`.pdb` は exe に入れてある)
- `雛形\`(雛形 xdt・雛形 Excel・型式一覧。exe には入れないので、差し替えても発行し直さなくてよい)
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
- 実機確認(2026-11-04、移植後のコードを `dotnet run -c Release` で。WF1973・7351E・NR-500 を USB でつないだ PC、旧 WAVE LOGGER、弁 SN「1」(動作確認用)・供給 約 440kPa。保存先 `C:\AS110Noise\確認_移植後`、Excel は `AS110ノイズ測定_移植後確認.xlsx`):
  - `--sim`: すべて OK(Excel で再計算した集計 最大 1.8・黄 1・赤 1・判定 赤)
  - 手順 1 `--logger-only`(5・10mA、Excel なし): `GetUnitInfo(0) = 193`、記録 39.7 / 39.6 秒、xdt 2 本とも 39,032 バイト。終了時 `ファイル数=0`、NRWL.exe は残らない
  - 手順 2 `--real`(5・10mA): 7351E `F5 R6 PR4 ... TRS3`、実測 5.024 / 10.040mA(補正 0 回)。p-p 0.8 / 1.8 kPa、Excel 行 8 の集計 最大 1.8・黄 1・赤 0・判定 黄。1 電流あたり 42.7 / 42.5 秒。終了時 出力 OFF(0V)
  - `cancel`(記録開始 20 秒後に中断): `OK: 中断 → 出力 OFF=True / LOGGER 停止=True`。途中の xdt は保存されない。LOGGER は Quit で閉じる
  - 手順 3 `full`(7〜30mA、24 本): 完走、合計 1020 秒、1 電流あたり 平均 42.5 / 最長 42.8 秒。電流合わせは 24 本とも補正 0 回・±0.04mA 以内。10mA は手順 2 と重なり `_2` 付きで保存。Excel 行 8 の集計 最大 2.1・黄 12・赤 2(9mA・28mA)・判定 赤。終了時 出力 OFF(0V)
  - 移植前(2026-11-03、SN60226)と同じ流れ・所要時間(記録 39.6〜39.8 秒、1 電流 約 42.5 秒)で動き、赤も同じ 9mA・28mA に出た
  - 人の目視: 終了時に WF1973 が 0V・出力 OFF(前面パネル)。LOGGER 画面で 9mA・28mA の波形を見て、赤の判定どおりと確認
- 未確認: 9mA・28mA 以外の 22 本の LOGGER 画面との目視照合、発行した exe を実機 PC・別の PC で動かすこと(今回は `dotnet run`)

### 画面と型式(2026-11-05)

- 変えたこと: 入力に型式を足した(xdt 名の頭が `AS110_035` 固定 → 型式から。結果 Excel の `PP値`・`平均値`・`赤判定`・`詳細` に「型式」列を足し、mA の列が 1 つ右へずれた。行のキーは型式・SN・備忘録)。模擬の 2 クラスを `Tools/MeasurementCheck` から `Simulation/` へ移した。確認ツールに `--model` を足した
- 確認(機器なし。SDK 9.0、Excel 16 のある PC):
  - `AS110NoiseTest.slnf` のビルド(警告 0・エラー 0)。wrapper は変えていない
  - `MeasurementCheck --sim`: すべて OK。xdt 名 `AS110_035_模擬_SIM0001_5mA_…`、Excel の B 列に「AS110-035」、5mA の値が I 列
  - 画面 `--sim` を UI オートメーションで操作(人は触らずに):
    - 入力の確認: SN 空・電流空・範囲外(40mA)・小数の刻み・保存先空・ドライブ直下・追記先なし・同名の新規、それぞれ想定どおりのメッセージ
    - 通し(5〜20mA、新規): 状態欄が「準備中 → 5mA 測定中 → … → 20mA 測定中 → 後始末 → 終了しました」と変わり、16 秒で「終了しました(判定 赤、最大 p-p 1.8 kPa、黄 1、赤 1)」。結果 Excel が画面に出て開いたまま残った
    - 渡した結果 Excel を開いたまま「追記」で開始 → 「開けません。閉じてから…」で止まり、裏の Excel は残らない
    - 同じ型式・SN・備忘録で開始 → 「5, 10, 15, 20 mA を測定済み。重なる 15, 20 mA は上書き」の確認。「いいえ」で 3 秒後に待機中(Excel は残らない)、「はい」で測定
    - 17mA の測定中に中断 → 確認 →「中断しました。続きは 17mA から。15〜16 mA は保存済み」。Excel は残らない
    - 18mA の測定中に画面を閉じる → 中断の確認 → 後始末のあとアプリが終わる
    - 結果 Excel の行 8 に 5・10・15・16・17・20mA、赤判定は 20mA、詳細は 7 行
  - `FolderProfile` で発行し、発行フォルダーが `AS110NoiseTest.exe` と `雛形\` だけになること、発行した exe の `--sim` で 9・10mA が通ること(備忘録が空のときの名前 `AS110_035_P1_9mA_…`)。`MeasurementCheck` の発行も通る(`雛形\` に型式一覧も入る)
- 未確認: 画面を実機(WF1973・7351E・NR-500)で動かすこと(LOGGER の画面を前に出す動き、機器が見つからないときの知らせを含む)、新規に作った結果 Excel を 1 本も書かずに止まったとき消すこと(模擬では起こせなかった)、別の PC での発行版

### 測定中の制御圧力を真ん中に出す試し取り(2026-11-05)

- きっかけ: 画面の実機試験で、測定中の LOGGER 画面の制御圧力が雛形の表示レンジ(74〜84 kPa)のままで、線が画面の外に出ていた(保存後のファイルは真ん中に直っていた)
- 変えたこと: 各電流で、電流を合わせたあと `雛形\AS110ノイズ試し取り用.xdt` で 0.5 秒記録して制御圧力の平均を出し、本番の雛形の写し(`%TEMP%\AS110NoiseTest\AS110ノイズ測定用.xdt`)の表示レンジを平均が真ん中になるよう直してから本番を記録する。試し取りの xdt は消す。試し取り用の雛形が無ければ今までどおり。確認ツールに `--preview` / `--no-preview` / `--preview-duration` を足した。模擬の制御圧力を電流ごとに変えた(SN71363 の実測のめやす)
- 記録が短いと LOGGER の状態が「測定中」になったのを読めないことがあるので、試し取りでは「雛形の記録時間 + 1.5 秒たっても停止のままなら終わった」とみなし、保存した xdt の測定日時が雛形のままでないことで記録されたかを確かめる
- 確認(機器なし): `AS110NoiseTest.slnf` のビルド。`MeasurementCheck --sim` で、25 点の xdt を試し取りの雛形にして 8〜10mA(状態が見えないまま終わる場合。表示レンジ 26.5〜36.5 / 55〜65 / 87〜97 kPa で記録され、保存後の直しは「そのまま」)、試し取り 3 秒(状態が見える場合)、`--no-preview`(今までどおり)、試し取り中の中断(LOGGER 停止・出力 OFF)。作業フォルダーに試し取りの xdt が残らないこと
- 未確認: 試し取り用の雛形がまだ無い(人が LOGGER で作る)ので実機では動かしていない。実機の 0.5 秒記録で状態の変化が見えるか、測定中の画面が真ん中・幅 10 kPa になるか、1 電流あたり何秒延びるか(模擬では約 2 秒)、画面(`--sim` を含む)からの試し取り
