AS110 共通処理・専用処理の分割版

■ 既存ファイルを置き換える（7個）
ArduinoConnection.cs
IFunctionGeneratorSession.cs
Wf1973Session.cs
Sdg1032XSession.cs
MainForm.cs
MainForm.Connections.cs
MainForm.Measurement.cs

■ 新しくプロジェクトへ追加する（3個）
GeneratorBurstSettings.cs
As110MotionTest.cs
As110ArduinoProtocol.cs

ZIP内の.csをプロジェクトの同名ファイルと差し替え、新規3ファイルも同じ場所へ配置してください。
自動で認識されない場合はVisual Studioの「追加」→「既存の項目」で追加してください。
古い.csのバックアップはプロジェクトの外へ置いてください（同じクラスの二重登録を避けるため）。
他の.cs、.resx、.csproj、Arduinoの.inoは今回変更不要です。
namespaceはすべてAS110BoltCheckです。
今回の追加で.csの数は18個から21個になります。

■ 発振器の構成
As110MotionTest.cs：AS110動き検査の設定値を1か所に定義する。
GeneratorBurstSettings.cs：機種共通の設定データと基本的な値の確認を担当する。
IFunctionGeneratorSession.cs：接続と機種共通の操作を定義する。
Wf1973Session.cs／Sdg1032XSession.cs：設定を各機種のコマンドへ変換する。

MainFormからの準備は As110MotionTest.Prepare(_generator) と書きます。
その中で generator.PrepareBurst(Settings) を呼び出します。
プロジェクト固有の名前はAs110MotionTest、機種共通の操作名はPrepareBurstです。

AS110の条件：三角波／0.2Hz／10.2Vp-p／オフセット0V／1周期。
開始位相の指定はnull（本体設定を使用）、負荷も本体設定を使用します。
設定を変えるときはAs110MotionTest.csのSettingsを修正してください。
GUIに出す測定開始ログもこの設定から作るため、別途数値の修正は不要です。
同じ設定の準備は繰り返さず、初回トリガー時だけ出力ONにします。

別プロジェクトでは、そのプロジェクト専用の設定クラスを追加して共通のSessionへ渡します。
現在の設定データが扱うのは三角波のバースト出力です。
別の波形や連続出力を使う場合は、対応する共通操作と両機種の実装を追加します。
本体の設定範囲による制約もあるため、値を変えた後は実機で確認してください。

■ Arduinoの構成
ArduinoConnection.cs：COMポートの検索・接続・行単位の送受信・切断通知。
As110ArduinoProtocol.cs：AS110専用の接続確認、受信文字列の解釈、通電状態の管理。
MainFormはAs110ArduinoProtocolを使用します。
送信はArduinoConnection.SendLineで行えます（今後の専用クラスからも利用可能）。
通信文字列と115200baud、起動待ち1.7秒、通電情報の鮮度1秒は現行のままです。
Arduino側の書き換えは不要です。

■ 確認内容と実機確認
現在の設定から作る両機種の設定コマンド列を元コードと照合し、一致を確認しました。
トリガー処理、準備の呼び出し箇所、Arduinoの通信文字列も照合しました。
この環境には.NETのコンパイラと実機がないため、ビルド・実機試験は未実施です。

Visual Studioでリビルド後、以下を確認してください。
1. 起動時に各機器へ接続でき、発振器が出力OFFのまま待機する。
2. NF弁通電中にボタンを離すと、LOGGERと発振器が動作する。
3. 測定終了後、2回目以降も開始できる。
4. 非通電時は測定せず、最前面にポップアップが表示される。
5. 使用する発振器の設定値が従来と同じである。

自動保存と外部LEDの異常通知は、今回も追加していません。
