# LoggerFiles.Tests の照合用データ

テストはこのフォルダーのファイルを読むだけで、書き換えない(書き換えの検査は一時フォルダーのコピーで行う)。

| ファイル | 形式 | 出どころ |
|---|---|---|
| `b.xdt` `c.xdt` `PC.xdt` `CR.xdt` | 旧 WAVE LOGGER 形式(`03 00 03 00`) | XdtConversion `Verification/Fixtures/`(`9809ec3a5352`) |
| `loggerX.xdt` | WAVE LOGGER X 形式(`01 01 03 00`) | 同上 |
| `*.bin` | 上の xdt の全点の正解値(移動平均込み)。先頭に点数・CH 数、続けて CH 番号と double 値 | 同上 |
| `timing-reference.json` | メーカーソフトの CSV から写したサンプリング周期・日時・CH 設定 | 同上 |
| `as110-motion-legacy.xdt` | 旧 WAVE LOGGER 形式 | AS110AllCheck `雛形/AS110動き検査用.xdt`(`982252833f86`) |
| `as110-motion-nrx100.ydt` | NR-X100 の ydt(`NRX100P.DAT`) | AS110AllCheck `雛形/AS110動き検査_NRX100.ydt`(`982252833f86`) |
| `as110-noise-legacy.xdt` | 旧 WAVE LOGGER 形式 | AS110NoiseTest `雛形/AS110ノイズ測定用.xdt`(`c3e75b3b0c05`) |

雛形由来の 3 本は、テストで扱いやすいように名前を ASCII に変えた。中身はバイト単位で同じ。
