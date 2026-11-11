# WaveLogger.LiveReadCheck(測定中の値の取り出しを実機で確かめる)

測定中の LOGGER から、**xdt を保存せずに**値を取り出せるかを確かめる確認ツール(2026-11-10 作成)。
AS100201SwitchController で「測定中も波形から助言を刻々と出す」ための下調べ。

**2026-11-11 に NR-500・旧 WAVE LOGGER で実機確認した**(下の「確認の記録」)。WAVE LOGGER X・NR-X は未確認。

## 何を確かめるか

WAVE LOGGER のメーカー API(説明書 `C:\Program Files (x86)\KEYENCE\WAVE LOGGER\WLPROAuto_M.pdf` 7.4 章)には、値を取り出す関数がある。共通部品 `WaveLoggerSession` にはまだ入っていない。

| 関数 | 説明書の説明 |
|---|---|
| `GetDataCount` | PC に読み出されたデータ数 |
| `GetArrayData` | PC に読み出されている計測データを配列で取る(換算前の値) |
| `GetData` | 指定位置の 1 点(換算前の値) |
| `GetCurrentData` | 計測中の NR-500 で常に更新されている最新値 |

旧 WAVE LOGGER(`WaveLogger2.tlb`)にも WAVE LOGGER X(`WaveLoggerX.tlb`)にも同名の関数がある(型情報で確認)。

このツールは雛形を開いて 1 回収集を始め、測定中に一定間隔でこれらを呼び、次を出す。

1. 呼び出しにかかった時間(1 回の読み取り全体・関数ごとの平均と最大)
2. PC に届くまでの遅れ(記録開始からの時間と、届いた点数の差)と、点数が何点ずつ・何秒おきに増えるか
3. 停止後に保存した xdt と、測定中に取り出した値の 1 点ずつの突き合わせ(点数・不一致の数)
4. 雛形の換算(スケーリング)で換算した値と、保存した xdt の換算値の差(アプリが雛形から係数を読めば済むか)
5. `GetCurrentData` が測定中に無効値を返さないか

`GetArrayData` で受け取れない(例外・戻り値が合わない・`GetData` と値が合わない)ときは、自動で `GetData` の 1 点ずつに切り替え、そのことを表示する。

## 使い方

LOGGER(旧 WAVE LOGGER)と NR-500 を USB でつないだ PC で、WAVE LOGGER を閉じてから実行する。収集は雛形の設定どおりに 1 回行う(発振器や弁は動かさない。波形の値は何でもよいが、値が動いているほうが突き合わせの確認になる)。

```bash
dotnet run --project Wrappers/Tests/WaveLogger.LiveReadCheck -- --template "<雛形.xdt>"
```

| オプション | 既定 | 意味 |
|---|---|---|
| `--template <xdt>` | (必須) | 開く雛形。AS100201SwitchController の雛形(10ms・5100 点 = 約 51 秒)で試す |
| `--interval <ms>` | 200 | 読み取りの間隔 |
| `--stop-after <秒>` | なし | この秒数で Stop を送る(長い雛形を途中で止める) |
| `--max-seconds <秒>` | 120 | 止まらないときに Stop を送るまでの秒数 |
| `--unit <番号>` | 1 | メーカー API のユニット番号。チャンネルは雛形の CH 番号 − 1 を使う |
| `--per-point` | なし | 最初から `GetData` の 1 点ずつで取る |
| `--out <フォルダ>` | `%TEMP%\WaveLogger_LiveReadCheck` | 保存した xdt と、読み取り 1 回ごとの記録(CSV)の置き場 |
| `--logger legacy\|x\|auto` | legacy | 使う LOGGER |
| `--usb-id <0〜3>` | 0 | NR-500 の USB 機器 ID |
| `--dry-run` | なし | 雛形のチャンネル・周期・換算だけ表示する(機器なし) |

終了コードは、突き合わせがすべて合えば 0、合わなければ 1、引数の誤りは 2。

## 作りの注意

- 2026-11-11 に部品(`WaveLoggerSession`)へ `ReadRawData` / `GetCurrentRawValue` を足した(人の承認)。まとめて取る既定の読み取りと最新値は、その関数を使う。表の「GetDataCount」は 1 点ずつのときだけ数え、まとめて取るときは「値の取り出し」に含まれる
- 1 点ずつの `GetData`(`--per-point` と、まとめて取れないときの切り替え)と、まとめて取った値の先頭・末尾を `GetData` で照合する部分は、部品に無いので今も LOGGER の参照をリフレクションで借りている(**確認用だけの手段**で、アプリでは使わない)
- 読み取りは LOGGER の部品と同じ画面のスレッド(STA)から `await Task.Delay` をはさんで呼ぶ。アプリでタイマーから呼ぶときと同じ条件
- 取り出す値は換算前(電圧など)。助言に使うときは雛形の換算係数(`XdtFile` の `ChannelInfo.Gain` / `Offset`)で換算する

## 確認の記録

- 2026-11-10: ビルド、`--dry-run`(学習データの AS100-203 SN148 の xdt で、10ms・5100 点・CH1〜3, 5, 6 と換算係数を表示)
- 2026-11-11: **実機**(NR-500 USB・旧 WAVE LOGGER)。AS100201SwitchController の雛形(`雛形_AS100_201流量調整用_650kPa_0_02Hz_10Vpp.xdt`、10ms・5 CH)で `--stop-after 20`、読み取り間隔 200ms(既定)。終了コード 0
  - 測定中に `GetArrayData`(まとめて)で取り出せた。`GetData` への切り替えは起きなかった
  - PC に届くまでの遅れ(記録開始からの時間 − 届いた点数 × 10ms): 平均 0.07 秒、最大 0.17 秒。点数は約 0.2 秒おきに約 21 点ずつ増える(読み取り間隔に合わせて届く)
  - 1 回の読み取り全体(状態・点数・5 CH の取り出し・現在値): 平均 12ms、最大 88ms(最初の 1 回 34ms、`GetState` の最大 62ms が 1 回)
  - `GetCurrentData` は測定中 94 回とも無効値なし
  - 停止後に保存した xdt(2020 点)と、測定中に取り出した値が 5 CH とも全点一致。雛形の換算係数で換算した値と xdt の換算値の差は 0
  - 弁・エア・発振器はつないでいない(値は 0 付近のノイズ)。ノイズでも点ごとに値が変わるので突き合わせにはなるが、大きく動く波形では見ていない
  - 未確認: WAVE LOGGER X、NR-X、雛形の最後(約 51 秒)まで記録して自動で止まったとき、AS100201SwitchController の画面(Arduino・WF1973 の処理と同じ UI スレッド)から読んだとき、読み取り間隔を短く(例: 50ms)したとき
- 2026-11-11(部品に関数を足したあと): 同じ条件で `--stop-after 10`。部品の `ReadRawData` でまとめて取れ、`GetData` との照合も合った。`ReadRawData` 1 回(5 CH)平均 6.1ms・最大 17.8ms、遅れ平均 0.07 秒・最大 0.14 秒、保存した xdt(1012 点)と 5 CH 全点一致。終了コード 0。`GetCurrentRawValue` は 1 チャンネルごとにアクティブなファイルを取り直すので、5 CH で平均 7.5ms(前は 1.9ms。アプリでは使っていない)
