# wrapper の機能一覧(できること・制約)

> **2026-11-04 更新**。WAVE LOGGER・ロガーの出力ファイル・共通(NR-X 接続補助を含む)・Excel(xlsx 直接書き込みと、Excel を起動する操作)・Arduino・VISA 機器(発振器・DMM)は `Wrappers/` へ移植済みで、この一覧は実装に合わせてある。各項目の「元」は旧リポジトリの場所。出どころと移植時の変更は `docs/移行記録.md`。
>
> ここに書いた動作のうち、機器を使うものの実機確認は 2026-11-04 に移植後のコードで行った範囲だけ: AS110NoiseTest の通しで使った旧 WAVE LOGGER + NR-500(USB)・7351E・WF1973(DC)・Excel への書き込み、`Wrappers/Tests/Instruments.HardwareCheck` で WF1973 のバースト・連続、AS100201SwitchController の通しで使った Arduino(UNO R4 Minima、接続・受信・切断検知・再接続)・WF1973(連続、形の指定つき)・旧 WAVE LOGGER + NR-500(`ReferenceOnly`、Start / Stop)。**それ以外(WAVE LOGGER X、NR-X、Wi-Fi 切替、SDG1032X など)は実機で確認していない**(ビルドと、模擬クラスを使うテストだけ)。各項目の「制約」に確認した範囲を書く。Excel を起動する操作は、Excel のある PC で `Wrappers/Tests/Excel.Tests` が実際に Excel を動かして確かめている(アプリからは AS110NoiseTest の結果ブックへの書き込みを実機確認で通した。XdtConversion の `ExcelViewer` は未確認)。

新しい依頼を受けたら、まずここを読んで「今の wrapper で何ができるか」を把握し、できる範囲から選択肢を出す。ここに無い機器・操作は、wrapper の新規作成(全アプリに影響)として人に確認する。

## 機器と wrapper の対応

| 機器 | 役割 | PC との接続 | wrapper(置き場) | 元 |
|---|---|---|---|---|
| KEYENCE NR-500 / NR-X100(WAVE LOGGER / WAVE LOGGER X) | データロガー。波形を xdt / ydt で記録 | COM オートメーション(WAVE LOGGER 本体ソフト経由)。本体は USB / LAN / Wi-Fi | `Wrappers/WaveLogger.Automation`(移植済み) | AS110AllCheck 他 3 つ(ほぼ同一) |
| NF WF1973 | ファンクションジェネレータ。三角波バースト / 連続波 / DC オフセット | USB(VISA、NI-VISA 必須) | `Wrappers/Instruments`(移植済み) | AllCheck(バースト)、SwitchController(連続)、NoiseTest(DC) |
| SIGLENT SDG1032X | ファンクションジェネレータ(WF1973 の代替) | USB(VISA) | `Wrappers/Instruments`(移植済み) | AllCheck、BoltCheck |
| ADCMT 7351E | DMM。直流電流の実測 | **USB のみ**(USBTMC、VISA)。GPIB / RS-232C は不可 | `Wrappers/Instruments`(移植済み) | NoiseTest |
| Arduino(UNO R4 Minima、M5Stack 等) | 試験治具のマイコン。スイッチ・LED・NF 弁のインターロック | USB シリアル(COM ポート、行単位テキスト) | `Wrappers/Arduino`(移植済み) | AllCheck、BoltCheck、SwitchController |
| Windows の Wi-Fi | NR-X を Wi-Fi で使うときの接続先切替 | `netsh` | `Wrappers/Common`(移植済み) | AllCheck 他 |
| ロガーの出力ファイル(xdt / ydt / CSV) | WAVE LOGGER の波形ファイルと CSV | ファイル | `Wrappers/LoggerFiles`(移植済み) | XdtConversion.Core 他 |
| Excel ブック(xlsx) | 測定値の転記・判定・PDF 保存 | ファイル直接 / Excel COM(Excel 本体ソフト経由) | `Wrappers/Excel`(移植済み) | AllCheck、XdtConversion、NoiseTest |

## できること

### WAVE LOGGER(`WaveLoggerSession`、`PSCAutomation.Wrappers.WaveLogger.Automation`)

