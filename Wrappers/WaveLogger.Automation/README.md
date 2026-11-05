# WaveLogger.Automation(WAVE LOGGER 共通 wrapper)

KEYENCE WAVE LOGGER / WAVE LOGGER X を COM オートメーションで操作する `net9.0-windows` のライブラリ。名前空間は `PSCAutomation.Wrappers.WaveLogger.Automation`。
Arduino・VISA・Excel・検査画面・保存先の設定には依存しない。WAVE LOGGER / WAVE LOGGER X 本体ソフトと対応ドライバーは実行 PC に別途必要(COM は実行時に ProgID で探すので、ビルドには不要)。

元は旧 4 リポジトリ(AS110AllCheck / AS110BoltCheck / AS100201SwitchController / AS110NoiseTest)の `Libraries/WaveLogger.Automation`。AS110AllCheck 版を土台に、AS100201SwitchController 版の追加(`WaveLoggerLifecycle`、`Start()`、`LoggerMeasurementState.GetDisplayText`)を足した。詳細は `docs/移行記録.md`。

## 構成

| ファイル | 責任 |
|---|---|
| `WaveLoggerSession.cs` | COM 接続の所有、機種差の吸収、本体確認、測定開始・停止、診断 |
| `WaveLoggerSession.Shutdown.cs` | × 検知、停止・Quit・COM 解放、対象プロセス終了確認 |
| `WaveLoggerSession.Files.cs` | ファイル開閉、データ数取得、既存の LOGGER 形式・CSV 保存 API |
| `WaveLoggerSession.Reference.cs` | 参照読み込み前の停止待ち、版別の読み込み、横軸拡大 |
| `WaveLoggerSession.Window.cs` | LOGGER 画面の復元・前面表示 |
| `WaveLoggerSession.Prestart.cs` | LOGGER 本体ソフトの事前起動(別 STA スレッドで COM 生成し、UI スレッドへ参照を渡す) |
| `LoggerProcessLifetime.cs` | 制御対象 PID の識別、条件付き終了、別プロセスの保護 |
| `LoggerReferenceLoader.cs` / `LoggerFileDrop.cs` / `LoggerHorizontalZoom.cs` | 内部の Windows 画面操作。呼び出し元から直接使用しない |
| `LoggerMeasurementState.cs` | 測定状態の判定・表示、開始直後の古い停止情報の抑止 |

雛形の 2 点スケーリングを書き換える `XdtScalingEditor` は、ファイル操作なので `Wrappers/LoggerFiles` へ移した(旧リポジトリではこのフォルダーにあった)。

## アプリから使う

1. アプリの csproj から `Wrappers/WaveLogger.Automation/WaveLogger.Automation.csproj` を `ProjectReference` する。
2. 画面または制御処理が `WaveLoggerSession` を 1 つ所有し、接続から終了まで使い続ける。
3. メッセージループのある同一 STA スレッド(WinForms の UI スレッド)で操作する。非同期処理は同じ同期コンテキストへ戻す。`Task.Run`、並列呼び出し、終了処理中の再接続を行わない。
4. 非同期操作中は測定・ファイル切替・終了などのアプリ操作を排他する。LOGGER の手動操作による状態変化・COM 例外も呼び出し元へ伝わるので、成功扱いせず画面へ通知する。
5. 通常の終了・再接続前には必ず `await session.ShutdownAsync(log)` を実行する。失敗したセッションは保持して再試行し、新しいセッションへ置き換えない。

```csharp
using PSCAutomation.Wrappers.WaveLogger.Automation;

// WinForms の UI スレッド上。アプリのフィールドとして保持する。
private readonly WaveLoggerSession _logger = new();

// 接続操作
_logger.Initialize(WaveLoggerEdition.WaveLoggerX, AppendLog);
var result = _logger.DetectNrX(0, NrXConnectionMethod.Usb);
// result.IsDetected とファイル操作の戻り値を確認してから測定へ進む。

// 終了または再接続前(失敗時はアプリ終了を保留し、このインスタンスを保持)
await _logger.ShutdownAsync(AppendLog);
```

### 起動を画面準備と重ねる(事前起動)

旧 WAVE LOGGER の起動(COM サーバーの生成)は 7〜8 秒かかる。`Initialize` を UI スレッドで呼ぶとその間は画面が固まるので、画面を作る前に起動だけ始めておける。

```csharp
// フォームのコンストラクターの先頭(画面を作る前)
_logger.BeginServerStartup(WaveLoggerEdition.Legacy);   // 失敗しても例外にならない

// 接続確認(UI スレッドの async 処理の中)
await _logger.WaitForServerStartupAsync();              // 残りの起動待ち。UI スレッドは止めない
_logger.Initialize(WaveLoggerEdition.Legacy, AppendLog); // 起動済みの参照を受け取るだけ(同じ種類のとき)
```

