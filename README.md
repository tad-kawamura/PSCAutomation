# AS110Tools(仮称)

AS110 系のハード連携アプリ(マイコン・発振器・計測器・KEYENCE WAVE LOGGER)と、それらが共通で使う wrapper を 1 つのリポジトリに集約するモノレポ。

- 作業ルール・仕様決めの手順は [AGENTS.md](AGENTS.md)
- 何ができるか(wrapper の機能一覧)は [docs/capabilities.md](docs/capabilities.md)
- 立ち上げの経緯は [docs/引継ぎ_2026-11-04.md](docs/引継ぎ_2026-11-04.md)
- 旧リポジトリの wrapper 比較と統合方針は [docs/wrapper比較.md](docs/wrapper比較.md)

## 目標構成

```
AS110Tools/
├── AGENTS.md / CLAUDE.md
├── docs/capabilities.md   ← wrapper の機能一覧(できること・制約)
├── Wrappers/              ← 共通 wrapper(C# クラスライブラリ)
├── Apps/<各アプリ>/        ← exe はアプリごとに別
├── specs/                 ← 仕様インタビューで作った SPEC.md
├── AS110Tools.sln
└── *.slnf                 ← アプリ別フィルター
```

## 状態(2026-11-04)

- リポジトリの土台(git、AGENTS.md、docs)を作った段階。コードの移植はまだ。
- 移植元 5 リポジトリ(すべて `tad-kawamura` の Private)の比較を終え、統合方針を `docs/wrapper比較.md` に書いた。人の判断待ちの項目は同文書の末尾。
- .NET は全アプリ `net9.0-windows`(SDK 9.0.318 で確認)。
