# XdtConversion

保存済みの旧 WAVE LOGGER / WAVE LOGGER X の xdt を直接読み、測定値を Excel 雛形へ転送する単機能アプリ。LOGGER 本体ソフトは使わない。
元は GitHub `tad-kawamura/XdtConversion`(`9809ec3a5352`、2026-10-26)。2026-11-04 に PSCAutomation へ移した(出どころと変更点は `docs/移行記録.md`)。

- namespace は `PSCAutomation.Apps.XdtConversion`、対象は `net9.0-windows`(WinForms)。exe 名は元のまま `XdtConversion.exe`
- xdt の読み取りは `Wrappers/LoggerFiles`(`XdtReader`)、xlsx への書き込みは `Wrappers/Excel`(`TemplateWriter`)を使う
- アプリ側に置いているもの: 画面(`MainForm.cs`)、間引きと CH → シート・セルの対応(`ConversionService.cs`)。Excel を起動して表示する部分は共通部品 `Wrappers/Excel` の `ExcelViewer` を使う(2026-11-04 に、アプリ内の `ExcelPresenter.cs` から乗り換えた)

## 開発・実行

Windows、Visual Studio 2022(.NET 9 対応版)、.NET 9 SDK。Excel での表示にはデスクトップ版 Microsoft Excel が必要(Excel 操作は COM の遅延バインディングなので、ビルドには不要)。LOGGER 本体、NI-VISA、NuGet パッケージは不要。

```powershell
dotnet build XdtConversion.slnf -c Release
```

Visual Studio ではリポジトリのルートの `XdtConversion.slnf` を開く。`雛形/雛形.xlsx` は出力・発行先へ自動でコピーされる。

## 操作

1. PC 用・CR 用の保存済み xdt を選ぶ。各欄へ 1 ファイルずつドラッグしても選べる。
2. ログに LOGGER の種別、点数、存在する CH、移動平均の設定が出る。
3. 転送表で、転送チェック・入力(PC / CR)・CH 番号・転送先シート名・開始セルを指定する。
4. 間引き数を指定する。1 は全点、10 は 0 始まりで 0, 10, 20, … 点目を転送する。移動平均を全点にかけてから間引く。
5. 別名の保存先を指定し、「変換・保存 → Excel表示」を押す。
6. 保存が終わると Excel が開く。自動計算を有効にし、以後の数式・グラフの更新は Excel に任せる。計算の完了は監視せず、アプリは再保存しない。Excel は開いたまま。計算後の結果を残すなら Excel で保存する。

初期値(添付の測定値と照合した対応):

| 入力 | CH | 転送先 | 内容 |
| --- | --- | --- | --- |
| PC | 5 | Sheet1!A6 | 変位 |
| PC | 6 | Sheet1!B6 | 流量 |
| CR | 5 | Sheet1!H6 | 変位 |
| CR | 6 | Sheet1!I6 | 流量 |

初期の間引き数は 10(50,000 点から 5,000 点を転送)。LOGGER X など別の CH 構成では、ログに出た CH 番号に変える。
PC だけ転送するときは CR の行のチェックを外す。転送しない列は雛形の値のまま残る。
表の最下行から転送先を増やせる。間引き数とチェックした転送設定は `%LOCALAPPDATA%\XdtConversion\settings.json` に保存し、次回起動時に戻す(移植前と同じ場所)。入力・保存先のパスは保存しない。

## 転送先の扱い

- 開始セルから下へ数値として書く。CH 名・日時・見出しは書かない。
- 指定列の開始セル以降の既存値を消してから書く(短いファイルに変えても過去の末尾が残らない)。この範囲は測定値専用にする。
- 指定範囲に数式がある列はエラーで止める。別の列の数式、書式、グラフ、その他のパーツは保持する。
- 同じシートの同じ列へ複数の設定で書く指定は受け付けない。
- 既存のファイルへは上書きしない。雛形自体も更新しない。
- 数式・グラフの参照範囲は雛形に従う。今の雛形は 5,000 点(Sheet1 の 6〜5005 行)を参照する。間引き 1 で 50,000 点にしても、雛形の数式・グラフは自動では広がらない。

## 時間の記録

| ログ | 範囲 |
| --- | --- |
| ① 変換・保存時間 | xdt の再読込、選択 CH のスケーリング・移動平均、間引き、雛形の読込、数値の書込、別名保存 |
| ② Excel 起動・表示時間 | Excel の起動、ブックの読込、自動計算の設定、表示要求まで(計算・描画の完了は含まない) |

① は Excel を起動せずに書くので、計算結果とグラフのキャッシュは雛形の時点のまま。Excel の表示に失敗しても ① で保存したファイルは残る。Excel がない PC では ① だけ成功し ② でエラーになる。既存の Excel とは別のインスタンスを使う。

## オフライン PC 用の発行

Visual Studio でプロジェクトを右クリック → 発行 → フォルダーで、付属の `FolderProfile` を選ぶ(Release / win-x64 / 自己完結 / 単一ファイル)。発行フォルダー全体(exe と `雛形` フォルダー)をコピーする。Excel は持ち運び先の PC に必要。

```powershell
dotnet publish Apps/XdtConversion/XdtConversion.csproj -p:PublishProfile=FolderProfile
```

## 検証

`Verification/` は GUI と Excel COM を使わない検証(コンソールアプリ)。リポジトリのルートで実行する。

```powershell
dotnet run --project Apps/XdtConversion/Verification -c Release
```

- 同梱の雛形 xlsx と `Verification/Fixtures` の PC / CR の xdt を、既定の転送設定・間引き 10 で変換し、転送した 4 列 × 5,000 点を参照値(`*.bin`、メーカーソフトで出した全点の値)と照合する。数式・グラフなど対象外のパーツが変わらないこと、末尾消去・同名停止・間引き数 0・xdt 未指定・数式列・不正 CH・破損ファイルで止まることも確かめる。
- `-- --compare-old <旧アプリが出力した xlsx>` を付けると、旧アプリの出力と転送値を文字列として比べる。旧リポジトリの `XdtConversion/雛形/…_変換_20261008_144212413.xlsx`(旧アプリの出力例。PSCAutomation には入れていない)で 20,000 セルの一致を確認済み(2026-11-04)。
- xdt の読み取りそのものは `Wrappers/Tests/LoggerFiles.Tests`、`TemplateWriter` は `Wrappers/Tests/Excel.Tests` で確かめている。

## 雛形

- `雛形/雛形.xlsx`: 転送先の雛形。差し替えるときはこのファイルを置き換える。
- `雛形/*.xdt`: 雛形に合わせたサンプルの xdt(`Verification/Fixtures` の PC.xdt / CR.xdt と同じ内容)。
- `雛形/memo.txt`: 雛形の出どころのメモ。

## 確認状況

- 確認(2026-11-04): Release / Debug ビルド(警告 0)、`Verification` の成功、旧アプリの出力との一致、exe が起動して画面が出ること。
- 確認(2026-11-04、`ExcelViewer` への乗り換え後): 全体 `PSCAutomation.sln` の Release ビルドと `XdtConversion.slnf` の Debug ビルド(警告 0)、`Verification` の成功。`ExcelViewer` そのものは `Wrappers/Tests/Excel.Tests` で Excel を起動して確かめている。
- 未確認: 画面操作(ファイル選択・ドラッグ・転送表の編集・設定の保存と復元)と、画面から変換して Excel が開くまで(乗り換え後のアプリでは未実施)。
