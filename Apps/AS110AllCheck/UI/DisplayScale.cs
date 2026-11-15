namespace PSCAutomation.Apps.AS110AllCheck;

// Windows の表示倍率(設定 → ディスプレイ → 拡大/縮小)に、画面の大きさ・位置を合わせる(2026-11-14、全体見直し・区切り 6、人の決定)。
// 文字は倍率に合わせて大きくなるのに、コードで決めた大きさ・位置(ピクセル)は 100% のままだったので、125%・150% では
// 文字が欄からはみ出したり、ボタンと重なったりするおそれがあった。倍率は Windows から取る(人が設定する所は無い)。
// 倍率は、アプリを起動したときのメインのディスプレイのもの(起動中に倍率を変えたとき・倍率の違う別のディスプレイへ動かしたときは、
// Windows が画面全体を引き伸ばす)。
internal static class DisplayScale
{
    /// <summary>
    /// 画面の部品を全部足して、大きさ・位置を 100%(96 dpi)の値で決め終わってから呼ぶ。その場で画面と中の部品を倍率ぶん拡大する
    /// (文字はもとから倍率に合っているので変えない)。呼んだ後に足した部品は拡大されない。
    /// </summary>
    public static void Apply(ContainerControl window)
    {
        // 先に方式を決める(方式を変えると基準の値が消えるため)。基準の値を入れた時点で拡大される。
        window.AutoScaleMode = AutoScaleMode.Dpi;
        window.AutoScaleDimensions = new SizeF(96F, 96F);
    }

    /// <summary>100% のときのピクセル数を、いまの倍率のピクセル数にする(Apply の後で大きさを決め直すとき)。</summary>
    public static int Pixels(Control control, int at100Percent) => (int)Math.Round(at100Percent * control.DeviceDpi / 96.0);
}
