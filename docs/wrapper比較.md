# 旧5リポジトリの wrapper 比較と統合方針(2026-11-04)

引継ぎの「次にやること 1」。GitHub `tad-kawamura` の 5 リポジトリを `main` の HEAD でクローンし、同名ファイルのハッシュ比較と diff で調べた。
調査用クローンは `C:\Users\kawam\AppData\Local\Temp\as110_ref\<リポジトリ名>`(一時フォルダ。消えたら再クローンすればよい)。

## 比較したコミット

| リポジトリ | HEAD | 日付 | 内容 |
|---|---|---|---|
| AS110AllCheck | `982252833f86` | 2026-11-02 | データ調整の発行を版つき名称に(ver1.03) |
| AS110BoltCheck | `58746ceb6928` | 2026-10-28 | ドキュメントの名称を AS110BoltCheck に |
| AS100201SwitchController | `5eb5b6391a82` | 2026-10-28 | 実機デバッグの動作確認結果 |
| XdtConversion | `9809ec3a5352` | 2026-10-26 | XDT reader を XdtConversion.Core に分離 |
| AS110NoiseTest | `c3e75b3b0c05` | 2026-11-03 | 保存した xdt の制御圧力の表示レンジ調整 |

## 先に結論

- **.NET は揃っている**: 全アプリ `net9.0-windows`、WinForms。XdtConversion.Core とその検証だけ `net9.0`(Windows 非依存)。ローカル SDK は 9.0.318。未確定事項の 1 つは解消。
- **wrapper は 6 グループ**に分けられる。「そのまま移せる」ものと「版が割れていて設計の統合が要る」ものがある。

| グループ | 元の場所 | 版の割れ方 | 統合の難しさ |
|---|---|---|---|
| A. WAVE LOGGER(`WaveLogger.Automation`) | 4 リポジトリ | 12 ファイル中 9 が完全一致。3 ファイルが追加機能の差 | **低**(追加を足し合わせるだけ) |
| B. Arduino シリアル(`ArduinoConnection` / `ArduinoDiagnosticLog`) | 3 リポジトリ | AllCheck ≒ SwitchController。BoltCheck だけ古く NF 判定が wrapper に残る | **中**(BoltCheck 側のアプリ修正が要る) |
| C. 共通小物(`ErrorNotification` / `PcDelayObservation` / `WindowActivation`) | 3 リポジトリ | namespace 以外同一 | **低** |
| D. VISA 機器(WF1973 / SDG1032X / ADCMT 7351E) | 4 リポジトリ | **3 つの別設計**。同名クラス `Wf1973Session` が AllCheck と SwitchController で互換性なし | **高**(設計の統合) |
| E. NR-X 接続補助(`NrDeviceConnections` / `SingleAdapterWifiManager`) | 3 リポジトリ | SwitchController が Wi-Fi プロファイル一覧をアプリ側へ分離済み。他は wrapper 内に SSID 直書き | **低** |
| F. ロガー出力ファイル読み書き(xdt / ydt / CSV) | 4 リポジトリ | AllCheck と NoiseTest の `XdtReader` は同一。XdtConversion.Core はその上位互換。さらに NoiseTest と WaveLogger.Automation にバイナリ直接編集クラスが散在 | **中**(置き場の整理) |

推奨する移植順: **A・C・E・F → B → D**。前半は機械的で、全アプリのビルドが通ることを早く確認できる。B と D は実機確認が要る。

## 移植の進み具合

