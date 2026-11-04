# AS100201SwitchController

AS100-201 のスイッチ制御・測定アプリ。治具の Arduino のスイッチ操作を受け取り、WF1973 の連続出力(三角波)を位相をそろえて出し直し、KEYENCE WAVE LOGGER の測定開始・停止を行う。

元は GitHub `tad-kawamura/AS100201SwitchController`(`5eb5b6391a82`、2026-10-28)。旧リポジトリは 2026-11-04 にアーカイブした(読み取り専用)。移植前の履歴はそちらで見る。旧リポジトリの README・AGENTS.md の冒頭には、ここへの案内を書いてある(`c8a5e4b`)。旧リポジトリをクローンした PC は、PSCAutomation をクローンし直して作業する。

- 仕様(機器・操作・異常時・移植で変えたこと): [specs/AS100201SwitchController/SPEC.md](../../specs/AS100201SwitchController/SPEC.md)
- Arduino スケッチ: `firmware/ArduinoSwitchAndReadyLed/ArduinoSwitchAndReadyLed.ino`(移植で内容は変えていない)

## 開く・ビルドする

Visual Studio ではリポジトリのルートの `AS100201SwitchController.slnf` を開く(アプリ・互換テスト・使う wrapper 4 つ)。

```powershell
dotnet build AS100201SwitchController.slnf
```

NI-VISA が入っていない PC ではビルドできない(`Wrappers/Instruments` が `Ivi.Visa.Interop` を参照するため)。

## 構成

| フォルダー・ファイル | 中身 |
|---|---|
| `As100201/As100201WaveformSettings.cs` | WF1973 の設定値(振幅 10.2Vpp・0.02Hz・オフセット 0V・PC 位相 15°・CR 位相 165°・三角波)。実機で位相を調整するときはここだけ直す |
| `As100201/As100201ArduinoProtocol.cs` | Arduino との取り決め(確認文字列、250ms ごとの定期応答の監視、1 秒止まったら操作禁止、3 秒で再接続、復旧後 1 秒は操作を捨てる、READY LED) |
| `As100201/As100201Application.cs` | 画面の文言、異常ログのアプリ名と固有の異常種別 |
| `As100201/MainForm.*.cs` | 接続確認・雛形の登録・スイッチの処理・定期確認・前面表示・終了 |
| `UI/` | 画面の組み立て、操作の共通処理、PC 遅延観測 |
| `Tests/Compatibility/` | 互換テスト(模擬の機器で、送るコマンドと順番・LOGGER の呼び方・状態判定・異常ログの設定を確かめる) |
| `firmware/` | Arduino スケッチ(旧 `_arduino/`) |

使う wrapper: `WaveLogger.Automation`(LOGGER、`ReferenceOnly`)、`Common`(NR-500 の本体確認・エラー表示・PC 遅延観測)、`Arduino`(シリアル通信・異常ログ)、`Instruments`(`Wf1973ContinuousSession`)。

## 操作

| 操作 | 動作 |
|---|---|
| 起動 | Arduino・WF1973・LOGGER/NR 本体(USB)を確認する。WF1973 は三角波にする。「確認」ボタンでやり直せる |
| 「確認」(WF1973 が接続済み) | WF1973 の生存確認のあと、波形の形(三角波)だけを送り直す。出力・振幅・位相は変えない(形が DC などになっていたら、これで戻る) |
| 雛形を画面へドロップ | LOGGER で開く。READY LED が点く |
| PC_go / CR_go | WF1973 を PC 位相(15°)/ CR 位相(165°)で出し直す。出力中は OFF にしない。処理中の再押下は最後の押下で上書き |
| F8 / 「測定開始（確認用）」 | LOGGER の測定開始(毎回 Start) |
| sw4 | LOGGER の停止 |

NR 本体は **USB だけ**(NR-500 / NR-X)。移植前にあった Wi-Fi の選択と「元のWi-Fiへ戻す」ボタンは外した(SPEC.md)。

## 互換テスト

```powershell
dotnet build Apps/AS100201SwitchController/Tests/Compatibility/AS100201.RegressionTests.csproj
Apps/AS100201SwitchController/Tests/Compatibility/bin/Debug/net9.0-windows/AS100201.RegressionTests.exe
```

機器・LOGGER は使わない。10 グループすべて PASS で終了コード 0。

