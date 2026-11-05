# AS110AllCheck 仕様(2026-11-05 移植時に整理)

移植前のリポジトリ AS110AllCheck(`982252833f86`、2026-11-02)の README・コードの動きを、雛形の形にまとめ直したもの。移植前に使われていた動きはそのまま残した。**移植時に人と決めたこと**は「移植で決めたこと」に分けて書く。操作の細部は `Apps/AS110AllCheck/README.md`「操作・設定」と `Apps/AS110AllCheck/docs/operation.md` が正本。

- 状態: **移植前の動きのまま移した**(2026-11-05)。ビルドと機器なしの検査まで。実機では未確認(`Apps/AS110AllCheck/README.md`「確認」)

## 目的

AS110 の動き検査・データ調整・データ取りを 1 つのアプリで行う。データ取りでは、測定 → Excel 雛形への転送と合否判定 → 正式保存(Excel・PDF・LOGGER)→ 漏れチェック → 完了(S/N 管理)までを、PC と M5Stack のボタンで進める。

## 使う機器

| 機器 | 役割 | 接続 | 使う wrapper |
|---|---|---|---|
| M5Stack Basic V2.7(スケッチ `firmware/AS110M5Stack`) | 現場の操作ボタン A/B/C・状態表示・外部接点(動き検査の開始条件) | USB シリアル | `Wrappers/Arduino`(取り決めはアプリの `As110ArduinoProtocol`) |
| NF WF1973 / SIGLENT SDG1032X | バースト出力(動き検査・データ取り)、漏れ試験の DC +5V | USB(VISA) | `Wrappers/Instruments` の `FunctionGeneratorConnection`・`Wf1973BurstSession`・`Sdg1032XSession` |
| KEYENCE NR-500 / NR-X | データロガー。雛形・参照波形の読み込み、測定、一時保存、正式保存 | NR-500: USB。NR-X: **USB と Wi-Fi**(画面で選ぶ) | `Wrappers/WaveLogger.Automation`、`Wrappers/Common`(本体確認・Wi-Fi 切替) |
| Microsoft Excel | 作業ブック(雛形.xlsx)への転送、判定セルの読み取り、保存・PDF | PC | `Wrappers/Excel` |

## 入力

- モード(動き検査・データ調整・データ取り)→ スタンド → 型式(参照波形)を画面または M5Stack で選ぶ。データ取りでは S/N も入れる
- NR-500 の xdt では、スタンド選択時に電流・圧力の 2 点校正(8 項目)を入れる
- 雛形: `雛形/` の LOGGER 雛形(xdt / ydt)・参照波形(xyb)・Excel 雛形(雛形.xlsx)。exe の隣の `雛形` フォルダーを人が差し替える
- 転送設定(CH・圧縮点数・NR-X の CSV 列・点数・シート・セル・保存先): `%LOCALAPPDATA%\AS110BoltCheck\ver3.json`
- NR-X の Wi-Fi プロファイル(SSID・IP): `As110/As110ConnectionSettings.cs`

## 出力

- 正式保存: Excel(作業ブックの別名保存)・PDF(シート「圧縮5」)・LOGGER のファイル。保存先は転送設定
- 完了記録: 保存先の `完了記録.txt`
- 校正記録: `%LOCALAPPDATA%\AS110AllCheck\校正記録.txt`
- 診断ログ: exe の隣の `Logs`(書けなければ `%LOCALAPPDATA%\AS110AllCheck\Logs`)

## 手順・異常時・細部

移植前と同じ。`Apps/AS110AllCheck/README.md`「操作・設定」「動き検査と開発者モード」と `Apps/AS110AllCheck/docs/` の各記録を参照。

## 移植で決めたこと(2026-11-05、人の判断)

1. NR-X は **USB と Wi-Fi の両方を残す**(今のまま)。Wi-Fi の SSID・IP は**アプリの設定クラス**(`As110/As110ConnectionSettings.cs` の `NrWifiProfiles`)に置く。共通の設定ファイルにはしない(同じ NR-X を別のアプリでも Wi-Fi で使うと、SSID が変わったときに複数か所を直すことになる)
2. 発行の設定は「**データ調整verX.YY**」(版つき。次は ver1.04)と、ふつうの発行(`FolderProfile`、`AS110AllCheck.exe`)の 2 つを残す。旧プロトタイプ ver1・ver2 の設定は移さない
3. 診断ログの代わりの保存先のフォルダー名を **AS110AllCheck** に変える(移植前は `AS110BoltCheck`)。転送設定 `ver3.json` の場所は変えない(現場 PC の保存先設定を引き継ぐため。人に確認はしていない)

## 未確定・未確認

- 実機での動作すべて(`Apps/AS110AllCheck/README.md`「確認」)
- M5Stack の入荷(2026-11-09 の週の予定)後に実機確認する
- YDT の電流・圧力の GUI 校正(移植前からの宿題。`Apps/AS110AllCheck/docs/xdt-calibration.md`)
- マニュアルトリガ運用の長期方針(移植前から検討中)