| 手順 | 内容 | 状態 |
|---|---|---|
| 1 | 旧 5 リポジトリの比較と統合方針(本文書) | 済(2026-11-04) |
| 2 | A(WaveLogger.Automation)・C(Common)・E(NR-X 接続補助)・F(LoggerFiles)・Excel の移植 | **済(2026-11-04)**。Excel を起動する操作(`ExcelWorkSession` / `NoiseResultWorkbook` / `ExcelPresenter` の汎用部分)は判断 8 のあとに移植(2026-11-04)。出どころと変更点は `docs/移行記録.md` |
| 3 | B(Arduino)の移植 | **済(2026-11-04)**。AllCheck 版の `ArduinoConnection` と、SwitchController 版の `Configure` 方式 + AllCheck の `SHUTDOWN_ERROR` の `ArduinoDiagnosticLog`。BoltCheck の NF 判定をどうするか(判断 3)はアプリ側の話なので、BoltCheck を移すときに決める。実機確認は未実施 |
| 4 | D(VISA 機器)の移植 | **済(2026-11-04)**。判断 4 の案 A(下記 D 節の採用案)で `Wrappers/Instruments` へ。通信部分を 1 つにし、WF1973 は 3 クラスを別名で残した。送るコマンドと順番は元のまま(模擬の通信で確認)。実機確認は 2026-11-04 に WF1973 の 3 クラス(DC・バースト・連続)と 7351E(AS110NoiseTest の範囲)で済(`Wrappers/Instruments/docs/WF1973実機確認.md`)。SDG1032X は未確認 |
| 5 | アプリの移植と `.slnf` の整備 | **XdtConversion は済(2026-11-04)**(`Apps/XdtConversion`、`XdtConversion.slnf`)。旧 Verification の A6 停止は検査の前提の誤りで、参照値と比べる形に直した(`docs/移行記録.md`)。**AS110NoiseTest は済(2026-11-04)**(`Apps/AS110NoiseTest`、`AS110NoiseTest.slnf`)。機器・Excel の操作を wrapper に置き換え、移植前のツールと出力が一致することを確認。2026-11-04 に実機でも通しが完走(`Apps/AS110NoiseTest/README.md`)。AS110AllCheck・AS110BoltCheck・AS100201SwitchController は未着手 |

手順 2 で決めたこと・本文書の案から変えたこと:
- E(NR-X 接続補助)は案どおり `Wrappers/Common` に置いた。`NrXConnection` / `Nr500Connection` が `WaveLoggerSession` を使うので、Common は WaveLogger.Automation を参照する(外部依存はないまま)。
- `PcDelayObservation` は Arduino の異常ログへ直接書いていたので、書き込み先を `Start(write)` で受け取る形にした(Common が Arduino に依存しないように)。
- `LoggerCsvReader` はアプリの設定クラスの代わりに、CH 番号・列番号・点数を引数で受け取る形にした(案どおり)。
- 旧 AllCheck / NoiseTest の `XdtReader` と LoggerFiles の `XdtReader` は、スケーリング無効の CH の扱いが違う(`docs/移行記録.md`)。アプリ移植のときに確認する。

## グループ別の詳細

### A. WaveLogger.Automation(KEYENCE WAVE LOGGER / LOGGER X の COM 操作)

すでに独立した csproj(`net9.0-windows`、namespace `WaveLogger.Automation`)で、各アプリが `ProjectReference` している。モノレポの `Wrappers/` にそのまま置ける。README(使い方・STA スレッド制約・`ShutdownAsync` 必須)も揃っている。

| ファイル | 一致状況 | 差の内容 | 採用案 |
|---|---|---|---|
| `WaveLoggerSession.cs` | 3 版(AllCheck = NoiseTest / BoltCheck / SwitchController) | BoltCheck は最古で `StartSingleAcquisition()` がない。SwitchController は `WaveLoggerLifecycle.ReferenceOnly`(接続時の診断照会・プロセス追跡をしない)と `Start()`(`ResetMeasurementStart` を呼ばない 1 回 Start)を追加し、`StartSingleAcquisition()` を持たない | AllCheck 版を土台に、SwitchController の `Lifecycle` と `Start()` を**追加**で足す。`Start()` と `StartSingleAcquisition()` は挙動が違う(後者は開始直後の古い停止情報を抑止)ので**両方残す** |
| `LoggerMeasurementState.cs` | 2 版 | SwitchController が `GetDisplayText(bool describeHardwareStop)` を追加(状態 10 の文言切替) | SwitchController 版(追加のみ、既存 `DisplayText` は維持) |
| `XdtScalingEditor.cs` | 2 版 | AllCheck / NoiseTest が `ScalingPoints` record と `GetScaling(ch)` を追加(2 点校正の読み出し) | AllCheck 版(追加のみ) |
| 残り 9 ファイル | 4 リポジトリで完全一致 | なし | そのまま |