## 発行

```powershell
dotnet publish Apps/AS100201SwitchController/AS100201SwitchController.csproj -p:PublishProfile=FolderProfile
```

`bin/Release/publish/AS100201SwitchController.exe` 1 つ(自己完結・win-x64)。雛形はアプリに同梱しない(人がふだんの xdt をドロップする)。異常ログは exe の隣の `Logs\`、書けなければ `%LOCALAPPDATA%\AS100201SwitchController\Logs`。

## 確認(2026-11-04、移植後)

実機: WF1973(USB)・NR-500(USB、旧 WAVE LOGGER)・Arduino UNO R4 Minima(COM4、同梱スケッチ)。PC には WAVE LOGGER X も入っている。Debug ビルドの exe で、人がスイッチを押し、アプリのログ・異常ログと人の目視で確かめた。

| 項目 | 結果 |
|---|---|
| 起動時の接続確認 | 3 機器とも成功(約 1.8 秒)。LOGGER は旧 WAVE LOGGER、本体は NR-500 |
| 雛形のドロップ | 測定済みの AS100-201 の xdt(流量 PC)で成功。READY LED 点灯(目視) |
| PC_go / CR_go | 位相 15° / 165° で出し直し(1 回目約 90ms、以後 3〜5ms)。**三角波で位相が切り替わり、出力は途中で OFF にならない**(目視)。約 0.45 秒間隔の PC→CR の連続押下も両方とも処理 |
| F8 → sw4 | 3 回とも開始・停止。停止後すぐの F8 でも開始できた(目視でも確認) |
| Arduino の USB 抜き差し | 2 回とも自動で再接続(COM ポートが戻ってから約 1.7 秒。起動待ちの分)。抜いている間は READY LED 消灯、再接続後に再点灯(目視)。抜き差しだけでは WF1973・LOGGER は動かない(ログと目視)。2 回目は測定中に抜いたが、LOGGER へ停止を送らなかった(ログ) |
| 再接続後の操作 | PC_go・CR_go・F8・sw4 が通常どおり動く |
| 接続済みでの「確認」で形を戻す | アプリが WF1973 を開いたまま、別の通信(作業用の確認プログラム。WF1973 は接続中リモートになり前面パネルでは変えられないため)で形を DC にし、「確認」を押した。形は RAMP・対称 50% に戻り、出力は OFF のまま変わらなかった(読み出しで確認)。「確認」の WF1973 の部分は約 0.17 秒 |

**移植の実機確認で見つけて直したこと**: 最初の確認で、PC_go / CR_go を押しても WF1973 が DC のままだった。アプリの送ったコマンド(位相・出力 ON)は正しく、WF1973 本体の「波形の形」が直前に使った AS110NoiseTest の DC のまま残っていたため(移植前のアプリは形を送らず本体の設定を使う作り)。人と相談して、接続時に三角波(対称 50%)を送るようにした(`Wf1973ContinuousSession.Connect(GeneratorWaveform)` を wrapper に追加。SPEC.md)。直した後の確認が上の表。

未確認:
- 出力中(PC_go の後)に「確認」で形を送り直したときの出力の乱れ。確かめたのは出力 OFF のときだけ
- 再押下で処理中の出し直しを打ち切る動き(コマンドの間に次の押下が入る場合)。実機では 1 回の出し直しが 3〜5ms で終わり、人の連続押下では重ならなかった。模擬では互換テストで確認
- WAVE LOGGER X での操作、NR-X(USB)、旧 WAVE LOGGER で見つからないときの LOGGER X への自動切り替え
- LOGGER 側でファイルを閉じたとき・LOGGER を閉じたときの扱い、「雛形を削除」「状態確認」ボタン、アプリ終了時の READY LED 消灯
- Arduino の再起動(`DIAG_BOOT`)、定期応答だけが止まる場合(USB はつながったまま)
- 発行した exe を別の PC で動かすこと
- 波形の振幅・周波数・位相の値そのもの(NR-500 で記録しての照合はしていない。目視だけ)。旧アプリの値(10.2Vpp・0.02Hz)は負の電圧が出るので、`Wrappers/Instruments/docs/WF1973実機確認.md` の連続の確認は 5.1Vpp・オフセット 2.55V に変えて行っている