- 旧 WAVE LOGGER と WAVE LOGGER X の両方を同じ API で扱う(`WaveLoggerEdition` で選ぶ)
- 本体の検出: NR-500 を USB で、NR-X を USB / LAN / Wi-Fi で確認する
- 雛形ファイル(xdt / ydt)を開く・閉じる、記録開始(`StartAsync` トリガ付き / `StartSingleAcquisition` 1 回収集 / `Start` 素の Start。違いは `Wrappers/WaveLogger.Automation/README.md`)、停止、状態取得(`GetState`、状態番号の意味は `LoggerMeasurementState`)
- 保存: xdt、CSV
- 参照波形の読み込み、横軸拡大、LOGGER 画面の復元・前面表示(`BringToFront`)。`BringToFront` は Windows に 1 回頼むだけで、M5Stack など外部入力から呼んだときやほかのアプリが前面のときは拒否されやすい。確実に前に出すときは `GetMainWindowHandle`(画面のハンドルを返すだけ、2026-11-10 追加)を `WindowActivation.TryActivate` に渡す(AS110AllCheck が使う)
- 終了処理: × 検知、停止・Quit・COM 解放、対象プロセスだけを終了(別プロセスは守る)
- 終了の扱いを選べる(`Lifecycle`): 既定は上の終了処理(`ManagedShutdown`)。`ReferenceOnly` は LOGGER を止めず COM 参照だけ解放する(AS100201SwitchController の使い方。移植後に NR-500・旧 WAVE LOGGER で実機確認)
- LOGGER 本体ソフトの起動だけを先に始めておける(`BeginServerStartup` → `WaitForServerStartupAsync` → `Initialize`)。旧 WAVE LOGGER の起動は 7〜8 秒かかるので、画面を作る前に始めて画面準備・他の機器の確認と重ねる。`Initialize` は起動済みの参照を受け取るだけになる。使わないアプリは何も変わらない(AS110AllCheck で NR-500・旧 WAVE LOGGER を実機確認、2026-11-05)
- 機器なしの模擬に差し替えるための薄い interface の例: AS110NoiseTest `Measurement/ILoggerDevice.cs`
- **部品には無いがメーカー API にあるもの**: 保存せずに値を取り出す `GetArrayData` / `GetData` / `GetCurrentData`(測定中も使える、と説明書にある。換算前の値)。測定中に取り出せるか・速さ・保存した xdt と一致するかを確かめる確認ツール `Wrappers/Tests/WaveLogger.LiveReadCheck` を 2026-11-10 に作った(実機は未確認)。使うときは部品に関数を足す(全アプリに影響するので人に確認)

**制約**
- WinForms の UI スレッド(STA)から使う。`Task.Run` や並列呼び出しは不可。非同期操作中はアプリ側で操作を排他する(事前起動だけは内部で別スレッドを使うが、受け取った後の操作は従来どおり UI スレッド)
- 事前起動を使うアプリは、`Initialize` せずに終わる経路(接続確認前に閉じる、LOGGER の種類を切り替える)で `CancelServerStartup()` を呼ぶ(`Release()` / `ShutdownAsync` からも破棄される)
- 終了・再接続の前に必ず `await ShutdownAsync(log)`。失敗したら新しいセッションに置き換えず、同じものを保持して再試行
- LOGGER を人が手で操作すると状態が変わり COM 例外になる。成功扱いせず画面に出す
- 実行 PC に WAVE LOGGER 本体ソフトとドライバが必要。NR-500 は旧 WAVE LOGGER(4.03.00)で測る(X 2.00.10 が入っていても)

### VISA の通信(`Wrappers/Instruments`、`PSCAutomation.Wrappers.Instruments`)

発振器・DMM のクラスは、通信部分 `VisaInstrumentSession`(元 SwitchController。NoiseTest の探し方を足した)を通して機器とやり取りする。機器クラスは `IInstrumentSession` を受け取るので、模擬に差し替えて機器なしで確かめられる。

- 機器の探索(`VisaInstrumentSession.Connect`): VISA リソースを探して `*IDN?` を送り、渡した判定に合った最初の機器に、渡した初期化コマンドを送って返す。探し方は `VisaSearchOptions` で選ぶ
  - `?*INSTR` の総当たり(既定)/ USB の VID/PID などのパターン指定 / リソース名の直接指定
  - USB だけに絞る、失敗時のメッセージに「通信できなかったリソースと理由」「識別が合わなかったリソース」を付ける
  - 該当なしのときは VISA の例外ではなく、渡したメッセージの `InvalidOperationException`
- 送受信(`Write` / `Read` / `Query`)、タイムアウト変更、切断(`Dispose`。COM オブジェクトを解放する)

### WF1973 / SDG1032X(発振器)(`Wrappers/Instruments`)

