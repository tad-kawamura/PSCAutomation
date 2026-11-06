# AS110AllCheck

AS110の動き検査、データ調整、データ取り、Excel/PDF/LOGGER保存、漏れチェック、完了S/N管理を行うWindows Formsアプリです。

`AS110BoltCheck_ver3`（2026-10-19版）の実装を元に、旧リポジトリ `AS110AllCheck` で整理したものを、2026-11-05 にこのリポジトリへ移しました（移植元 `982252833f86`）。名前空間は `PSCAutomation.Apps.AS110AllCheck`（移植前は `AS110BoltCheck` / `XdtConversion`）、実行ファイル名は `AS110AllCheck.exe` のままです。移植で変えたことは `docs/移行記録.md`（リポジトリ直下）の「アプリ: AS110AllCheck」、仕様は `specs/AS110AllCheck/SPEC.md` を参照してください。

## 開く・ビルドする

Windows、.NET 9 以降の SDK を使います。リポジトリ直下の `AS110AllCheck.slnf`（アプリ・検査・使う wrapper だけ）を開いてください。wrapper を変えたときは全体の `PSCAutomation.sln` をビルドします。

```powershell
dotnet build AS110AllCheck.slnf -c Release
dotnet run --project Apps/AS110AllCheck/Tests/Verification/AS110AllCheck.Verification.csproj -c Release
```

実行にはNI-VISA等のVISA COM環境、WAVE LOGGER / WAVE LOGGER Xと対応ドライバー、Microsoft Excel、M5StackのUSB接続環境が必要です。発振器はWF1973 / SDG1032X、LOGGERはNR-500 / NR-X（USB・Wi-Fi）に対応します。VISA COM の参照は `Wrappers/Instruments` が持ちます（非標準の配置は `-p:VisaInteropPath="C:\path\to\Ivi.Visa.Interop.dll"`）。

## 構成

| 配置 | 役割 |
| --- | --- |
| `Program.cs` | 起動入口。異常ログの設定（`As110/As110Application.cs`）のあと MainForm を開く。MainForm は画面を作る前に LOGGER 本体ソフトの起動だけ先に始め（`BeginLoggerStartup`）、画面準備・発振器確認と重ねる |
| `UI/` | MainForm、画面配置、接続・測定・保存の操作、校正画面 |
| `As110/` | AS110の設定、測定定義、M5Stack通信、校正値と校正記録、NR-X の Wi-Fi プロファイル一覧（`As110ConnectionSettings.cs`） |
| `Production/` | 工程状態、保存・漏れチェック・完了S/Nの保護 |
| `Excel/` | 作業ブックの操作（`ExcelWorkSession`。Excel の操作は `Wrappers/Excel` の部品）、転送設定、点数圧縮 |
| `GlobalUsings.cs` | 使う wrapper の名前空間 |
| `Tests/Verification/` | 実機・Excel を使わない工程・校正・変換の検証 |
| `Tests/AppShutdown/` | 終了処理（機器の停止・解放の失敗を含む）の検証 |
| `Tests/SaveAvailability/` | 実際の MainForm で保存ボタン・M5Stack の許可・前面表示・校正画面を確かめる（Excel を使う項目あり） |
| `Tests/ExcelHeader/` | 実際の Excel で作業ブックの再利用・事前準備・判定・図形の S/N 書き換えを確かめる |
| `firmware/` | M5Stack Basic V2.7用スケッチ、PlatformIO設定 |
| `雛形/` | LOGGER雛形、型式別参照波形、Excel雛形 |
| `docs/` | 適用・操作手順と、移植前の変更・確認の記録（記録の中のパスは移植前のもの） |
| `docs/legacy/` | 元のver3説明・過去の整理資料（原文） |

機器との通信・ファイル形式の読み書きは wrapper を使います: `Wrappers/WaveLogger.Automation`（LOGGER）、`Wrappers/Common`（NR-500 / NR-X の本体確認・Wi-Fi 切替・エラー表示・前面表示・PC 遅延観測）、`Wrappers/Arduino`（M5Stack のシリアル通信・異常ログ）、`Wrappers/Instruments`（発振器）、`Wrappers/LoggerFiles`（xdt・校正の書き換え・NR-X の CSV）、`Wrappers/Excel`（雛形への書き込み・Excel の操作）。

