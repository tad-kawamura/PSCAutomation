# AS100201SwitchController 仕様(2026-11-04 移植時に整理)

移植前のリポジトリ AS100201SwitchController(`5eb5b63`、2026-10-28)の README・コードの動きを、雛形の形にまとめ直したもの。移植前に実機で使われていた動きはそのまま残した。**移植時に人と決めて変えたこと**は「移植で変えたこと」に分けて書く。

- 状態: **仕様確定**(2026-11-04、人の確認)。移植・実機確認済み(確認した範囲は `Apps/AS100201SwitchController/README.md`)

## 目的

AS100-201 の測定で、人が WF1973 の位相合わせと WAVE LOGGER の開始・停止を手で行っていた作業を、治具のスイッチだけでできるようにする。

## 使う機器

| 機器 | 役割 | 接続 | 使う wrapper |
|---|---|---|---|
| Arduino(UNO R4 Minima 等、スケッチ `firmware/ArduinoSwitchAndReadyLed`) | スイッチ 4 つ(PC_go・CR_go・F8・sw4)と READY LED | USB シリアル(COM ポートを自動で探す) | `Wrappers/Arduino`(取り決めはアプリの `As100201ArduinoProtocol`) |
| NF WF1973 | 連続出力(三角波)を、PC / CR の位相で出し直す | USB(VISA) | `Wrappers/Instruments` の `Wf1973ContinuousSession` |
| KEYENCE NR-500 / NR-X | データロガー。測定の開始・停止 | **USB だけ**。旧 WAVE LOGGER / WAVE LOGGER X(両方入っていれば画面で選ぶ。旧で本体が見つからなければ X に切り替えて NR-500 → NR-X の順に探す) | `Wrappers/WaveLogger.Automation`(`Lifecycle = ReferenceOnly`)、`Wrappers/Common` の `Nr500Connection` |

- NR-X の Wi-Fi は使わない(移植で変えたこと 1)。SSID・IP の置き場は要らない
- Excel・ファイルの保存は使わない。測定データの保存は LOGGER 側の設定に従う

## 入力

- 雛形(xdt / ydt): 人が画面へ 1 件ドラッグする。アプリには同梱しない。LOGGER の `OpenFile` が開ければよい
- 設定値(コードの定数。`As100201/As100201WaveformSettings.cs`): 振幅 10.2Vpp、周波数 0.02Hz、オフセット 0V、PC 位相 15°、CR 位相 165°、波形の形 = 三角波(対称 50%)

## 出力

- WF1973 の出力、LOGGER の測定(保存は LOGGER の設定)
- 画面のログ。異常があったときは、直近 1 分の記録ごと `Logs\ArduinoIncident_*.log`(exe の隣。書けなければ `%LOCALAPPDATA%\AS100201SwitchController\Logs`)。復旧後 30 秒まで同じファイル。自動削除なし

## 手順

1. 起動すると接続確認(Arduino の確認と、WF1973・LOGGER/NR 本体の確認を並行)。「確認」ボタンでやり直せる
   - WF1973: 出力 OFF → 三角波(RAMP・対称 50%)→ オフセット 0V → バースト OFF → `*OPC?` で設定完了を確かめる
   - WF1973 が接続済みのときの「確認」: `*IDN?` で生存確認したあと、**波形の形(三角波)だけ**を送り直す(`*OPC?` で確認)。出力の ON / OFF・振幅・オフセット・位相は変えない(次の PC_go / CR_go で送る)。動作中に別のアプリや前面パネルで形を変えられても、「確認」を押せば戻る
   - LOGGER: COM で起動・初期化し、NR 本体を USB で確認
2. 雛形をドロップ → LOGGER で開く → READY LED 点灯 → LOGGER を前面に出す。それまではアプリを最前面に保つ
3. スイッチ
   - PC_go / CR_go: 振幅・位相・周波数 → オフセット → 位相の初期化(`:SOURce:PHASe:INITiate`)→ 初回だけ出力 ON。出力中は OFF にしない。コマンドの間に次の押下が来たら残りを送らず、最後の押下の位相でやり直す。**波形の形は押すたびには送らない**(出力中に形を送ったときの動きを確かめていないため。形は接続時と「確認」で送る)
   - F8(と画面の「測定開始（確認用）」): LOGGER が開始できる状態なら Start を 1 回。測定中などは開始しない
   - sw4: LOGGER へ Stop(状態が読めなくても送る)
