namespace PSCAutomation.Apps.AS110AllCheck;

// 返ってこないかもしれない機器の呼び出し(VISA の接続など)を、画面とは別のスレッドで試し、決まった時間で見切る部品(2026-11-25)。
//
// 2026-11-25、WF1973 の USB 通信が止まり、接続確認の FunctionGeneratorConnection.Connect(VISA の Open)が返ってこず、
// 画面ごと「応答なし」になった(起動しても立ち上がらないように見えた)。VISA の Open にはタイムアウトが効かない。
// そこで先に別のスレッドで同じ接続を試し、時間内に返ってきたときだけ、画面のスレッドで今までどおりつなぐ。
//
// 試しのスレッドは止められない(ドライバーの中で止まっているため。機器の電源を入れ直せば返る)。
// 見切ったあとも動き続けるので、終わるまでは新しい試しを重ねない(StillRunning を返す)。アプリの終了は妨げない(背景スレッド)。
// VISA の COM は画面と同じ STA で使っているので、試しのスレッドも STA にする。
internal sealed class BlockingCallProbe
{
    internal enum Outcome { Completed, TimedOut, StillRunning }

    private Task? _running;

    // 前の試しがまだ返っていないか。
    public bool IsRunning => _running is { IsCompleted: false };

    // action を別のスレッドで動かし、timeout まで待つ。action の例外はここでは扱わない(Completed として返す。
    // このあと画面のスレッドで同じ処理をして、同じ失敗を今までどおり知らせる)。
    public async Task<Outcome> RunAsync(Action action, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsRunning) return Outcome.StillRunning;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception) { }
            finally { completion.TrySetResult(); }
        })
        {
            IsBackground = true,
            Name = "AS110AllCheck 機器の応答確認",
        };
        thread.SetApartmentState(ApartmentState.STA);
        _running = completion.Task;
        thread.Start();
        var finished = await Task.WhenAny(completion.Task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        return finished == completion.Task ? Outcome.Completed : Outcome.TimedOut;
    }
}
