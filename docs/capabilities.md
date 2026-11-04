# wrapper の機能一覧(できること・制約)

> **2026-11-04 更新**。WAVE LOGGER・ロガーの出力ファイル・共通(NR-X 接続補助を含む)・Excel の xlsx 直接書き込み・Arduino は `Wrappers/` へ移植済みで、この一覧は実装に合わせてある。VISA 機器・Excel を起動する操作は**まだ移していない**(旧リポジトリを調べた時点の内容)。各項目の「元」は旧リポジトリの場所。出どころと移植時の変更は `docs/移行記録.md`。
>
> ここに書いた動作のうち、機器や Excel を使うものは**実機で確認していない**(ビルドと、模擬クラスを使うテストだけ)。

新しい依頼を受けたら、まずここを読んで「今の wrapper で何ができるか」を把握し、できる範囲から選択肢を出す。ここに無い機器・操作は、wrapper の新規作成(全アプリに影響)として人に確認する。

## 機器と wrapper の対応

| 機器 | 役割 | PC との接続 | wrapper(置き場) | 元 |
|---|---|---|---|---|
| KEYENCE NR-500 / NR-X100(WAVE LOGGER / WAVE LOGGER X) | データロガー。波形を xdt / ydt で記録 | COM オートメーション(WAVE LOGGER 本体ソフト経由)。本体は USB / LAN / Wi-Fi | `Wrappers/WaveLogger.Automation`(移植済み) | AS110AllCheck 他 3 つ(ほぼ同一) |
| NF WF1973 | ファンクションジェネレータ。三角波バースト / 連続波 / DC オフセット | USB(VISA、NI-VISA 必須) | `Wrappers/Instruments`(未移植) | AllCheck(バースト)、SwitchController(連続)、NoiseTest(DC) |
| SIGLENT SDG1032X | ファンクションジェネレータ(WF1973 の代替) | USB(VISA) | `Wrappers/Instruments`(未移植) | AllCheck、BoltCheck |
| ADCMT 7351E | DMM。直流電流の実測 | **USB のみ**(USBTMC、VISA)。GPIB / RS-232C は不可 | `Wrappers/Instruments`(未移植) | NoiseTest |
| Arduino(UNO R4 Minima、M5Stack 等) | 試験治具のマイコン。スイッチ・LED・NF 弁のインターロック | USB シリアル(COM ポート、行単位テキスト) | `Wrappers/Arduino`(移植済み) | AllCheck、BoltCheck、SwitchController |
| Windows の Wi-Fi | NR-X を Wi-Fi で使うときの接続先切替 | `netsh` | `Wrappers/Common`(移植済み) | AllCheck 他 |
| ロガーの出力ファイル(xdt / ydt / CSV) | WAVE LOGGER の波形ファイルと CSV | ファイル | `Wrappers/LoggerFiles`(移植済み) | XdtConversion.Core 他 |
| Excel ブック(xlsx) | 測定値の転記・判定・PDF 保存 | ファイル直接 / Excel COM(Excel 本体ソフト経由) | `Wrappers/Excel`(直接書き込みのみ移植済み) | AllCheck、XdtConversion、NoiseTest |

## できること

### WAVE LOGGER(`WaveLoggerSession`、`PSCAutomation.Wrappers.WaveLogger.Automation`)

- 旧 WAVE LOGGER と WAVE LOGGER X の両方を同じ API で扱う(`WaveLoggerEdition` で選ぶ)
- 本体の検出: NR-500 を USB で、NR-X を USB / LAN / Wi-Fi で確認する
- 雛形ファイル(xdt / ydt)を開く・閉じる、記録開始(`StartAsync` トリガ付き / `StartSingleAcquisition` 1 回収集 / `Start` 素の Start。違いは `Wrappers/WaveLogger.Automation/README.md`)、停止、状態取得(`GetState`、状態番号の意味は `LoggerMeasurementState`)
- 保存: xdt、CSV
- 参照波形の読み込み、横軸拡大、LOGGER 画面の復元・前面表示
- 終了処理: × 検知、停止・Quit・COM 解放、対象プロセスだけを終了(別プロセスは守る)
- 終了の扱いを選べる(`Lifecycle`): 既定は上の終了処理(`ManagedShutdown`)。`ReferenceOnly` は LOGGER を止めず COM 参照だけ解放する(AS100201SwitchController の使い方)
- 機器なしの模擬に差し替えるための薄い interface の例: AS110NoiseTest `Measurement/ILoggerDevice.cs`

