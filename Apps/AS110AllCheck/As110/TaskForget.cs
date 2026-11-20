using System.Runtime.ExceptionServices;

namespace PSCAutomation.Apps.AS110AllCheck;

// 終わりを待たずに始める処理の失敗を、取りこぼさないための部品(2026-11-20。アナライザーの警告 VSTHRD100・VSTHRD101 の直し)。
//
// ボタン・タイマー・画面の OnShown などは戻り値を返せないので、そこから始めた待ちのある処理(async)は、誰も終わりを待たない。
// それまでは「async void」で書いていた。async void は、中で受け止めなかった例外を呼び出し元へ返せず、
// 画面のスレッドなら想定外のエラーの受け皿(UnexpectedErrors)へ、それ以外のスレッドならアプリの終了へ進む。
// アナライザーは async void を警告するので、処理は Task を返す形(…Async)で書き、始める所で Forget() を付ける。
//
// Forget() は、処理が失敗したときに async void と同じ道すじで例外を出し直す
// (始めたスレッドに画面の処理の順番待ちがあればそこへ、無ければスレッドプールへ)。
// だから、失敗したときの動き(日本語の「想定外のエラー」の知らせ・出力 OFF・ログ)は async void のときと変わらない。
internal static class TaskForgetExtensions
{
    public static void Forget(this Task task)
    {
        if (task.IsCompletedSuccessfully) return;
        _ = RethrowFailureAsync(task, SynchronizationContext.Current);
    }

    private static async Task RethrowFailureAsync(Task task, SynchronizationContext? context)
    {
        // この部品の中の 2 か所だけ、アナライザーの警告を止める(ほかの場所では止めない)。
        // VSTHRD003(よそで始めた Task を待たない): ここは失敗を見届けるためだけに待つ。画面のスレッドに戻らない待ち方
        //   (ConfigureAwait(false))で、だれもこの待ちの終わりを止めて待たないので、固まることはない。
        // VSTHRD001(Post を使わない): 勧められる代わりの JoinableTaskFactory は Visual Studio の拡張機能向けの部品で、このアプリには無い。
        //   ここの Post は async void が中でしているのと同じ「例外を画面のスレッドで出し直す」だけで、終わりを待たない。
#pragma warning disable VSTHRD003, VSTHRD001
        try { await task.ConfigureAwait(false); }
        catch (Exception ex)
        {
            // スタックトレースを保ったまま出し直す。
            var failure = ExceptionDispatchInfo.Capture(ex);
            if (context is not null) context.Post(static state => ((ExceptionDispatchInfo)state!).Throw(), failure);
            else ThreadPool.QueueUserWorkItem(static state => state.Throw(), failure, preferLocal: false);
        }
#pragma warning restore VSTHRD003, VSTHRD001
    }
}