`Tests/LoggerShutdown`(ProcessLifetime と Session の契約テスト)が 3 リポジトリに同じものがある。モノレポでは 1 つにする。

### B. Arduino シリアル通信

`Arduino/ArduinoConnection.cs`(COM ポート開閉・受信スレッド・切断検知)と `Arduino/ArduinoDiagnosticLog.cs`(異常ログの保存)。namespace はアプリ名(AllCheck の分は履歴の都合で `AS110BoltCheck` のまま)。

- **ArduinoConnection**: AllCheck と SwitchController は namespace と `Encoding = UTF8Encoding(false)`(AllCheck のみ)を除いて同一。**BoltCheck は古い版**で、`NF:0`/`NF:1` 受信の鮮度判定(`NF_STALE` / `RX_RECOVERING` / `RECEIVING` → `CompleteIfActive`)が wrapper の中に入っている。AllCheck ではその判定を `As110/As110ArduinoProtocol.cs`(アプリ側)へ移し、wrapper は「受信頻度はプロトコルごとに違うので判定しない」方針になっている。AllCheck 版は `DisconnectAsync()`、`_disposed` ガード、`WRITE_ERROR` 時の `Disconnected` 通知も持つ。
  - SwitchController の `docs/wrapper-reuse.md` は「AS110 版は NF 監視が入っているので上書きしなかった」と書いているが、これは当時の話で、現在の AllCheck 版は NF 監視を外し SwitchController 版とほぼ同じになっている。
  - **採用案**: AllCheck 版。BoltCheck は AllCheck の `As110ArduinoProtocol.cs` の NF 判定部分を取り込む必要がある(アプリ側の変更。実機で NF 断線時の挙動確認が要る)。
- **ArduinoDiagnosticLog**: SwitchController 版が `Configure(applicationName, params string[] protocolIncidents)` を持ち、ログ保存先のアプリ名と「アプリ固有の異常種別」を起動時に注入できる。AllCheck / BoltCheck 版は保存先フォルダ名 `"AS110BoltCheck"` と `NF_STALE` が直書き(**AllCheck のログが `AS110BoltCheck` フォルダに書かれている**。移植時に直る)。AllCheck 版だけ `SHUTDOWN_ERROR` 種別がある。
  - **採用案**: SwitchController 版の `Configure` 方式 + AllCheck の `SHUTDOWN_ERROR`。各アプリは起動時に `ArduinoDiagnosticLog.Configure("アプリ名", "NF_STALE", ...)` を呼ぶ(SwitchController の `As100201Application.ConfigureDiagnostics()` が手本)。

### C. 共通小物

`Common/ErrorNotification.cs`(30 行)、`Common/PcDelayObservation.cs`(193 行、PC の処理遅延監視)は 3 リポジトリで namespace と BOM 以外同一。`Common/WindowActivation.cs` は AllCheck だけ。そのまま `Wrappers/Common` へ。

### D. VISA 機器(発振器・DMM)

ここが一番割れている。3 つの設計が並立している。

| 設計 | 使うアプリ | クラス | 用途 | VISA の扱い |
|---|---|---|---|---|
| D1 バースト出力 | AllCheck、BoltCheck | `IFunctionGeneratorSession`、`Wf1973Session`、`Sdg1032XSession`、`GeneratorBurstSettings` | 三角波バースト + 外部トリガ(`*TRG`)、DC 出力(`SetDcOutput`) | 各クラスが `ResourceManager` / `FormattedIO488` を直接持つ。`?*INSTR` を総当たりし `*IDN?` で機種判定 |
| D2 連続出力 | SwitchController | `IInstrumentSession` / `VisaInstrumentSession`、`Wf1973Session`、`GeneratorContinuousSettings` | 振幅・位相・周波数を設定して `PHASe:INITiate`。押下連打の中断判定付き | 検索・識別・COM 所有権を `VisaInstrumentSession` に分離済み(`Write` / `Query` / `Dispose`) |
| D3 DC 電流指令 + DMM | NoiseTest | `VisaUtil`、`Wf1973DcSource`、`Adcmt7351ESession`(+`.Commands`、`Adcmt7351ESettings`) | WF1973 の DC オフセットで電流指令(1V = 6mA)、ADCMT 7351E で実電流を読む(USBTMC、BUS トリガ) | `VisaUtil` に Find/Open/Close/Release を集約。検索は USB の VID/PID パターン(`USB?*::0x0D4A::0x000D::?*::INSTR`)で直接 |

