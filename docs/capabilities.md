# wrapper の機能一覧(できること・制約)

> **下書き(2026-11-04)**。旧 5 リポジトリの wrapper を調べた時点の内容で、コードはまだこのリポジトリに移していない。移植が進んだら `Wrappers/` の実装に合わせて更新する。各項目の「元」は旧リポジトリの場所。

新しい依頼を受けたら、まずここを読んで「今の wrapper で何ができるか」を把握し、できる範囲から選択肢を出す。ここに無い機器・操作は、wrapper の新規作成(全アプリに影響)として人に確認する。

## 機器と wrapper の対応

| 機器 | 役割 | PC との接続 | wrapper(予定の置き場) | 元 |
|---|---|---|---|---|
| KEYENCE NR-500 / NR-X100(WAVE LOGGER / WAVE LOGGER X) | データロガー。波形を xdt / ydt で記録 | COM オートメーション(WAVE LOGGER 本体ソフト経由)。本体は USB / LAN / Wi-Fi | `Wrappers/WaveLogger.Automation` | AS110AllCheck 他 3 つ(ほぼ同一) |
| NF WF1973 | ファンクションジェネレータ。三角波バースト / 連続波 / DC オフセット | USB(VISA、NI-VISA 必須) | `Wrappers/Instruments` | AllCheck(バースト)、SwitchController(連続)、NoiseTest(DC) |
| SIGLENT SDG1032X | ファンクションジェネレータ(WF1973 の代替) | USB(VISA) | `Wrappers/Instruments` | AllCheck、BoltCheck |
| ADCMT 7351E | DMM。直流電流の実測 | **USB のみ**(USBTMC、VISA)。GPIB / RS-232C は不可 | `Wrappers/Instruments` | NoiseTest |
| Arduino(UNO R4 Minima、M5Stack 等) | 試験治具のマイコン。スイッチ・LED・NF 弁のインターロック | USB シリアル(COM ポート、行単位テキスト) | `Wrappers/Arduino` | AllCheck、BoltCheck、SwitchController |
| Windows の Wi-Fi | NR-X を Wi-Fi で使うときの接続先切替 | `netsh` | `Wrappers/Common`(E) | AllCheck 他 |
| XDT ファイル | WAVE LOGGER の波形ファイル | ファイル | `Wrappers/Xdt` | XdtConversion.Core 他 |

## できること

### WAVE LOGGER(`WaveLoggerSession`)

- 旧 WAVE LOGGER と WAVE LOGGER X の両方を同じ API で扱う(`WaveLoggerEdition` で選ぶ)
- 本体の検出: NR-500 を USB で、NR-X を USB / LAN / Wi-Fi で確認する
- 雛形ファイル(xdt / ydt)を開く・閉じる、記録開始(`StartAsync` トリガ付き / `StartSingleAcquisition` 1 回収集 / `Start` 素の Start)、停止、状態取得(`GetState`、状態番号の意味は `LoggerMeasurementState`)
- 保存: xdt、CSV
- 参照波形の読み込み、横軸拡大、LOGGER 画面の復元・前面表示
- 終了処理: × 検知、停止・Quit・COM 解放、対象プロセスだけを終了(別プロセスは守る)
- 雛形 xdt の 2 点スケーリングの読み書き(`XdtScalingEditor`)
- 機器なしの模擬に差し替えるための薄い interface の例: AS110NoiseTest `Measurement/ILoggerDevice.cs`

**制約**
- WinForms の UI スレッド(STA)から使う。`Task.Run` や並列呼び出しは不可。非同期操作中はアプリ側で操作を排他する
- 終了・再接続の前に必ず `await ShutdownAsync(log)`。失敗したら新しいセッションに置き換えず、同じものを保持して再試行
- LOGGER を人が手で操作すると状態が変わり COM 例外になる。成功扱いせず画面に出す
- 実行 PC に WAVE LOGGER 本体ソフトとドライバが必要。NR-500 は旧 WAVE LOGGER(4.03.00)で測る(X 2.00.10 が入っていても)
- NR-X の YDT は CH1 のスケーリングのみ対応