## 発行

exe は単一ファイル（自己完結・win-x64）です。`雛形` フォルダーは exe に入れず、発行先の exe の隣へコピーします（人が差し替えるため）。

現場へ渡す「データ調整」は専用プロファイルで発行します。発行のたびに版を上げ、発行先フォルダー名・exe名を「データ調整verX.YY」にそろえます（例：ver1.03＝バージョン1.0.3）。

```powershell
dotnet publish Apps/AS110AllCheck/AS110AllCheck.csproj -p:PublishProfile=DataAdjustment
```

- 版の上げ方: `Properties/PublishProfiles/DataAdjustment.pubxml` の `PublishDir`（`bin\データ調整verX.YY\`）・`AssemblyName`・`AssemblyTitle`・`Product` を同じ名称にそろえ、`Version` を `X.0.YY`、`FileVersion` を `X.0.YY.0` にします。次の版は小数第2位を1つ上げます（1.03→1.04）。上の桁を上げるときは人に確認します。同じ版の発行物を上書きしません。
- 版を上げたプロファイルとこの README を commit・push してから発行し、発行フォルダーに `発行記録.txt`（発行元コミットID、版、同梱ファイルのSHA256、確認済み・未確認の範囲）を残します。
- 現在の版は「データ調整ver1.03」（移植前の旧リポジトリで発行したもの）。このリポジトリからはまだ発行していません。次に発行するときは ver1.04 にします。
- 旧「データ調整プロトタイプver1・ver2」の発行設定は移していません（旧リポジトリにあります）。

ふつうの発行（exe 名 `AS110AllCheck.exe`、`bin\Release\publish\`）は `FolderProfile` です。

```powershell
dotnet publish Apps/AS110AllCheck/AS110AllCheck.csproj -p:PublishProfile=FolderProfile
```

GitHubへのpushは運用PCへの配布ではありません。オフラインの運用PCへは、確認済みの発行物、雛形、必要な外部ソフトを別途反映します。M5Stackのスケッチを変えたときは本体への書き込みも別途必要です。

## 操作・設定

詳細は [ver3の適用・操作手順](docs/operation.md) を参照してください。元資料は [00_ver3_適用方法.txt](docs/legacy/00_ver3_適用方法.txt) に保存しています。

- 動き検査、データ調整、データ取りの3モードを使います。
- 起動すると接続確認（M5Stack・発振器・LOGGER／NR本体）が自動で走り、終わるまでボタンは押せません。LOGGER 本体ソフトの起動（旧 WAVE LOGGER で 7〜8 秒）は画面を作る前に始めて画面準備と重ねているので、起動から操作できるまでは LOGGER の起動時間とほぼ同じです。ログ欄の「起動時間：プロセス起動から接続確認終了まで」で確かめられます。
- 上段のボタンの下に「次にすること」を 1 行出します。データ調整（動き検査では測定開始）のボタンが押せない間は、その理由と次の操作を赤字で（例：「校正値を入力してください」「型式（参照波形）を選んでください」「「保存先選択」で保存先を選んでください」「測定開始S/N を入力してください」「M5Stack が未接続です」）、押せるときは次の操作を緑字で出します。型式（参照波形）未選択・データ取りの保存先未選択・測定開始S/N が完了済み（保護中）のときはボタン自体を押せなくします（完了済みは S/N を入れた時点で完了記録を読んで判定し、ポップアップは出しません。2026-11-06）。
- 起動後はモード→校正値→型式（参照波形）の順に進みます。型式は「型式（参照波形）」の一覧で .xyb を選ぶと、そのファイル名に含まれる型式名（AS110-035 など）から決まります。「型式」の欄と「型式を適用」ボタンはありません（2026-11-06 に廃止）。型式名を含まない .xyb はデータ調整・データ取りでは選べません（動き検査では型式を使わないので選べます）。接続確認とモード選択の両方が済むと校正画面（スケーリング）が自動で開きます（モードを先に選んだときは接続確認が終わった時点で開きます）。スタンドの選択はありません（2026-11-06 に廃止）。モード欄は選ぶまで赤文字で「未設定」と表示します。型式・Excel転送などの操作欄もモードを選ぶまで表示しません。操作欄は上から「保存先選択・保存先」→「開始S/N・最後S/N（保存先フォルダー名から。入力不可）」→「測定開始S/N」→「完了番号の保護と解除・転送設定」→「Excel転送などのボタン」→「状態」の順です。保存先・開始S/N・最後S/N・測定開始S/N・完了番号の保護と解除と、保存・+30mA・漏れ試験実施・合格のボタンはデータ取りを選んだときだけ表示します（転送設定はデータ調整でも表示）。
- NR-X は USB と Wi-Fi のどちらでもつなげます。Wi-Fi のプロファイル（SSID・IP）は `As110/As110ConnectionSettings.cs` の `NrWifiProfiles` にあります。機器を増やすときは1行足します。パスワードは Windows の Wi-Fi プロファイルが持つので、ここには書きません。
- NR-500のXDTでは、校正画面「スケーリング」で「電流」「圧力」の枠ごとに 1（低点・ゼロ付近）と 2（高点）の計測値（V）と表示値（mA／kPa）の8項目を入力し、「適用」を押すと、その値を作業用XDTへ反映してLOGGERへ読み込みます（元の雛形は保持）。並びは現場の校正記録用紙と同じ縦型です。例：圧力の 2 に `5.503 V＝539.5 kPa`、別の日には `5.498 V＝540.5 kPa`。初期値は電流・圧力ともそのモードの雛形に入っている値で、前回の適用値は呼び出しません（校正は毎回入れ直す。人の判断）。キャンセルしたときは「校正値を入力」ボタンから開き直します。
- 校正記録は、データ取りの保存先フォルダーの中の `校正記録` フォルダーに `校正_yyyy-MM-dd_HH-mm-ss.txt`（適用日時・モード・本体・雛形・電流と圧力の2点ずつ）として 1 件 1 ファイルで残します。校正は保存先を選ぶ前に行うので、校正の適用と保存先の選択の両方がそろった時点で書き、校正を入れ直したとき・保存先を選び直したときもそのつど書きます。データ調整（保存先が無い）と NR-X（校正しない）では残りません。exe の隣や `%LOCALAPPDATA%` には置きません（2026-11-06 に人の判断で変更。以前の `%LOCALAPPDATA%/AS110AllCheck/校正記録.txt` への追記はやめました。過去のファイルは消しません）。[記録形式](docs/calibration-history.md)
- 試験前の再入力は「校正値を変更」から行います（現在の適用値が入った状態で開きます）。その右隣の「設定変更（測定を破棄）」で、未保存の測定を破棄して校正値・型式・S/Nを変えられます（保存後は「再測定へ戻る」になります）。適用後は型式（参照波形）を再選択してください。測定中や測定結果が残っている間は変更できません。必要なら先に「設定変更（測定を破棄）」を行ってください。校正記録を保存できない場合はエラーを表示し、設定完了として測定を開始させません。保存先の選択で校正記録を書けない保存先は受け付けず、選ぶ前の保存先に戻します。
- 校正入力は小数点 `.` を使用し、2（高点）の電圧・表示値をそれぞれ 1（低点）より大きくします。NR-XのYDTはGUI校正の対象外（保留）で、校正画面を出さずに雛形の値のまま読み込みます。YDTの電流・圧力のGUI校正対応は今後の宿題です。対応範囲・完了条件と実機確認事項は [XDT校正の確認記録](docs/xdt-calibration.md) を参照してください。
- Excel転送後は、転送設定の「判定セルのシート」（既定は「sheet1」）を前面に表示します。Windowsが切り替えを許可しない場合はログに案内を表示します。
- 同じExcel雛形・転送先・転送点数なら、再測定時も前回の作業ブックを保持し、転送時に測定値だけを一括更新します。型式・S/N・供給圧力・測定日は変更時だけ更新します。ブックを閉じた場合や雛形・転送先・点数を変更した場合は作り直します。再計算・合否確認は従来どおり行います。[検証記録](docs/excel-workbook-reuse.md)
- 再利用時の重複するブック確認・入力シート再計算を省き、隣接する電流・圧力列はまとめて更新します。ログには処理内訳に加え、Excel前面表示、合否ポップアップ表示（Excel前面表示を含む）、転送開始から表示処理完了までの合計時間を記録します。[高速化と確認範囲](docs/excel-transfer-optimization.md)
- 同梱の `雛形.xlsx` は、判定に使わない集計列（S〜X列）の不要な2万行と重複する検索・平均計算を整理し、計算結果を変えずに軽くしました。参照波形の読み込み操作後と合格確定後には、LOGGERの前面表示中にExcelを表示せず作業ブックを準備し、最初の転送を短くします。[変更と検証](docs/excel-template-preparation.md)
- データ調整・データ取りではExcel転送後に合格／不合格を表示します。約0.4秒表示したあと自動で閉じ、Excelの判定シートを前面に出します（OKボタンはありません）。待たずにEnterや、PCまたはM5Stackのデータ調整・Excel転送ボタンでも閉じて操作できます。データ取りで合格済みの場合は「合格しているけど、またやるの？」と確認し、「はい」で再実行、「いいえ」・Enter・閉じるで取り消します。合格表示をEnterで閉じた後も再実行時には確認します。
- Excel転送後は、最小化を解除したExcelの判定シートを背後に、合否ポップアップを最前面に表示します。ポップアップを表示するとキーボード入力先も移し、Enterはポップアップで受け取ります。Enter、または約0.4秒後の自動クローズで閉じるとExcelの判定シートへ表示を戻します。データ調整の実行時は毎回LOGGERの測定画面を前面に表示します（データ取りモードのデータ調整ボタンも同様）。
- データ調整開始時は前回のExcelを操作せず保持します。Excelが応答待ちの場合も所有参照を維持し、次の転送で同じExcelを再試行します。[二重起動につながる参照破棄の修正記録](docs/excel-session-retention.md)
- データ調整では型式（参照波形）だけを選び、測定後にExcelへ転送して確認します。S/N入力は表示せず、ExcelのS/Nは「-」です。正式保存・完了番号管理は行いません。転送用の内部一時ファイルは使用します。
- データ取りでは型式（参照波形）・保存先・測定開始S/Nを設定し、測定後にExcelへ転送します。保存先とS/Nの欄はデータ取りを選ぶと表示されます。開始S/N・最後S/Nは保存先フォルダー名から自動で入り（緑字・入力不可）、人が入れるのは「測定開始S/N」（今から測る番号。途中から再開するときはその番号）だけです。測定開始S/Nは起動のたびに空から始まり、空のあいだは欄の右に赤字で「入力してください」と出て測定開始できません（Excelの事前準備も入力後に始めます）。合格でS/Nを1加算して最後S/Nを超えたら「最後のS/N まで終わりました」と表示し、保存先を選び直して測定開始S/Nを入れ直すまで測定開始できません。
- Excel転送でExcelが操作を拒否した場合は「Excelの入力をEscで解除してから、もう一度Excel転送してください」と表示します。入力解除後に再転送してください。詳細なエラーコードはログに残します。
- 機器の通信エラーで停止・解放に失敗しても、残りの終了処理を続けてアプリを閉じます。処理中の終了要求は、処理が戻ってから引き継ぎます。停止を確認できなかった機器は診断ログの `SHUTDOWN_ERROR` に記録します。[終了処理の変更と検証](docs/application-shutdown-errors.md)
- 診断ログ（M5Stack通信の異常記録）は exe の隣の `Logs` に保存します。書けないときは `%LOCALAPPDATA%/AS110AllCheck/Logs` に保存します（移植前は `%LOCALAPPDATA%/AS110BoltCheck/Logs`）。
- 発振器の出力OFFを送信できなかった場合は「出力状態不明」と表示し、測定・漏れ試験・合格を止めます。本体で出力OFFを確認し「確認」で再接続してください。状態監視の通信失敗1回では発振器の接続を手放しません。[変更と確認範囲](docs/generator-output-stop.md)
- 正式保存はExcel・PDF・LOGGERの3種類です。漏れチェックは正式保存後に行い、一式保存と漏れチェックの正常終了後に合格操作が可能になります。
- 保存後のボタンはA「+30mA」、B「漏れ試験実施」、C「合格」です（画面では「合格」は下段の左、「設定変更（測定を破棄）」の隣にあります）。Aで従来のDC +5V出力だけを開始・保持し、BでLOGGERを前面に表示して同じ雛形で収集を開始します。収集開始から10秒後に両方を停止し、LOGGERの完全停止を確認します。合否は人が判断し、この収集結果は正式保存へ上書きしません。
- 完了記録は保存先の `完了記録.txt` に残ります。測定データと一緒に維持してください。
- 保存先フォルダーの名前は `SN開始-最後`（例: `SN71360-71460`）を含めます。保存先を選ぶと、フォルダー名から開始S/Nと最後S/Nを緑字で入れます（選び直すたびに入れ替え。範囲が無いフォルダーなら空）。測定開始S/Nがその範囲の外か空、またはフォルダー名に範囲が無いなら、保存先の行の下に赤字で「保存先フォルダーを確認してください」と出し（入力中も更新）、正式保存はポップアップで止めます（何も書き込みません）。
- 保存先を選ぶと `Excel/PDF/LOGGER` を走査し、ファイル名 `型式_SN番号_日付_6500` からS/Nを拾って完了記録と突き合わせます。記録に無いS/Nのデータがあれば件数とS/Nをポップアップとログで知らせます（このアプリの形式でないファイルは件数だけ）。保存は止めません。正式保存のときに同じ S/N の保存ファイル（Excel／PDF／LOGGER）がすでにあれば（同じアプリの起動中に自分で保存したもの・保護解除済みの S/N は除く）、ファイル名を示して「今回の測定で上書きしますか？」と聞きます。「はい」で上書き、「いいえ」なら保存せず、「次にすること」に取れる手（上書きする／「設定変更（測定を破棄）」→ 測定開始S/N を次の番号にして測り直す／前のデータを合格扱いにするなら「完了番号の保護と解除」で保護）を赤字で出します（2026-11-06）。
- 「完了番号の保護と解除」は完了記録にあるS/N（保護中はチェックあり、解除済みはチェックなし）と、記録に無いデータのS/N（「記録なし」、チェックなし）を番号順に一覧にします。チェックを外して「確定」を押すと RELEASE を追記して保護を解除し（既存ファイルは残します）、チェックを付けて「確定」を押すと COMPLETE を追記して保護します（解除済みは型式「-」・ファイル名「再保護」、記録なしはファイル名から読んだ型式とファイル名）。キャンセルなら何もしません。保存先が未選択なら「保存先を選択してください」、記録もデータも無ければその旨をポップアップで出します。
- 同梱の `雛形.xlsx` の判定式はAS110-035用です。他型式は判定規格を確認し、必要に応じて型式名のExcel雛形を用意してください。
- LOGGER雛形はモードと検出した本体で次の固定ファイル名から選択します。データ調整とデータ取りは共用です。ビルド先・発行先にもコピーします。用途が異なるモードへ切り替えたら校正値を入れ直してください（自動で校正画面が開きます）。

| 用途 | NR-500 | NR-X（WAVE LOGGER X） |
| --- | --- | --- |
| 動き検査 | `AS110動き検査用.xdt` | `AS110動き検査_NRX100.ydt` |
| データ調整・データ取り | `AS110データ調整・データ取り用.xdt` | `AS110データ調整・データ取り_NRX100.ydt` |

共用XDTは指定ファイル `AS110-035_SN74233-20261016-2mS-ave0.xdt` のコピーです。XDTからYDTへの変換機能はありません。共用YDTは指定ファイル `AS110dデータ取り_NRX100.ydt` を元に、リピート・マニュアルトリガをOFFにした2ms・6500点の単発収集設定です。データ調整・データ取りでは毎回Startし、完全停止後に一時保存します。NR-X100形式とCH1のmAスケーリングへの適合を確認済みです。未配置時は別用途の雛形を流用せず、必要なファイル名を表示します。LOGGERでの実測は別途確認してください。

転送設定の保存先は元のver3と同じ `%LOCALAPPDATA%/AS110BoltCheck/ver3.json` です（移植後も変えていません。現場PCの保存先設定をそのまま引き継ぐため）。

YDT一時保存失敗の推定原因、当面の単発収集への変更と検証範囲は [調査記録](docs/data-acquisition-save-issue.md) を参照してください。マニュアルトリガ運用の長期方針は検討中です。

## M5Stack

書込元は `firmware/AS110M5Stack/AS110M5Stack.ino`。Arduino IDEではボードM5Core（esp32 3.x。古い版ではM5Stack-Core-ESP32）とM5Unifiedを使用します。準備から書き込みまでの手順は [Arduino IDE の準備](docs/m5stack-arduino-ide-setup.md) を参照してください。PlatformIOでは次を実行します（リポジトリ直下から）。

```powershell
pio run -d Apps/AS110AllCheck/firmware
```

M5Stackの通常文字は日本語16pxフォントを1.125倍した18px相当です。ボタン名が枠幅を超える場合だけ縮小します。データ調整ではS/N欄を表示しません。文字サイズの変更には本体への再書き込みが必要です。生成物の `.ino.cpp` は入れず、`.ino` を正本とします。

## 動き検査と開発者モード

- 動き検査には保存・Excel転送・S/N設定はありません。M5StackはA＝「測定開始」、B/C＝「-」（未使用）です。ここで不要なのは製品のシリアル番号で、M5StackとのUSBシリアル通信は引き続き必要です。
- PC画面の「開発者モード（接点模擬）」を選ぶと、ボタンで模擬接点を通電／非通電に切り替えられます。動き検査だけに適用し、初期値は非通電です。測定中は切り替えられません。
- 模擬するのは開始条件の接点状態です。実際の電源や接点は操作しません。M5Stackの「外部接点」欄は実入力、状態欄の「開発者・模擬接点」はPCでの判定状態を示します。通信確認は開発者モードでも必要です。
- 開発者モードを解除するか他モードへ移ると実接点に戻り、模擬状態は保存しません。
- M5Stackの外部接点欄は動き検査だけに表示します。データ調整・データ取りでは表示しません。
- 参照波形を読み込んだあとの LOGGER の横軸拡大は行いません（スタンドごとの 2 段拡大は 2026-11-06 に廃止。必要なら人が LOGGER の画面で拡大します）。

## 検証

コマンドはリポジトリ直下から実行します。`<dir>` は `Apps/AS110AllCheck` の絶対パスです。

```powershell
# 実機・Excel なし
dotnet run --project Apps/AS110AllCheck/Tests/Verification/AS110AllCheck.Verification.csproj -c Release
# 検証用 XDT と Excel 雛形の照合(XDT はデータ調整・データ取り用。動き検査用は点数が違うので使わない)
dotnet run --project Apps/AS110AllCheck/Tests/Verification/AS110AllCheck.Verification.csproj -c Release -- "<dir>/雛形/AS110データ調整・データ取り用.xdt" "<dir>/雛形/雛形.xlsx"
# 終了処理(機器なし)
dotnet run --project Apps/AS110AllCheck/Tests/AppShutdown/AppShutdownTests.csproj -c Release
# 実際の MainForm と Excel(画面を出す。実行中はマウス・キーボードに触らない)
dotnet run --project Apps/AS110AllCheck/Tests/SaveAvailability/SaveAvailability.csproj -c Release -- --no-dialogs --with-excel "<dir>/雛形/雛形.xlsx" "<dir>/雛形/AS110データ調整・データ取り用.xdt"
```

Excel の作業ブック（アプリの DLL を読み込んで実行。モードなしは図形の S/N 書き換えと判定シートの前面表示）:

```powershell
dotnet build Apps/AS110AllCheck/Tests/ExcelHeader/ExcelHeaderTests.csproj -c Release
$e = "Apps/AS110AllCheck/Tests/ExcelHeader/bin/Release/net9.0-windows/ExcelHeaderTests.dll"
$d = "<dir>/bin/Release/net9.0-windows/AS110AllCheck.dll"
dotnet $e $d "<dir>/雛形/雛形.xlsx"
dotnet $e $d "<dir>/雛形/雛形.xlsx" --optimization
# --session / --transfer / --reuse / --prepare は最後に "<dir>/雛形/AS110データ調整・データ取り用.xdt" を付ける
```

`Tests/ExcelHeader` と `Tests/SaveAvailability` は、Excel が応答しない（別の Excel が残っている、画面を触った）と `0x80010001`（呼び出し拒否）や前面表示の失敗で止まることがあります。Excel を閉じ、画面に触らずに再実行してください。終わったあと Excel が裏に残ることがあります（移植前から同じ）。

移植前の確認結果は [整理・検証記録](docs/reorganization.md)、元の2026-10-19時点の検証記録は [ver3_検証結果.txt](docs/legacy/ver3_検証結果.txt) です。

## 確認（2026-11-05、移植後）

- この PC（SDK 10.0.401、NI-VISA・Excel あり、機器なし）で: `AS110AllCheck.slnf` の Release ビルド（エラー 0。警告は `Wrappers/Instruments` の VISA 参照の MSB3270 だけで、移植前から同じ）、Verification（照合を含む）、AppShutdown 11 件、SaveAvailability（`--no-dialogs --with-excel`）21 件、ExcelHeader（ヘッダ 7・`--optimization` 54・`--session` 9・`--transfer` 6・`--reuse` 16・`--prepare` 7）。件数は移植前と同じ（`--session` は切断とみなすエラーを 1 つ足した分だけ多い）
- 未確認: **実機での動作すべて**（M5Stack、NR-500 / NR-X の USB・Wi-Fi、WF1973 / SDG1032X、LOGGER の測定・保存、漏れチェック、正式保存）、SaveAvailability のダイアログありの実行、発行した exe を別の PC で動かすこと
- 2026-11-05 LOGGER の事前起動（起動を画面準備と重ねる）: この PC に M5Stack・WF1973・NR-500（旧 WAVE LOGGER、USB）をつないだ状態で Debug ビルドの exe を起動し、接続確認が正常終了（本体=NR-500）して、閉じると LOGGER も終了することを確認。接続確認のログで「COMオブジェクト生成（事前起動を使用）」と「LOGGER事前起動の完了待ち = 0.000 秒」が出て、プロセス起動から接続確認終了までが 3.0 秒（変更前の同日のログでは 11.2〜13.4 秒。ただしこの回は LOGGER 本体の起動自体が 0.6 秒と速く、7〜8 秒かかる回での短縮幅は未確認）。未確認: NR-X・WAVE LOGGER X・Wi-Fi での事前起動、両方の LOGGER が入った PC で種類を切り替えたときの破棄
- 2026-11-06、別の PC（SDK 9.0.318、NI-VISA 25.3・Excel 16・WAVE LOGGER X 1.11.06 あり、旧 WAVE LOGGER と機器はなし）で機器なしの検査をやり直した: `AS110AllCheck.slnf` の Release ビルド（エラー 0・警告 0）、Verification（照合を含む）、AppShutdown、SaveAvailability（`--no-dialogs --with-excel`）、ExcelHeader の 6 モード、`Wrappers/Tests/WaveLogger.Automation.Tests`（事前起動を含む）がすべて成功。検査のあと Excel が 3 つ裏に残った（既知）。この PC では NR-500 経路（旧 WAVE LOGGER）の実機確認はできない
- M5Stack スケッチ: 2026-11-05 に人が Arduino IDE（esp32 3.3.12、ボード M5Core、M5Unified 0.2.29）で M5Stack Basic V2.7 へ書き込めた（手順は [Arduino IDE の準備](docs/m5stack-arduino-ide-setup.md)）。書き込み後の表示・ボタン・G26 接点・PC アプリとの通信は未確認。スケッチは変えていない