- 同名 `Wf1973Session` が D1 と D2 で API も責務も違う。1 つの namespace には置けない。
- D1 の AllCheck 版は BoltCheck 版より新しい(`StopOutput()` / `SetDcOutput()` 追加、`Dispose` 時に出力 OFF を試す)。追加のみなので AllCheck 版を採用。
- 機器探索の方針が「`?*INSTR` 総当たり + IDN」(D1/D2)と「VID/PID 指定」(D3)で違う。どちらも実機で動いているので、統合トランスポートは**両方の探し方を受け付ける**形にする。

**採用案(2 段階)**:
1. まず **VISA トランスポートを 1 つにする**(D2 の `VisaInstrumentSession` を土台に D3 の `VisaUtil` の探索パターン指定を足す)。機器クラスは 3 つを別名で残す: `Wf1973BurstSession`(D1)、`Wf1973ContinuousSession`(D2)、`Wf1973DcSource`(D3)、`Sdg1032XSession`、`Adcmt7351ESession`。アプリ側の呼び出しは `using` とクラス名の置換だけで済む。
2. 動作が安定してから、WF1973 の 3 クラスを 1 つに寄せるかを判断する(急がない)。

→ 1 段階目は手順 4 で実施(2026-11-04)。名前は案のとおり。D1 の接続入口 `FunctionGeneratorConnection` と `IFunctionGeneratorSession` は名前を変えずに残した。D3 の `VisaUtil` は `VisaInstrumentSession` に吸収した。詳細は `docs/移行記録.md`「手順 4」。

### E. NR-X 接続補助

`Logger/NrDeviceConnections.cs`(NR-X の USB / LAN / Wi-Fi 接続先の整理)、`Logger/SingleAdapterWifiManager.cs`(Wi-Fi 切替、3 リポジトリ同一)、`Logger/LoggerImports.cs`(1 行、同一)。

- AllCheck / BoltCheck の `NrDeviceConnections.cs` には `NrWifiProfiles`(実機の SSID `NRX_E8FDF803634C` と IP)が直書き。SwitchController は `As100201/As100201ConnectionSettings.cs` へ分離済み。
- **採用案**: SwitchController の分け方(wrapper にはプロファイル一覧を持たない)。
- SSID の置き場(アプリの設定クラスか、共通の 1 か所か)は**ここでは一律に決めない(決定: 2026-11-04)**。NR-X を使うかどうか・Wi-Fi で使うかどうかはアプリ次第なので、アプリを作る(移植する)ときに AI が聞く(`AGENTS.md`「仕様決め」)。NR-X を使わない人に「使わないから要らない」と言わせない。

### F. XDT ファイル読み書き

XDT(WAVE LOGGER の波形ファイル)のバイナリを触るコードが 4 か所にある。

| 場所 | クラス | できること |
|---|---|---|
| AllCheck `Conversion/XdtReader.cs` = NoiseTest 同名(完全一致、namespace `XdtConversion`) | `XdtReader.Inspect` / `Read` | 旧 LOGGER / LOGGER X の判別、CH のスケーリング・移動平均込みで値を読む |
| XdtConversion `XdtConversion.Core/`(csproj、`net9.0`) | `XdtFile`、`XdtChannel`、`XdtInfo`(時刻・サンプリング周期・バイナリ項目位置)、互換用の静的 `XdtReader` | 上の上位互換。生値 / スケーリング後 / 移動平均後を選べる。サンプリング周期と日時を取得(メーカー CSV と 5 種照合済み) |
| NoiseTest `Conversion/XdtDisplayRange.cs`、`XdtTimestamp.cs` | 表示レンジの読み書き、日時の読み出し | 雛形 xdt の表示レンジを平均値中心に直す(LOGGER で線が画面外に出る対策) |
| WaveLogger.Automation `XdtScalingEditor.cs` | 2 点スケーリングの読み書き | 校正値を雛形 xdt に反映 |

