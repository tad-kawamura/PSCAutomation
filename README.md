# PSCAutomation

PSC 社内の自動化プログラム(人が手でやっていた機器操作・画面操作・記録を、ボタン 1 つでできるようにするアプリ)と、それらが共通で使う wrapper を 1 つのリポジトリに集約するモノレポ。最初の対象は AS110 / AS100201 系のハード連携アプリ(マイコン・発振器・計測器・KEYENCE WAVE LOGGER)。

- 作業ルール・仕様決めの手順は [AGENTS.md](AGENTS.md)
- 何ができるか(wrapper の機能一覧)は [docs/capabilities.md](docs/capabilities.md)
- 立ち上げの経緯は [docs/引継ぎ_2026-11-04.md](docs/引継ぎ_2026-11-04.md)
- 旧リポジトリの wrapper 比較と統合方針は [docs/wrapper比較.md](docs/wrapper比較.md)

## 目標構成

```
PSCAutomation/
├── AGENTS.md / CLAUDE.md
├── docs/capabilities.md   ← wrapper の機能一覧(できること・制約)
├── Wrappers/              ← 共通 wrapper(C# クラスライブラリ)
├── Apps/<各アプリ>/        ← exe はアプリごとに別
├── specs/                 ← 仕様インタビューで作った SPEC.md
├── PSCAutomation.sln
└── *.slnf                 ← アプリ別フィルター
```

## 状態(2026-11-04)

- 共通 wrapper(WaveLogger.Automation・LoggerFiles・Common(NR-X 接続補助を含む)・Excel(xlsx 直接書き込みと Excel を起動する操作)・Arduino・Instruments(VISA 機器))はすべて `Wrappers/` へ移した(`docs/wrapper比較.md` の手順 2〜4)。出どころと変更点は `docs/移行記録.md`、箱の一覧とテストの実行方法は `Wrappers/README.md`。
- アプリは XdtConversion(`Apps/XdtConversion`、`XdtConversion.slnf`。[README](Apps/XdtConversion/README.md))と AS110NoiseTest(`Apps/AS110NoiseTest`、`AS110NoiseTest.slnf`。[README](Apps/AS110NoiseTest/README.md))を移した。AS110AllCheck・AS110BoltCheck・AS100201SwitchController はまだ。
- 全体 `PSCAutomation.sln` のビルド、`Wrappers/Tests` の 6 つのテスト、XdtConversion の検証、AS110NoiseTest の機器なしの確認ツールは成功(2026-11-04)。機器を使う動作は、2026-11-04 に移植後のコードで AS110NoiseTest の通し(WF1973・7351E・NR-500・旧 WAVE LOGGER・Excel)と WF1973 の 3 クラス(DC・バースト・連続)を実機で確認した([AS110NoiseTest の README](Apps/AS110NoiseTest/README.md)、`Wrappers/Instruments/docs/WF1973実機確認.md`)。WAVE LOGGER X・NR-X・Wi-Fi 切替・Arduino・SDG1032X は未確認。
- 移植元 5 リポジトリ(すべて `tad-kawamura` の Private)の比較を終え、統合方針を `docs/wrapper比較.md` に書いた。人の判断待ちの項目は同文書の末尾。
- リポジトリ名は `PSCAutomation`、Wrappers は依存ごとに 6 つの csproj に分けると決めた(`docs/wrapper比較.md`)。作業フォルダ名は `MergeProject` から `PSCAutomation` に変更済み、GitHub リポジトリ(`tad-kawamura/PSCAutomation`、Private)も作成済み。
- .NET は全アプリ `net9.0-windows`(SDK 9.0.318 で確認)。