**制約**
- WinForms の UI スレッド(STA)から使う。`Task.Run` や並列呼び出しは不可。非同期操作中はアプリ側で操作を排他する
- 終了・再接続の前に必ず `await ShutdownAsync(log)`。失敗したら新しいセッションに置き換えず、同じものを保持して再試行
- LOGGER を人が手で操作すると状態が変わり COM 例外になる。成功扱いせず画面に出す
- 実行 PC に WAVE LOGGER 本体ソフトとドライバが必要。NR-500 は旧 WAVE LOGGER(4.03.00)で測る(X 2.00.10 が入っていても)

### WF1973 / SDG1032X(発振器)(未移植)

- バースト: 三角波(対称 50%)、周波数・オフセット・振幅・周期数・開始位相を設定し、外部トリガ(`*TRG`)で指定周期数だけ出す。設定完了を `*OPC?` で確認。同じ設定なら再送しない
- 連続: 振幅・位相・周波数を設定して位相同期で再開始。出力中は OFF にしない。押下連打の途中で中断できる
- DC: オフセット電圧の設定と出力 ON/OFF(AS110 では **1V = 6mA** の電流指令に使う)
- 出力停止(`StopOutput`)。`Dispose` 時に出力 OFF を試みる
- 機器の探索: VISA の `?*INSTR` を総当たりして `*IDN?` で機種を見分ける方式と、USB の VID/PID(`USB?*::0x0D4A::0x000D::?*::INSTR`)で直接開く方式がある

**制約**
- NI-VISA(`Ivi.Visa.Interop` COM)が実行 PC に要る
- オフセットは ±5V まで(wrapper 側で弾く)
- 負荷設定は本体の設定を使う
- SDG1032X は AS110 のバースト用途だけ確認済み

### ADCMT 7351E(DMM)(未移植)

- 直流電流を読む(`ReadMilliamps`)。応答は `DCI_ +006.027E-03` 形式、約 400ms/回
- 設定(レンジ 200mA / 2000mA / 10A / Auto、サンプリング速度、オートゼロ、スムージング、商用周波数)を適用して、読み返して照合
- トリガ: Immediate / Manual / External / **Bus**(読むたびに `*TRG`。古い値を掴まない)
- エラーの読み出し(`DrainErrors`)

**制約**
- 通信は USB のみ。コマンドは ADC 独自(SCPI 不可)
- 設定変更直後は古い読み値が残るので捨てる(`DiscardStaleReadings`。Bus トリガなら不要)

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
- 実機(Arduino・COM ポート)での動作は**未確認**。テストはポートを開かない範囲(ログの種別・保存、ポートなしでの振る舞い)だけ

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
- (未移植)Excel を起動しての操作(元 AllCheck `ExcelWorkSession`、NoiseTest `NoiseResultWorkbook`): 開く・数式の再計算・セルの読み取り・保存・PDF 保存・閉じる。汎用部分だけを wrapper にし、判定ロジックはアプリ側。どこまでを wrapper にするかは判断待ち。XdtConversion の `ExcelPresenter`(Excel を起動してブックを表示するだけ)は、判断が出るまで `Apps/XdtConversion` 側に置いてある

**制約**
- Excel を起動する操作は実行 PC に Excel が必要(実行時に探す方式なので、ビルドには不要)
- 移植時に汎用部分とアプリ固有部分を切り分ける。どこまでを wrapper にするかは移植時に確認する

### 共通(`Wrappers/Common`、`PSCAutomation.Wrappers.Common`)

- `ErrorNotification`: 警告・エラーのメッセージボックスを最前面に表示(LOGGER の後ろに隠れない)
- `PcDelayObservation`: PC の処理遅延(タイマーの遅れ)を監視して記録。書き込み先は `Start(write)` で渡す(例: Arduino の異常ログ)
- `WindowActivation`: 他プロセスの窓を前面にする
- `NrDeviceConnections` / `SingleAdapterWifiManager`: NR-X の接続先(USB / LAN / Wi-Fi)の整理と、Wi-Fi プロファイル切替。SSID 一覧はアプリ側の設定に置く

## アプリの一覧

| アプリ | 内容 | 使う機器 |
|---|---|---|
| AS110AllCheck | AS110 の全数検査(動き検査・校正・リーク・Excel 転記) | NR-X / NR-500、WF1973 or SDG1032X、Arduino(M5Stack)、Excel |
| AS110BoltCheck | AS110 の動き検査 | NR-X、WF1973 or SDG1032X、Arduino(NF インターロック) |
| AS100201SwitchController | AS100-201 のスイッチ制御 | NR-X、WF1973(連続)、Arduino(スイッチ・READY LED) |
| AS110NoiseTest | AS110 のノイズ測定(電流を変えながら制御圧力の p-p を判定) | NR-500、WF1973(DC)、ADCMT 7351E、Excel |
| XdtConversion | xdt を Excel 雛形へ転記する単機能アプリ。**移植済み**(`Apps/XdtConversion`、`XdtConversion.slnf`) | ファイルのみ、Excel |
