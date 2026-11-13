# tools/dev-status(デスクトップの「開発状況.md」を自動で作り直す)

全アプリを 1 行ずつ並べた一覧 `開発状況.md` を、PC のデスクトップに作る仕組み(2026-11-13、人の指示)。
GitHub に push 済み(`origin/main`)の各アプリの `STATUS.md` の先頭を読んで表にするので、**別の PC で作業して push した結果も、1 時間以内にこの PC の一覧へ出る**。AI は使わない。

## 入れ方(PC ごとに 1 回)

このリポジトリのクローンのどれかで、次を実行する。管理者権限は要らない。

```powershell
powershell -ExecutionPolicy Bypass -File tools\dev-status\Install-DevStatus.ps1
```

- `%LOCALAPPDATA%\PSCAutomation\dev-status` に、スクリプトの写し・雛形の写し・読み取り専用のリポジトリの写し(`repo.git`)を置く。作業用のクローンには触らない
- タスク スケジューラに `PSCAutomation-DevStatus` を登録する(ログオン時と 1 時間ごと。PC が止まっていて逃した回は、起きたあとすぐ動く)
- スクリプトや雛形を直したときは、もう一度同じコマンドを実行して写しを入れ替える
- やめるとき: 末尾に `-Uninstall` を付けて実行する(タスクだけ消す。フォルダーは残る)

`-ExecutionPolicy Bypass` は、その 1 回の実行だけスクリプトを動かせるようにする指定で、PC の設定は変えない。

## 一覧の作り方

- `Apps/<アプリ>/STATUS.md`(無ければ `specs/<アプリ>/STATUS.md`)の先頭の、`---` で挟んだ 4 行を読む

  ```
  ---
  app: "AS110AllCheck"
  state: "今の状態を 1 行で"
  next: "次にやることを 1 行で"
  updated: "2026-11-13"
  ---
  ```

- 並びは `updated` の新しい順。`updated` は「状況が最後に変わった日」(yyyy-MM-dd)
- **push していない変更は一覧に出ない**。一覧を手で直しても、次の更新で元に戻る
- このリポジトリに無いアプリの行(ほかのプロジェクトの分など)は、消さずに表の下へ残す
- GitHub から取り込めなかったときは、前に取り込んだ内容で作り、表の下にその旨を出す
- 表の見出しや説明文は `開発状況.template.md` にある(直したら入れ直す)

## うまく動かないとき

- 記録: `%LOCALAPPDATA%\PSCAutomation\dev-status\update.log`
- 手で 1 回動かす:

  ```powershell
  powershell -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\PSCAutomation\dev-status\Update-DevStatus.ps1"
  ```

- `.ps1` は半角英数字だけで書く(Windows PowerShell 5.1 は BOM の無いファイルを Shift_JIS として読むので、日本語を入れると壊れる)。日本語は雛形とこの README に書く

## 確認(2026-11-13)

- 作り直し: 6 アプリの行が `updated` の新しい順に並ぶこと、リポジトリに無いアプリの行が残ること、取り込めなかったときの表示を、出力先を別のフォルダーにして確かめた

## 未確認

- タスク スケジューラからの実行(ログオン時・1 時間ごと)
- 別の PC で push した結果が一覧に出ること