- **採用案**: `Wrappers/LoggerFiles`(= `XdtConversion.Core` を土台)に集約。`XdtReader.Inspect/Read` は Core が同じシグネチャを持つので AllCheck / NoiseTest は参照先を変えるだけ(ただし `XdtInfo.Channels` が配列から `IReadOnlyList` に変わるので `.Length` を使っている箇所があれば直す)。`XdtDisplayRange` / `XdtTimestamp` も同じ場所へ。`XdtScalingEditor` は xdt と NR-X の ydt(CH1)を直接書き換えるファイル操作なので **LoggerFiles へ移す(決定: 2026-11-04)**。WaveLogger.Automation 内で使う箇所があれば LoggerFiles を参照する。
- ydt の解析はまだだが、**いずれ xdt と同じことを ydt でもできるようにする方針(2026-11-04、人の指示)**。箱の名前を `Xdt` ではなく `LoggerFiles` にしたのはこのため。API は xdt 専用の前提にしない。
- `TemplateWriter.cs`(AllCheck `Conversion/` と XdtConversion アプリで同一、xlsx を ZIP / XML で直接書く)は **`Wrappers/Excel` へ(決定: 2026-11-04)**。アプリごとに微妙に違うが大枠は「ロガーの値の転記」なので wrapper にする。`LoggerCsvReader.cs`(AllCheck のみ)はロガー CSV の読み出しなので LoggerFiles へ。ただしアプリ固有の `TransferSettings` を受け取っているので、CH 番号などを引数で渡す形に直す。

### G. wrapper にしないもの(アプリ側)

- `As110/*`(AS110 の Arduino プロトコル・動き検査・校正)、`As100201/*`、`UI/MainForm.*`、`Production/*`、NoiseTest の `Analysis/` `Control/` `Measurement/`(判定・電流フィードバック・測定シーケンス)。
- Excel COM(AllCheck `Excel/ExcelWorkSession.cs`、NoiseTest `Excel/NoiseResultWorkbook.cs`)は、開く・保存・PDF 化・閉じるなどの汎用部分だけを `Wrappers/Excel` へ移し、判定ロジック・セル位置はアプリ側に残す。Excel の起動は実行時に ProgID で探す方式なので、ビルドに Excel は要らない。
- NoiseTest の `Measurement/ILoggerDevice.cs`(wrapper を薄い interface で包み、機器なしの模擬に差し替える)は**他アプリでも真似したい形**。AGENTS.md の指針に書いた。

### H. ファームウェア

3 本の `.ino` がある(小さい、プロトコルがアプリと一体)。

- AS110AllCheck `firmware/AS110M5Stack/AS110M5Stack.ino`
- AS110BoltCheck `_arduino/AS110_NfInterlock/AS110_NfInterlock.ino`
- AS100201SwitchController `_arduino/ArduinoSwitchAndReadyLed/ArduinoSwitchAndReadyLed.ino`

**決定: モノレポに含める**(2026-11-04)。`Apps/<アプリ>/firmware/` に置く(`.ino.cpp` などの生成物は除外)。PC 側プロトコルと同じコミットで追え、クローンしたときにマイコン側のコードだけ欠けることがない。3 本とも秘密情報(Wi-Fi のパスワードなど)を含まず、小さく、1 アプリ専用であることを確認済み。移す時期は各アプリの移植と同時。書き込み手順(Arduino IDE、AllCheck は `M5Unified` ライブラリが要る)は各アプリの README に書く。

## namespace と csproj(決定: 2026-11-04)

- 旧コードの namespace はアプリ名(`AS110BoltCheck` / `AS100201SwitchController` / `AS110NoiseTest.Devices`)か `XdtConversion` / `WaveLogger.Automation`。モノレポでは `Wrappers` 配下を `PSCAutomation.Wrappers.<グループ>`(例: `PSCAutomation.Wrappers.Arduino`)、アプリ側を `PSCAutomation.Apps.<アプリ>` に揃え、アプリ側は `using` の追加で対応する(機械的な変更)。
- csproj は**依存ごとに 6 つに分ける**(決定)。アプリが要らない依存を引き込まないため。特に NI-VISA(COM 参照)を使わないアプリは、NI-VISA が入っていない PC でもビルドできる:

