# LOGGER wrapper移植記録（2026-10-28）

移植元は [AS110BoltCheck](https://github.com/tad-kawamura/AS110BoltCheck)、確認したHEADは `58746ce`。共通ライブラリは `3494a1d` で分離された `Libraries/WaveLogger.Automation` の一式を変更せず取り込んだ。

## 反映した処理

- `072c32f` の手動終了検知と停止・再接続、`f1860f1` の制御対象プロセス終了確認を含む。NR-Xの×終了で機器IDが255になった場合、接続済みIDを復元し、本体応答・Stop・停止状態・Quit・ファイル数と可視状態・COM解放・対象PIDの終了を順に確認する。失敗時は参照または終了待ち状態を保持し、再接続を止める。
- 接続のやり直し、Wi-Fi復帰、アプリ終了時に `ShutdownAsync` を待つ。漏れチェック中の終了は出力解除後に進む。
- LOGGER Xの初回Start後にトリガ待ちを確認し、Triggerを送る。以降もTriggerを送る。準備待ち後に接続・Arduino通信・動き検査の通電条件を再確認する。
- LOGGER Xのトリガ待ちから参照波形を変更するときは停止を待つ。雛形リセット時は開始準備・状態監視もリセットする。雛形読み込み前後の診断も取り込んだ。
- 保存・CSV・データ数取得は共通wrapperを呼び、取得したドキュメントのCOM参照をfinallyで解放する。
- AS110AllCheckで測定中にLOGGERや雛形が閉じられた場合は発振器出力を解除して測定を無効化する。一時保存済み・正式保存済みの工程は保持する。初回Startの準備待ち失敗時もStopを試み、次回の開始準備をリセットする。

工程管理、保存先・S/N保護、点数検証、Excel転送、正式保存、漏れチェックはAS110AllCheck側が引き続き管理する。移植元の動き検査専用画面・Arduino・発振器設定・雛形ファイルの変更は今回のwrapper移植に含めない。移植時点ではNR-X用YDTは別途配置が必要だったが、現在はAS110BoltCheck由来のYDTを同梱している。

## このPCでの確認

- .NET SDK 10.0.401でReleaseビルド成功（警告0・エラー0）。アプリとwrapperの両方をReleaseで生成。
- `Tests/Verification` の工程管理・完了番号保護・点数圧縮・NR-X CSV変換の16項目成功。NuGetの脆弱性情報を取得できないNU1900警告あり。
- このPCには.NET 9ランタイムがないため、上記検証は実行時だけ `DOTNET_ROLL_FORWARD=Major` を設定し.NET 10で実行。プロジェクトのターゲットは `net9.0-windows`（既存の工程検証は `net9.0`）を維持。
- `Tests/LoggerShutdown` も移植したが、起動時にWindowsアプリケーション制御ポリシーでDLLがブロックされた（`0x800711C7`）。このPCでのLOGGER回帰試験は未実施。セキュリティ設定は変更していない。
- 実機・メーカーCOM・画面操作・保存データの実測確認は未実施。

回帰試験のコマンド（.NET 9実行環境のあるPC）:

```powershell
dotnet build AS110AllCheck.sln -c Release
dotnet run --project Tests/LoggerShutdown/LoggerShutdownTests.csproj -c Release -- "$PWD/bin/Release/net9.0-windows/AS110AllCheck.dll"
dotnet run --project Tests/Verification/Verification.csproj -c Release
```

実機ではNR-X USBで測定→トリガ待ち→LOGGERの×終了→「確認」→雛形・参照再読込→再測定を確認する。検査アプリ終了後のNRWLXプロセス消滅、測定中の終了、一時保存・Excel転送・正式保存・漏れチェック、Wi-Fi復帰、NR-500と旧LOGGER、機器切断時の終了失敗と再試行も未確認項目として残る。
