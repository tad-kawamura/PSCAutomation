namespace PSCAutomation.Apps.AS100201SwitchController;

// 起動時に選ぶモード(SPEC_調整操作の記録.md「モード」)。途中で変えるには起動し直す。
// 通常モードは今までのアプリの動きそのもの。学習モードはタッチパネルで調整操作を記録し、
// 測定のたびに xdt を保存し、CSV 台帳に 1 行 1 出来事を追記する。
internal enum As100201StartupMode
{
    Normal,
    Learning
}
