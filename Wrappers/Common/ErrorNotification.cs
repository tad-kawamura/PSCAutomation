using System.Runtime.InteropServices;

namespace PSCAutomation.Wrappers.Common;

// 警告・エラーのポップアップ表示を担当する。
// LOGGERの後ろに隠れないよう、ポップアップを最前面に表示する。
// 各処理から共通の方法で呼び出して使用する。
// 将来の外部LEDによる異常通知も、このファイルへ追加する予定。
// 外部LEDの異常通知は、このクラスへ追加する。現段階では送信しない。
public static class ErrorNotification
{
    // 警告・エラーはLOGGERの後ろに隠れないよう、ポップアップ自体を最前面にする。
    private const uint MbSetForeground = 0x00010000;
    private const uint MbTopmost = 0x00040000;

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode,
        ExactSpelling = true)]
    private static extern int NativeMessageBox(
        IntPtr ownerHandle, string text, string caption, uint type);

    public static DialogResult Show(
        IWin32Window owner, string text, string caption,
        MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        uint flags = (uint)buttons | (uint)icon | MbSetForeground | MbTopmost;
        return (DialogResult)NativeMessageBox(owner.Handle, text, caption, flags);
    }


}