4. 5 秒ごとに WF1973 の `*IDN?` と NR 本体の `GetUnitInfo` で生存確認

## 異常時

- 機器が見つからない: 接続確認で全機器の失敗理由をまとめて表示。READY LED は点かず、スイッチは受け付けない
- Arduino の定期応答(250ms ごと)が 1 秒止まる: 操作を受け付けない。3 秒止まる・切断: 2 秒おきに再接続を試す。復旧後 1 秒は操作を捨てる。切断前・復旧前の押下は実行しない(押し直す)
- Arduino だけの切断では、WF1973 の出力と LOGGER の測定を変えない。READY LED は PC から消せなくても、Arduino が最後の READY_ON から 3 秒で消す
- WF1973 / NR 本体の生存確認に失敗: その機器を未接続にして READY LED を消す。「確認」でやり直す
- LOGGER 側でファイルを閉じた: 雛形の登録を外す(再登録が要る)
- スイッチの処理に失敗: ログと最前面のメッセージボックス
- 終了時: READY LED を消し、Arduino・WF1973 を閉じ、LOGGER は止めずに COM の参照だけ外す(`ReferenceOnly`)

## 移植で変えたこと(2026-11-04、人の指示)

1. **NR 本体は USB だけにした**。移植前は画面で USB / Wi-Fi を選べ、NR-X の Wi-Fi プロファイル(`NRX_E8FDF803634C`、192.168.100.100:24682)をアプリの設定クラスに持っていた。接続方式の選択・Wi-Fi プロファイルの選択・「元のWi-Fiへ戻す」ボタン・終了時の Wi-Fi の復帰を外した。Wi-Fi が要るようになったら、移植前のコード(`As100201ConnectionSettings.cs`、`MainForm.Connections.cs` の Wi-Fi 分岐)と `docs/capabilities.md`「NR-X を Wi-Fi で使うとき」を見て足し直す
2. **接続時に WF1973 の波形の形(三角波・対称 50%)を送るようにした**。移植前は形を送らず本体の設定のまま出していた。移植の実機確認で、直前に AS110NoiseTest が DC にした WF1973 では PC_go / CR_go を押しても DC のままだったため。同じ WF1973 を別のアプリで使った後でも正しく出る。wrapper に `Wf1973ContinuousSession.Connect(GeneratorWaveform)` を足した(既存の `Connect()` の動きは変えていない)。接続済みのときの「確認」でも形だけ送り直す(`SetWaveform`、同日追加)

## 細部

- スイッチ名と Arduino のピン: D1 = READY LED、D2/D3 = F8 の LED 給電 / 入力、D4/D5 = sw4、D6/D7 = CR_go、D8/D9 = PC_go。チャタリング対策 30ms(スケッチ)
- 確認文字列: PC → `AS100201_PING`、Arduino → `AS100201_READY:1`。定期応答 `DIAG_FRAME:<連番>:<millis>`、起動通知 `DIAG_BOOT:1`
- アプリ固有の異常種別: `HEARTBEAT_STALE`、`FRAME_GAP`、`PROTOCOL_RECOVERY`
- LOGGER の開始は毎回 Start(トリガ・状態待ち・再試行なし)。開始できる状態は `GetState` が 1・11 のとき。開始直後 500ms は状態を読まない
- 出力は 10.2Vpp・オフセット 0V で −5.1〜+5.1V。**負の電圧は問題ない**(2026-11-04、人の確認)
- 位相を変えて出し直すと段差が 2 回出る(位相の値を書き換えた時点でその差だけずれ、約 80ms 後に位相の初期化で設定の位相からやり直す。`Wrappers/Instruments/docs/WF1973実機確認.md`)。**使用上の問題はないので、このままにする**(2026-11-04、人の確認)。出力を一度 OFF にすれば段差は 1 回になるが 0V の切れ目が入る、などの対策の候補は検討だけした
- 波形の形は三角波(対称 50%)で固定(人の回答)。正弦波などが要るときは `GeneratorWaveform` に足す(wrapper の追加)

## 未確定・未確認

- 仕様として未確定の項目はない
- 実機で未確認の項目は `Apps/AS100201SwitchController/README.md`「確認」