使い方ごとにクラスが分かれている(判断 4、2026-11-04)。どれを使うかは `Wrappers/Instruments/README.md`。

- バースト(`FunctionGeneratorConnection.Connect` → `Wf1973BurstSession` / `Sdg1032XSession`、元 AllCheck): USB の WF1973 か SDG1032X を自動で見分ける。三角波(対称 50%)、周波数・オフセット・振幅・周期数・開始位相を設定し、外部トリガで指定周期数だけ出す(WF1973 は `*TRG`、SDG1032X は `C1:BTWV MTRIG`)。設定完了を `*OPC?` で確認(SDG1032X は設定値を読み戻して照合)。同じ設定なら再送しない。DC 出力(`SetDcOutput`、±5V まで)、出力停止(`StopOutput`)
- 連続(`Wf1973ContinuousSession`、元 SwitchController): 振幅・位相・周波数を設定して位相同期で再開始。出力中は OFF にしない。押下連打の途中で中断できる
  - 波形の形を指定して接続(`Connect(GeneratorWaveform)`、2026-11-04 追加): 接続時に出力 OFF のまま形(今は三角波・対称 50% だけ)を送り、`*OPC?` で確かめる。別のアプリが DC などにした WF1973 でも正しく出る。`Connect()` は元のまま形を送らない
  - 接続したまま形だけ送り直す(`SetWaveform(GeneratorWaveform)`、2026-11-04 追加): 出力・振幅・オフセット・位相は変えない。出力中に呼んだときの出力の乱れは確かめていない
- DC(`Wf1973DcSource`、元 NoiseTest): 接続時に DC 波形・0V にして確認。出力を切らずにオフセット電圧だけ変える(`SetOffsetVolts`、±10V、0.1mV 単位、本体エラーを確認)。出力 ON(`SetOutput(true)`)の後 50ms 待つ。AS110 では **1V = 6mA** の電流指令に使う
- どれも `Dispose` 時に出力 OFF を試みる(タイムアウト 500ms、失敗しても解放は続ける)。連続(`Wf1973ContinuousSession`)だけは元のとおり出力 OFF を送らない

**制約**
- NI-VISA(`Ivi.Visa.Interop` COM)が実行 PC とビルド PC に要る(Instruments を参照しないアプリには不要)
- 負荷設定は本体の設定を使う
- SDG1032X は AS110 のバースト用途だけ確認済み
- 同じ WF1973 を 2 つのクラスで同時に開かない(状態の管理がクラスごと)
- 連続(`Wf1973ContinuousSession`)を `Connect()` でつなぐと波形の形を送らない(本体に残っている形のまま出る。DC の後に使うと DC のまま。AS100201SwitchController の移植時に実機で起きた)。形を決めたいときは `Connect(GeneratorWaveform)` を使う。位相を変えて出し直すと、まず設定の差だけずれ、約 80ms 後に設定の位相からやり直す(段差が 2 回)
- `SetDcOutput` は呼んでから約 0.16 秒後に出る(バーストの `*TRG` はすぐ出る)
- 移植後の実機確認(2026-11-04、WF1973 の 3 クラスとも): `Wrappers/Instruments/docs/WF1973実機確認.md`。連続は AS100201SwitchController の画面・スイッチからも確認(10.2Vpp の三角波で位相が切り替わることを目視。値の記録・照合はしていない)。SDG1032X、AllCheck・BoltCheck の画面からのバーストは未確認

### ADCMT 7351E(DMM)(`Adcmt7351ESession`、`Wrappers/Instruments`)

- 直流電流を読む(`ReadMilliamps`)。応答は `DCI_ +006.027E-03` 形式、約 400ms/回(SLOW2)。DCI 以外・レンジオーバー・演算有効の値は例外
- 設定(`ApplySettings`: レンジ 200mA / 2000mA / 10A / Auto、サンプリング速度、オートゼロ、スムージング、商用周波数)を 1 つずつ送って `ERR?` で確かめ、問い合わせて照合
- トリガ: Immediate / **Bus**(読むたびに `*TRG`。古い値を掴まない)。Manual・External は PC から測定を始められないので受け付けない
- エラーの読み出し(`DrainErrors`)、現在の設定の一覧(`ReadConfiguration`)
- `Dispose` 時にトリガを Immediate に戻す(前面パネルの表示が止まったままにならない)

