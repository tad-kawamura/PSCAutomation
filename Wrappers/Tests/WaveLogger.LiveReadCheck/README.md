# WaveLogger.LiveReadCheck(測定中の値の取り出しを実機で確かめる)

測定中の LOGGER から、**xdt を保存せずに**値を取り出せるかを確かめる確認ツール(2026-11-10 作成)。
AS100201SwitchController で「測定中も波形から助言を刻々と出す」ための下調べ。

**実機ではまだ動かしていない**(ビルドと、機器なしの `--dry-run` だけ確認済み)。

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

- 部品(`WaveLoggerSession`)は変えていない。値の取り出しは部品に無いので、部品が持っている LOGGER の参照をリフレクションで借りてメーカー API を呼んでいる。**確認用だけの手段**で、アプリでは使わない。使えると分かったら、部品に関数を足す(全アプリに影響するので人に確認してから)
- 読み取りは LOGGER の部品と同じ画面のスレッド(STA)から `await Task.Delay` をはさんで呼ぶ。アプリでタイマーから呼ぶときと同じ条件
- 取り出す値は換算前(電圧など)。助言に使うときは雛形の換算係数(`XdtFile` の `ChannelInfo.Gain` / `Offset`)で換算する

## 確認の記録

- 2026-11-10: ビルド、`--dry-run`(学習データの AS100-203 SN148 の xdt で、10ms・5100 点・CH1〜3, 5, 6 と換算係数を表示)。**実機は未確認**