- 事前起動は専用の STA スレッドで `CreateInstance` し、できた参照を COM のスレッド間マーシャリングで `Initialize` を呼ぶスレッドへ渡す。受け取った後の使い方は従来と同じ(UI スレッドで直列)
- `Initialize` の種類が事前起動と違うときは事前起動分を破棄して従来どおり起動する。事前起動が失敗していたときも従来どおり起動する(エラーは従来と同じ場所で出る)
- `Initialize` せずに終わる経路では `CancelServerStartup()` で破棄する(`Release()` / `ShutdownAsync` からも破棄される)。`HasPendingServerStartup` で未受け取りかどうかを見られる
- 事前起動を使わないアプリは何も変わらない

`IsInitialized` は COM 参照の有無で、本体との通信や画面が開いていることの保証ではない。定期監視では `IsSessionClosed()` を確認し、閉じられた場合も `ShutdownAsync` を通す。`HasPendingShutdown` の間は旧プロセス終了が未確認。`Release()` は初期化・接続失敗時などの参照解放用で、停止・終了成功を保証しない。

状態はインスタンスごとに保持するが、メーカー COM サーバーが複数インスタンスを独立して提供する保証は追加していない。同じ LOGGER を複数セッションや別アプリから同時制御する用途は対象外。Windows 画面操作の対象が複数なら、一意に確認できず失敗する場合がある。

診断区間を既存ログへ渡す場合は、作成時に `ObserveOperation = name => ...` を指定する(AS110 系は `PcDelayObservation.Begin` を注入していた)。コールバックは制御を妨げない実装にする。

## 測定開始の 3 つの方法

| メソッド | 動き | 使っていたアプリ |
|---|---|---|
| `StartAsync(log, beforeTrigger)` | X 版は初回だけ Start で準備し、以後はマニュアル Trigger で開始。旧版は毎回 Start。`beforeTrigger` で Trigger 直前のインターロックを確認できる | AS110 系(動き検査) |
| `StartSingleAcquisition()` | トリガなし・1 回収集の雛形用。開始準備をリセットしてから毎回 Start。Trigger は送らない | AS110AllCheck、AS110NoiseTest |
| `Start()` | 旧版・X 版ともメーカーの `Start()` を 1 回だけ呼ぶ。Trigger・状態待ち・再試行・開始準備のリセットをしない | AS100201SwitchController |

`StartSingleAcquisition()` と `Start()` は「開始準備(X 版の初回 Start 済みフラグ)をリセットするか」が違うので、両方残している。

## 終了の扱い(`Lifecycle`)

- 既定の `WaveLoggerLifecycle.ManagedShutdown`: 上の「アプリから使う」の通常終了手順(`ShutdownAsync` で停止・Quit・COM 解放・対象プロセス終了確認)。
- `WaveLoggerLifecycle.ReferenceOnly`: 接続時の追加の状態照会と LOGGER プロセスの追跡を行わない。終了・再確認・Wi-Fi 復帰は `Release()` で COM 参照を解放するだけで、LOGGER の停止・Quit・プロセス終了はしない(AS100201SwitchController の使い方)。開始方法とは独立した選択で、`ReferenceOnly` にしても `StartAsync()` の意味は変わらない。

`LoggerMeasurementState.Refresh()` の `allowManualTriggerWait` は既定 `false`。状態 10 の表示は `DisplayText`(「収集データ読み出し中(本体停止)」)と、`GetDisplayText(false)`(「収集データ読み出し中」)を選べる。

## 保存・Excel 転送を追加するアプリでの境界

このライブラリに Excel 依存はない。`GetDataCount()` / `SaveXdt(path)` / `SaveCsv(path)` は、取得したドキュメント参照を `finally` で解放し、メーカー API の戻り値や例外を呼び出し元へ返す。保存先・製品番号・上書き確認・出力内容の検証・再試行・二重転記防止は持たない。

保存を行うアプリ側に「測定結果の保存処理」を置き、対象測定・アクティブファイル・停止状態を確定し、保存完了と出力を確認した後で、別の「Excel 出力処理」へ保存済みデータを渡す。保存と終了を並行させず、保存失敗時の次回測定や終了の扱いもアプリで決める。

## 確認

`Wrappers/Tests/WaveLogger.Automation.Tests` は実機を操作せず、終了順序・終了失敗時の保持・プロセス隔離・セッション状態分離・測定開始(3 方式)・参照読み込みの停止通知・既存保存 API のエラー伝播・`Lifecycle` の既定値・状態 10 の表示・事前起動(参照の受け渡しが 1 回だけ成功すること、破棄・失敗時に止まらないこと。Excel がある PC では別プロセスの COM サーバーを受け取った後に呼べること)を確認する。COM 参照の実際の解放、画面操作、本体との通信は実機での確認が必要。