**制約**
- 通信は USB のみ。コマンドは ADC 独自(SCPI 不可)。本体の LANG を ADC にしておく
- 設定変更直後は古い読み値が残るので捨てる(`DiscardStaleReadings`。Bus トリガなら不要)
- 実機での確認内容は `Wrappers/Instruments/docs/7351E実機確認.md`(移植前の AS110NoiseTest での確認)。移植後は 2026-11-04 に AS110NoiseTest の範囲(DCI・200mA・BUS トリガでの読み取り)を実機で確認(`Apps/AS110NoiseTest/README.md`)。200mA 以外のレンジ・Immediate トリガは移植後未確認

### Arduino(`Wrappers/Arduino`、`PSCAutomation.Wrappers.Arduino`)

`ArduinoConnection`(元 AllCheck)と `ArduinoDiagnosticLog`(元 SwitchController の `Configure` 方式 + AllCheck の `SHUTDOWN_ERROR`)。

- COM ポートを開く・閉じる・再接続、行単位の送受信、受信スレッド、切断検知(ポート消失 / クローズ / 書込失敗)(`ArduinoConnection`)
  - 接続(`ConnectAsync(request, isReply)`): COM ポートを総当たりで開き、1.7 秒待ってから `request` を送り、`isReply` が真を返す行が 2 秒以内に来たポートを使う。送る文字列と応答の判定はアプリが渡す
  - 確認(`VerifyAsync`): 接続済みなら同じやり取りで生存確認、未接続なら接続する。失敗したらポートを閉じる
  - 受信は `LineReceived` イベント(専用スレッドから呼ばれる)、切断は `Disconnected` イベント。再接続の方針はアプリ側
- 異常ログ(`ArduinoDiagnosticLog`): 正常時は直近 1 分をメモリに持つだけ。異常種別(`COM_DISAPPEARED` `PORT_CLOSED` `RX_STALE` `WRITE_ERROR` `READER_ERROR` `SHUTDOWN_ERROR` 等)を書くと、直近 1 分の記録ごとファイルに保存し始める。復旧(`CompleteIfActive`)後 30 秒は同じファイルにまとめる
  - アプリ名と固有の異常種別は起動時に `ArduinoDiagnosticLog.Configure("アプリ名", "NF_STALE", ...)` で注入する(通信を始める前に 1 回)
  - 保存先は実行ファイルの隣の `Logs\ArduinoIncident_*.log`。書けないときは `%LOCALAPPDATA%\<アプリ名>\Logs`(`PrimaryDirectory` / `FallbackDirectory`)
  - 共通の異常種別か判定する `IsIncident(kind)`
- PC 遅延観測(`Wrappers/Common` の `PcDelayObservation`)と組み合わせるときは `PcDelayObservation.Start(ArduinoDiagnosticLog.Write)`
- 受信行の意味(`NF:0` / `NF:1`、READY、スイッチ状態)と受信頻度の判定は**アプリ側のプロトコルクラス**が持つ(AllCheck `As110ArduinoProtocol`、SwitchController `As100201ArduinoProtocol`)

**制約**
- 115200bps、改行 `\n`(LF)、送信 UTF-8(BOM なし)、受信は ASCII として解釈、DTR 有効(元のまま固定)
- `ConnectAsync` は応答が得られるまで PC の COM ポートを順に開いて確認文字列を送る。Arduino 以外の機器がつながった COM ポートにも送る点に注意(元のまま)
- 異常ログは静的クラスでプロセスに 1 つ。`Configure` は起動時に 1 回呼ぶ
- 通信断だけでは発振器や LOGGER を止めない(方針はアプリ側)
- 実機(Arduino UNO R4 Minima、COM4)では 2026-11-04 に AS100201SwitchController の範囲を確認: 総当たりの接続(1.7 秒の起動待ち込み)、行の受信、USB を抜いたときの切断検知(`PORT_CLOSED`)と異常ログの保存、挿し直した後の再接続(`Apps/AS100201SwitchController/README.md`)。M5Stack・AS110 の NF 監視、1024 文字超の破棄、`LocalApplicationData` への切り替えは未確認。テスト(`Arduino.Tests`)はポートを開かない範囲だけ

### ロガーの出力ファイル(`Wrappers/LoggerFiles`、`PSCAutomation.Wrappers.LoggerFiles`)

WAVE LOGGER を起動せずに、出力ファイルを直接読み書きする。Windows 非依存(`net9.0`)。使い方は `Wrappers/LoggerFiles/docs/XDT-WRAPPER.md`。