| csproj | 中身 | 外部依存 |
|---|---|---|
| `Wrappers/WaveLogger.Automation` | A | なし(COM は late binding) |
| `Wrappers/LoggerFiles` | F(xdt / ydt / ロガー CSV) | なし(`net9.0`) |
| `Wrappers/Instruments` | D | `Ivi.Visa.Interop`(COM 参照、NI-VISA) |
| `Wrappers/Arduino` | B | `System.IO.Ports` |
| `Wrappers/Common` | C、E | なし |
| `Wrappers/Excel` | Excel 雛形への転記、Excel の起動・保存・PDF 化 | なし(Excel は実行時のみ必要。COM は late binding) |

旧リポジトリでは `WaveLogger.Automation` と `XdtConversion.Core` 以外はアプリの csproj の中のフォルダで、アプリごとにコピーを持っていた(版が割れた原因)。モノレポでは `Wrappers/` はただの置き場で、その下の各フォルダが 1 つの csproj(箱)になる。

引継ぎの「各アプリは Wrappers をプロジェクト参照する」は、この 6 つのどれかを参照する、という読みで進める。namespace とアセンブリ名は `PSCAutomation.Wrappers.<箱のフォルダー名>`(例: `PSCAutomation.Wrappers.WaveLogger.Automation`)にした(手順 2)。1 つにまとめたい場合も `ProjectReference` を 1 行にするだけなので後から変えられる。

## git 履歴

5 リポジトリ合計 88 コミット(47 / 16 / 4 / 2 / 19)。**コピーのみ**で進める(判断 7、2026-11-04 決定)。各ファイルの出どころは本文書の「比較したコミット」の表と、移植時に `docs/移行記録.md` へ書くコミット ID で追えるようにする。**移植前の履歴を見たいときは、旧リポジトリ(アーカイブ済みのものも読める)を見る**。旧リポジトリの扱い(残す・アーカイブする)は**人が決める**。AI は、人がリポジトリを名指しで指示したとき以外、旧リポジトリを消したりアーカイブしたり push したりしない(読むだけ)。履歴を残すため、削除はせず、残すかアーカイブにする。XdtConversion は 2026-11-04 に人の指示でアーカイブした(移植元の `9809ec3a5352` が `main` の先頭で、ほかのブランチ・未処理のプルリクエストは無かった。後から同日にいったん解除し、README の冒頭へ移転先の案内を追記して(`31620ee`)アーカイブし直した)。AS110NoiseTest も同日にアーカイブした(移植元の `c3e75b3b0c05` が `main` の先頭。クローンして作業している PC があるので、先に README・CLAUDE.md の冒頭へ移転先の案内を追記して push した。`26edc29`)。AS100201SwitchController も同日、移植と実機確認のあとにアーカイブした(移植元の `5eb5b6391a82` が `main` の先頭で、ほかのブランチ・未処理のプルリクエスト・issue は無かった。先に README・AGENTS.md の冒頭へ移転先の案内を追記して push した。`c8a5e4b`)。履歴ごと持ち込む(`git subtree add` 等)のは、5 つの履歴が混ざって読みにくくなるので勧めない。

## 人に判断してもらいたいこと

