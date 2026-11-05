AS110 MainForm 構造整理版（2026-10-02）

適用方法
1. 今使っているMainForm.csを、プロジェクトの外へバックアップしてください。
   プロジェクト内にMainForm_backup.csなどの名前で残すと重複定義になります。
2. ZIP内のMainForm.csで既存のMainForm.csを置き換えてください。
3. ZIP内の残り7個の.csファイルを、MainForm.csと同じフォルダへ追加してください。
4. Visual Studioでビルドしてください。添付された.csprojの変更は不要です。
   通常はフォルダへ追加するだけで取り込まれます。

変更するファイル
MainForm.cs：起動、機器の所有、終了処理。
MainForm.Layout.cs：GUI作成、画面表示の更新。
MainForm.Connections.cs：機器選択、接続確認、定期監視、Wi-Fi復帰。
MainForm.Files.cs：雛形操作、参照波形の候補一覧と読み込み。
MainForm.Measurement.cs：Arduinoイベント、通電確認、測定開始・終了。
MainForm.Operations.cs：共通の操作処理、エラー処理。
LoggerMeasurementState.cs：LOGGER状態、測定中判定、開始直後の状態取得待ち。
ErrorNotification.cs：最前面の警告・エラーポップアップ。

MainForm.*.csの6ファイルはpartialで同じMainFormクラスを構成します。
別々の画面を作るものではありません。共通の状態は従来どおり同じ画面内で保持します。
今回、すべてを独立サービスへ作り直すことはせず、実機確認済みの手順を維持して役割ごとに分割しました。
測定状態とポップアップは、画面から独立したクラスへ移しました。

変更しないファイル
ArduinoConnection.cs、IFunctionGeneratorSession.cs、Wf1973Session.cs、
Sdg1032XSession.cs、WaveLoggerControl_Dual.cs、NrDeviceConnections.cs、
SingleAdapterWifiManager.cs、LoggerFileDrop.cs、LoggerReferenceLoader.cs、
Program.cs、AS110BoltCheck.csprojは、現在のものをそのまま使います。

維持する動作
・Arduino確認と発振器／LOGGER起動の並行実行。
・起動時の発振器出力OFF、0.2 Hz／10.2 Vp-p／オフセット0 V／三角波／1周期。
・開始位相や負荷など、各発振器で本体設定を使う現在の仕様。
・LOGGER開始成功後に発振器へトリガーを送信。
・非通電時の測定禁止と最前面ポップアップ。
・雛形の自動読み込み、参照波形の手動選択、LOGGER再接続。
・LOGGERの状態10／12では再開始しない。状態1／11で開始可能。
・開始直後500 msは古い停止情報を使わない。
・終了時の機器解放とWi-Fi復帰。

次段階の機能追加場所
自動保存：MainForm.Measurement.csのOnMeasurementCompleted()。
保存先設定、GUI手入力のシリアル番号、雛形名のシリアル部分の置換、
同名ファイル時の保存中止と次回測定禁止は、自動保存の実装時に追加します。
終了の検出は現在どおりGetState()の定期確認で行います。
外部LEDへの異常通知：ErrorNotification.cs。
今回追加したのは追加場所の明記までで、自動保存・LED送信・保存異常の解除は実装していません。
発振器：MainFormは既存のIFunctionGeneratorSession経由で操作し、
機種固有のコマンドは各Sessionが担当します。今回、コマンドは変更していません。

検証
移動した43個のメソッド本体の一致、メンバーの移動漏れ・重複、
ポップアップ呼び出しの移動、定期監視処理の一致を静的比較で確認しました。
測定状態の抽出箇所は元の条件と照合しました。
作成環境に.NET SDK／NI-VISAがないため、ビルドと実機確認は未実施です。
Windows側で、接続確認→参照波形選択→非通電時のボタン操作→通電時の測定→
測定終了後の再測定→LOGGERを閉じて確認ボタンで復帰→アプリ終了を確認してください。