- 旧 LOGGER / LOGGER X の判別、サンプル数、CH 一覧(名前・単位・スケーリング 2 点・移動平均)(`XdtFile`)
- 値の読み出し: 生値 / スケーリング後 / 移動平均後(`XdtFile`。旧アプリと同じ入口 `XdtReader.Inspect` / `Read` もある)
- サンプリング周期、収集開始日時、先頭・最終データ日時(メーカー CSV と照合済み)(`XdtFile.Info.Timing`)
- 表示レンジの読み書き(雛形の表示レンジを平均値中心に直す)(`XdtDisplayRange`、旧 LOGGER 形式のみ)
- 測定日時だけを速く読む(`XdtTimestamp`、旧 LOGGER 形式のみ。LOGGER X は `XdtFile` を使う)
- 雛形の 2 点スケーリング(校正値)の読み書き(`XdtScalingEditor`、元は WaveLogger.Automation 内): xdt と NR-X100 の ydt(CH1 のみ)
- ロガーの CSV の読み出し(`LoggerCsvReader`、元 AllCheck): CH 番号・見出しの語・列番号(0 は自動検出)と点数を渡すと、指定した列の値を返す。点数が一致する区間がちょうど 1 つでないと例外

**制約**
- ydt(NR-X)は今のところ CH1 のスケーリング読み書きのみ(`XdtFile` は ydt を読めず例外)。解析はまだだが、**いずれ xdt と同じこと(値・周期・日時・表示レンジ)を ydt でもできるようにする方針**。API は xdt 専用の名前や前提にせず、ydt を後から足せる形にする
- 日時のタイムゾーンと一部の周期単位は未確認。推測しない

### Excel(`Wrappers/Excel`、`PSCAutomation.Wrappers.Excel`)

大枠は「ロガーの値を Excel 雛形へ転記する」。アプリごとの違い(どの CH をどのシート・セルに書くか、間引き数、判定セルの読み方)はアプリ側の設定で渡す。

- xlsx への直接書き込み(`TemplateWriter`、元 AllCheck / XdtConversion、移植済み): 値の列を指定シート・セルから書く。開始セル以降の旧値は消す。数式のある列・同名の保存先は止める。開いたときに再計算する設定にする。**Excel のインストール不要**
- Excel を起動しての操作(元 AllCheck `ExcelWorkSession`、NoiseTest `NoiseResultWorkbook`、XdtConversion `ExcelPresenter` の共通部分、移植済み)。判定・セル位置・置き換える文字のパターンはアプリ側で渡す
  - 起動・終了(`ExcelApplication`): このアプリ専用の Excel を起動する(人が開いている Excel にはつながない)。画面に出すか・確認ダイアログを出すかを選べる。後始末(Dispose)で、開いたブックを保存せずに閉じ、Excel を終了する(ほかにブックが開いていれば残す/必ず終了する、を選べる)。Excel が落ちた・人が閉じたかを確かめられる(`IsConnected`)
  - ブックを開く(`ExcelApplication.Open`): 読み取り専用で開かれた(ほかで開いている、ファイルが読み取り専用)ら閉じて止める。読み取り専用を指定して開くこともできる
  - 保存(`ExcelWorkbook`): 上書き保存、開いたまま別名で保存(`SaveCopyAs`)
  - PDF(`ExcelSheet.ExportAsPdf`): シートを PDF に保存
  - セルの読み書き(`ExcelSheet`): 1 セル・範囲の読み書き、値の消去(書式は残す)、数式の有無、列の最後の行、行のコピー(数式・罫線ごと)。名前定義の範囲を読む(`ExcelWorkbook.ReadNamedRange`)
  - 開いているブックの列に測定値を書く(`ExcelSheet.WriteColumns`): 書き込み先に数式があれば何も書かずに止める。隣の列はまとめて書く。開始セルより下の前回の値は消さない(消すのは `TemplateWriter`)
  - シリアル番号などの書き換え: セルなら `SetValue`。図形(テキストボックス・オートシェイプ)の中の文字は `ExcelSheet.ReplaceShapeText` で、文字ごとの書式(フォント・大きさ・太字・下線)を保ったまま値の部分だけを置き換える。置き換える場所は正規表現の `(?<value>…)` で指定する。図形の文字の見本は `GetShapeTexts` で読める
  - 計算結果を読む: ブック全体・シートごとの再計算(測定値のシートを先に計算できる)、計算が終わるまで待つ(打ち切り時間つき)、判定セルを文字で読む(`GetText`)
  - 終わったら人に渡す: `ExcelApplication.HandOverToUser`(以後 Dispose しても閉じない)。ブックとシートを画面の前に出す(`ExcelWorkbook.ShowWindow`。Windows の前面に出すには返った窓のハンドルを `WindowActivation.TryActivate` に渡す)
  - 保存済みのブックを開いて渡すだけなら `ExcelViewer.OpenForUserAsync`(元 XdtConversion `ExcelPresenter`。画面のスレッドを止めない)