1. ~~リポジトリ名~~ → **決定: `PSCAutomation`**(2026-11-04)。AS110 は製品名で AS100201 などとは別物なので、全体の名前にしない。目的は「社内の自動化プログラムの整理」なので社名の略称 + Automation。PSC は全部大文字で書く(人の指示。名前空間も `PSCAutomation.…`)。作業フォルダ名は `MergeProject` から `PSCAutomation` に変更済み(2026-11-04、人が実施)。
2. ~~csproj の分け方~~ → **決定: 依存ごとに 6 つ**(上の表のとおり。2026-11-04)。NI-VISA を使う箱を分けておくと、使わないアプリは NI-VISA なしでビルドできる。校正値の部品は LoggerFiles、Excel 転記は新しい箱 `Excel` に置く。
3. **BoltCheck の Arduino を AllCheck 版に揃える**(NF 判定をアプリ側へ移す。実機確認が要る)か、当面 BoltCheck だけ古い wrapper を抱えたまま移すか。
4. ~~WF1973 の 3 クラスを別名で残す 2 段階案~~ → **決定: 案 A(2 段階案)**(2026-11-04)。今すぐ 1 クラスに統合する案(3 アプリとも発振器の動きが変わり実機確認が要る)と、通信部分もまとめずに移す案(COM の解放と探索が 3 か所に残る)を比べて選んだ。2 段階目(3 クラスを 1 つに寄せるか)は、アプリを移して実機で安定してから決める。
5. ~~ファームウェアをモノレポに含めるか~~ → **決定: 含める**(2026-11-04)。`Apps/<アプリ>/firmware/` に、各アプリの移植と同時に移す(上の「H. ファームウェア」)。
6. ~~NR-X の Wi-Fi プロファイル(SSID)の置き場~~ → **決定: アプリごとに、作る(移植する)ときに AI が聞く**(2026-11-04)。NR-X を使うかはアプリを作る人の想定次第で、使わない人もいる。使う機器に NR-X が入り、Wi-Fi で使うときだけ、置き場を選択肢で聞く(`AGENTS.md`「仕様決め」)。wrapper には持たせない(手順 2 のまま)。AllCheck・BoltCheck・SwitchController を移すときも同じく聞く。
7. ~~履歴はコピーのみでよいか~~ → **決定: コピーのみ**(2026-11-04)。移植前の履歴は持ち込まず、見たいときは旧リポジトリ(アーカイブ済みのものも読める)を見る。出どころ(移植元のコミット)は `docs/移行記録.md`。履歴ごと持ち込んでも、フォルダーの移動と wrapper のまとめ直しで `git blame` の多くは移植時点で止まり、5 つの履歴が混ざって読みにくくなるだけなので採らない。
8. ~~Excel を起動する操作のうち、どこまでを wrapper にするか~~ → **決定: 案 A(共通の操作を部品として `Wrappers/Excel` に切り出す)**(2026-11-04)。
   - 背景: AllCheck の `ExcelWorkSession` と NoiseTest の `NoiseResultWorkbook` は、共通の操作と AS110 固有の処理(図形内の S/N 書き換えのパターン、E21・L21 の判定、「圧縮5」シート、PP 値シートの行の決め方・mA の列)が 1 つのクラスに入っている。XdtConversion の `ExcelPresenter` も対象。
   - wrapper に置く部品: Excel の起動(無ければエラー)、ブックを開く(読み取り専用なら止める)、閉じる・終了・COM 解放、画面に出して利用者に渡す、保存(上書き・別名)、PDF 化、セルの読み書き、開いているブックの列への値の書き込み(数式があれば止める)、再計算を待つ(打ち切り時間つき)、図形内の文字を書式を保ったまま一部だけ置き換える。
   - アプリ側に残すもの: どのシート・セル・図形に何を書くか、置き換える文字のパターン、判定、行・列の決め方。
   - 3 アプリで違う動き(保存のしかた、確認ダイアログを出すか、終了時に Excel を閉じるか)は選べる設定にして、各アプリの今の動きを変えない。
   - 転送(雛形に測定値を書く)は既に `TemplateWriter` で wrapper にある。値の配列を受け取る形なので、xdt / ydt の違いは LoggerFiles 側で吸収する。
   - 比較した案: B(起動・開く・閉じる・解放・表示だけ。保存・PDF・S/N 書き換えはアプリごとにコピーが残る)、C(wrapper にせずアプリ側へそのまま移す。起動・解放が 3 か所に残る)。
   - 部品は先に移植した(2026-11-04、`docs/移行記録.md`「Excel を起動する操作」)。AllCheck・NoiseTest は移すときにこの部品を使う形に書き直し、Excel を実際に動かして元と同じ結果になるかを見る。XdtConversion は `ExcelPresenter` から `ExcelViewer` に乗り換えた(2026-11-04)。