### WF1973 / SDG1032X(発振器)

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

### ADCMT 7351E(DMM)

- 直流電流を読む(`ReadMilliamps`)。応答は `DCI_ +006.027E-03` 形式、約 400ms/回
- 設定(レンジ 200mA / 2000mA / 10A / Auto、サンプリング速度、オートゼロ、スムージング、商用周波数)を適用して、読み返して照合
- トリガ: Immediate / Manual / External / **Bus**(読むたびに `*TRG`。古い値を掴まない)
- エラーの読み出し(`DrainErrors`)

**制約**
- 通信は USB のみ。コマンドは ADC 独自(SCPI 不可)
- 設定変更直後は古い読み値が残るので捨てる(`DiscardStaleReadings`。Bus トリガなら不要)

### Arduino(`ArduinoConnection` / `ArduinoDiagnosticLog`)

- COM ポートを開く・閉じる・再接続、行単位の送受信、受信スレッド、切断検知(ポート消失 / クローズ / 書込失敗)
- 異常ログ: 種別(`COM_DISAPPEARED` `PORT_CLOSED` `RX_STALE` `WRITE_ERROR` `SHUTDOWN_ERROR` 等)付きでファイル保存。アプリ名と固有の異常種別は起動時に `Configure` で注入
- 受信行の意味(`NF:0` / `NF:1`、READY、スイッチ状態)と受信頻度の判定は**アプリ側のプロトコルクラス**が持つ(AllCheck `As110ArduinoProtocol`、SwitchController `As100201ArduinoProtocol`)

**制約**
- UTF-8、DTR 有効。ボーレート等は接続設定で渡す
- 通信断だけでは発振器や LOGGER を止めない(方針はアプリ側)

### XDT ファイル(`Wrappers/Xdt`)

- 旧 LOGGER / LOGGER X の判別、サンプル数、CH 一覧(名前・単位・スケーリング 2 点・移動平均)
- 値の読み出し: 生値 / スケーリング後 / 移動平均後
- サンプリング周期、収集開始日時、先頭・最終データ日時(メーカー CSV と照合済み)
- 表示レンジの読み書き(雛形の表示レンジを平均値中心に直す)、2 点スケーリングの書き換え

**制約**
- ydt(NR-X)は対象外(CH1 のスケーリング書き換えのみ)
- 日時のタイムゾーンと一部の周期単位は未確認。推測しない

### 共通(`Wrappers/Common`)

- `ErrorNotification`: 例外をユーザー向けメッセージにして表示
- `PcDelayObservation`: PC の処理遅延(タイマーの遅れ)を監視して記録
- `WindowActivation`: 他プロセスの窓を前面にする
- `NrDeviceConnections` / `SingleAdapterWifiManager`: NR-X の接続先(USB / LAN / Wi-Fi)の整理と、Wi-Fi プロファイル切替。SSID 一覧はアプリ側の設定に置く

## アプリの一覧(移植予定)

| アプリ | 内容 | 使う機器 |
|---|---|---|
| AS110AllCheck | AS110 の全数検査(動き検査・校正・リーク・Excel 転記) | NR-X / NR-500、WF1973 or SDG1032X、Arduino(M5Stack)、Excel |
| AS110BoltCheck | AS110 の動き検査 | NR-X、WF1973 or SDG1032X、Arduino(NF インターロック) |
| AS100201SwitchController | AS100-201 のスイッチ制御 | NR-X、WF1973(連続)、Arduino(スイッチ・READY LED) |
| AS110NoiseTest | AS110 のノイズ測定(電流を変えながら制御圧力の p-p を判定) | NR-500、WF1973(DC)、ADCMT 7351E、Excel |
| XdtConversion | xdt を Excel 雛形へ転記する単機能アプリ | ファイルのみ、Excel |