**制約**
- Excel を起動する操作は実行 PC に Excel が必要(実行時に探す方式なので、ビルドには不要)
- Excel を起動する操作は、同じスレッド(ふつうは画面のスレッド)から呼ぶ
- 測定値の書き込みは、ロガーの xdt / ydt を読んだ値の配列を渡す形。xdt / ydt の違いは LoggerFiles 側で吸収する
- XdtConversion は `ExcelViewer` に乗り換え済み(2026-11-04)。AllCheck・NoiseTest は移植時にこの部品を使う
- AS110AllCheck は 2026-11-05 の移植で `ExcelApplication`・`ExcelWorkbook`・`ExcelSheet` に乗り換えた(作業ブックの再利用・図形の S/N 書き換え・判定・別名保存・PDF・前面表示)。実際の雛形(圧縮5 の図形、sheet1 の E21・L21)で、Excel のある PC の検査(`Apps/AS110AllCheck/Tests/ExcelHeader`・`SaveAvailability`)は通した。機器をつないだ通しは未確認

### 共通(`Wrappers/Common`、`PSCAutomation.Wrappers.Common`)

- `ErrorNotification`: 警告・エラーのメッセージボックスを最前面に表示(LOGGER の後ろに隠れない)
- `PcDelayObservation`: PC の処理遅延(タイマーの遅れ)を監視して記録。書き込み先は `Start(write)` で渡す(例: Arduino の異常ログ)
- `WindowActivation`: 他プロセスの窓を前面にする
- `NrDeviceConnections` / `SingleAdapterWifiManager`: NR-X の接続先(USB / LAN / Wi-Fi)の整理と、Wi-Fi プロファイル切替。SSID 一覧は wrapper に持たず、アプリ側に置く(置き場はアプリを作るときに AI が聞く。`AGENTS.md`「仕様決め」)

**NR-X を Wi-Fi で使うとき**(USB なら以下は要らない)
- 事前準備: 最初の 1 回だけ、使う PC の Windows の Wi-Fi 画面から NR-X に接続し、プロファイルを保存しておく。wrapper はパスワードを扱わず、Windows に保存されたプロファイルだけを使う。未登録なら例外で止まる
- アプリに要る値: Wi-Fi プロファイル名(通常は SSID と同じ)、NR-X の IP アドレスとポート番号。社内の実機の値は旧 AllCheck / BoltCheck の `Logger/NrDeviceConnections.cs`、SwitchController の `As100201/As100201ConnectionSettings.cs`(いずれも `NrWifiProfiles`)に書かれている
- PC の Wi-Fi アダプター 1 台で接続先を切り替えるので、NR-X につないでいる間は PC がふだんの Wi-Fi(社内 LAN・インターネット)から外れる。終わったら元の接続先に戻す(`RestoreAsync`。切り替え前が未接続なら未接続に戻す)
- 接続の完了はタイムアウト付きで確かめる。時間内につながらなければ例外

## アプリの一覧

| アプリ | 内容 | 使う機器 |
|---|---|---|
| AS110AllCheck | AS110 の全数検査(動き検査・校正・リーク・Excel 転記) | NR-X / NR-500、WF1973 or SDG1032X、Arduino(M5Stack)、Excel |
| AS110BoltCheck | AS110 の動き検査 | NR-X、WF1973 or SDG1032X、Arduino(NF インターロック) |
| AS100201SwitchController | AS100-201 のスイッチ制御。**移植済み**(`Apps/AS100201SwitchController`、`AS100201SwitchController.slnf`) | NR-500 / NR-X(USB だけ)、WF1973(連続・三角波)、Arduino(スイッチ・READY LED) |
| AS110NoiseTest | AS110 のノイズ測定(電流を変えながら制御圧力の p-p を判定) | NR-500、WF1973(DC)、ADCMT 7351E、Excel |
| XdtConversion | xdt を Excel 雛形へ転記する単機能アプリ。**移植済み**(`Apps/XdtConversion`、`XdtConversion.slnf`) | ファイルのみ、Excel |
