# M5Stack への書き込み:Arduino IDE の準備と手順(2026-11-05)

`firmware/AS110M5Stack/AS110M5Stack.ino` を M5Stack Basic V2.7 へ Arduino IDE から書き込むための手順。2026-11-05 にこの手順で、人が Arduino IDE を操作して書き込めた。

## 使った環境

| 項目 | 内容 |
|---|---|
| Arduino IDE | 2.3.10 |
| ボード定義 | esp32 by Espressif Systems 3.3.12 |
| ライブラリ | M5Unified 0.2.29、M5GFX 0.2.36 |
| USB ドライバー | CH9102(USB-Enhanced-SERIAL CH9102)。この PC では最初から入っていた |
| ポート | COM6(PC によって番号は変わる) |

## 手順

1. **ボード一覧の URL を足す**
   Arduino IDE の「ファイル → 基本設定 → 追加のボードマネージャの URL」に次を入れる。
   ```
   https://raw.githubusercontent.com/espressif/arduino-esp32/gh-pages/package_esp32_index.json
   ```
2. **ボード定義を入れる**
   ボードマネージャで「esp32」を検索し、**esp32 by Espressif Systems** をインストールする。
   - 全部で約 2 GB あり、取得元はすべて GitHub。回線によっては 30 分ほどかかる
   - 一番大きいのは riscv のコンパイラー(約 670 MB)。ダウンロードが始まるまで数分止まって見えることがあるが、待てば進む
3. **ライブラリを入れる**
   ライブラリマネージャで **M5Unified** をインストールする。一緒に入れるライブラリ(M5GFX)を聞かれたら、全部入れる。
4. **ボードを選ぶ**
   「ツール → ボード → esp32 → **M5Core**」を選ぶ。M5Stack Basic V2.7 はこれ。
   - esp32 3.x では名前が「M5Core」になっている。「M5Stack-Core-ESP32」は古い版での名前
5. **ポートを選ぶ**
   M5Stack を USB でつなぎ、「ツール → ポート」で CH9102 のポート(この PC では COM6)を選ぶ。
   - ポートが出てこないときは、USB ドライバーが入っていないか、充電専用のケーブルを使っている
6. **書き込む**
   `firmware/AS110M5Stack/AS110M5Stack.ino` を開いて、「書き込み」を押す。

## 補足

- PlatformIO の設定(`firmware/platformio.ini`)は espressif32 6.7.0・M5Unified 0.2.26・M5GFX 0.2.33 で、上の Arduino IDE の版とは揃っていない。Arduino IDE で書き込むときは上の版で書き込めている
- 書き込んだ後の画面の表示・ボタン操作・G26 接点・PC アプリとの通信は、この手順書では確認の範囲に入れていない
