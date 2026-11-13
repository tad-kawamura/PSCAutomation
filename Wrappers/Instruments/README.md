# Instruments(VISA 機器・RS-232C の計測器)

発振器(NF WF1973、SIGLENT SDG1032X)と DMM(ADCMT 7351E)を VISA(NI-VISA の COM)で操作する箱。2026-11-13 に GC16 精密デジタル圧力計(RS-232C、`Gauges/Gc16Session`)を足した。namespace は `PSCAutomation.Wrappers.Instruments`、`net9.0-windows`。
できること・制約の一覧は `docs/capabilities.md`、移植の出どころと変更点は `docs/移行記録.md`「手順 4」。

## 構成

```
Visa/        通信部分(探索・送受信・切断)
  IInstrumentSession      機器クラスが受け取る通信の interface(模擬に差し替えられる)
  VisaInstrumentSession   NI-VISA での実装と探索(Connect)
  VisaSearchOptions       探し方(パターン・リソース指定・USB 限定・失敗時の詳細)
Generators/  発振器
Meters/      DMM
Gauges/      GC16 精密デジタル圧力計(RS-232C。COM ポートを問い合わせで見分ける。2026-11-13 追加、実機未確認)
docs/        7351E の実機確認メモ(元 AS110NoiseTest)、WF1973 の実機確認(移植後)
```

実機での確認は `Wrappers/Tests/Instruments.HardwareCheck`(WF1973 のバースト・連続を出しながら NR-500 で記録して判定する)。結果は `docs/WF1973実機確認.md`。

## WF1973 のクラスの選び方

WF1973 は使い方ごとに 3 つのクラスがある(判断 4、2026-11-04。同じ機器でも、接続時の初期化と状態の持ち方が違うため)。1 台の WF1973 を同時に 2 つのクラスで開かない。

| やりたいこと | クラス | 接続 | 元 |
|---|---|---|---|
| 三角波を、トリガー 1 回ごとに指定の周期数だけ出す(外部トリガー)。WF1973 と SDG1032X のどちらでも動かしたい | `IFunctionGeneratorSession`(中身は `Wf1973BurstSession` か `Sdg1032XSession`) | `FunctionGeneratorConnection.Connect()`(USB の WF1973 / SDG1032X を自動判別) | AS110AllCheck・AS110BoltCheck の `Wf1973Session` / `Sdg1032XSession` |
| 振幅・周波数・位相を決めて連続で出し、位相をそろえて出し直す | `Wf1973ContinuousSession` | `Wf1973ContinuousSession.Connect(GeneratorWaveform.Triangle)`(接続時に形も送る。`Connect()` は本体に残っている形のまま) | AS100201SwitchController の `Wf1973Session` |
| DC のオフセット電圧を出し、出力を切らずに電圧だけ変える(電流指令など) | `Wf1973DcSource` | `Wf1973DcSource.Connect()`(USB の VID/PID で探す) | AS110NoiseTest の `Wf1973DcSource` |

7351E は `Adcmt7351ESession.Connect()` → `ApplySettings(new Adcmt7351ESettings())` → `ReadMilliamps()`。

GC16 は `Gc16Session.Find(new Gc16SearchOptions { PreferredPort = 前回の番号, ExcludedPorts = [マイコンの COM] })` → `EnterRemote()` → `ReadPressureKilopascals()` / `ZeroAdjust()` → `Dispose()`(RM,0 に戻して閉じる)。シリアル通信は待ちがあるので、画面のあるアプリでは別のスレッドで呼ぶ。

## アプリから使う

```xml
<ProjectReference Include="..\..\Wrappers\Instruments\Instruments.csproj" />
```

```csharp
using PSCAutomation.Wrappers.Instruments;

using var generator = FunctionGeneratorConnection.Connect();   // 見つからなければ InvalidOperationException
generator.PrepareBurst(new GeneratorBurstSettings(GeneratorWaveform.Triangle, 1.0, 2.0, 0, 1));
generator.TriggerBurst();
```

- 値(周波数・振幅・電流の上限など)はアプリが渡す。この箱は製品の既定値を持たない
- 機器なしで動かすときは、アプリ側の薄い interface(例: AS110NoiseTest の `IOffsetVoltageSource` / `ICurrentMeter`)で包んで模擬に差し替える。機器クラスの単体の確認なら、`IInstrumentSession` の模擬を `new Wf1973BurstSession(fake)` のように渡す(`Wrappers/Tests/Instruments.Tests` の `FakeInstrument`)
- NI-VISA が入っていない PC ではビルドできない(`Ivi.Visa.Interop.dll` が見つからないとエラー)。この箱を参照しないアプリには影響しない
