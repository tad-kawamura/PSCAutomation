using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PSCAutomation.Wrappers.WaveLogger.Automation
{
    // LOGGER 本体ソフト(COM サーバー)の起動を、アプリの画面準備と並行して先に始めておく仕組み。
    //
    // 旧 WAVE LOGGER の起動(Activator.CreateInstance)は 7〜8 秒かかり、UI スレッドで待つと画面が固まる。
    // BeginServerStartup は別の STA スレッドで COM オブジェクトを作り、できた参照を COM のスレッド間
    // マーシャリング(CoMarshalInterThreadInterfaceInStream)で UI スレッドへ渡せる形にしておく。
    // その後 Initialize を呼ぶと、新しく起動せずに渡された参照を受け取って続きを行う(Initialize・画面表示)。
    // 受け取った後の使い方は従来と同じ(UI スレッドで直列に使う)。
    //
    // 事前起動を使わないアプリは、何も変わらない(Initialize は従来どおりその場で起動する)。
    public sealed partial class WaveLoggerSession
    {
        private ServerPrestart? _prestart;

        /// <summary>事前起動を始めていて、まだ Initialize で受け取っていない(または破棄していない)間 true。</summary>
        public bool HasPendingServerStartup => _prestart is not null;

        /// <summary>
        /// LOGGER 本体ソフトの起動だけを別スレッドで先に始める。画面を作る前(フォームのコンストラクターの先頭など)に呼ぶ。
        /// 起動が終わったかは <see cref="WaitForServerStartupAsync"/> で待てる。
        /// 次の <see cref="Initialize(WaveLoggerEdition, Action{string}?)"/> が同じ種類の LOGGER なら、起動済みの参照を受け取る。
        /// 違う種類なら事前起動分は破棄して従来どおり起動する。
        /// 起動に失敗しても例外にせず、Initialize が従来どおり起動を試みる(エラーは従来と同じ場所で出る)。
        /// </summary>
        /// <returns>起動の完了を待つ Task(失敗しても例外にならない)。</returns>
        public Task BeginServerStartup(WaveLoggerEdition edition)
        {
            if (_application is not null)
                throw new InvalidOperationException("WAVE LOGGERは既に初期化されています。");
            if (_prestart is not null)
                throw new InvalidOperationException("LOGGERの事前起動は既に始まっています。");
            if (_pendingProcess is not null)
                return Task.CompletedTask; // 旧プロセスの終了が未確認。Initialize 側で従来どおり扱う。

            Type type;
            WaveLoggerEdition selectedEdition;
            try
            {
                (type, selectedEdition) = ResolveServer(edition);
            }
            catch (InvalidOperationException)
            {
                // COM 未登録。Initialize で同じ理由の例外が出るので、ここでは何もしない。
                return Task.CompletedTask;
            }

            var existingProcesses = Lifecycle == WaveLoggerLifecycle.ManagedShutdown &&
                selectedEdition == WaveLoggerEdition.WaveLoggerX
                ? LoggerProcessLifetime.Snapshot("NRWLX") : null;
            var prestart = new ServerPrestart(type, selectedEdition, existingProcesses);
            _prestart = prestart;
            prestart.Start();
            return prestart.Completion;
        }

        /// <summary>
        /// 事前起動の完了を待つ(失敗しても例外にならない)。事前起動していなければすぐ戻る。
        /// Initialize の前に await しておくと、残りの起動待ちで UI スレッドを止めずに済む。
        /// </summary>
        public Task WaitForServerStartupAsync() => _prestart?.Completion ?? Task.CompletedTask;

        /// <summary>
        /// 受け取っていない事前起動分を破棄する(起動した LOGGER の参照を解放する)。
        /// Initialize せずにアプリを終えるときに呼ぶ。事前起動していなければ何もしない。
        /// </summary>
        public void CancelServerStartup()
        {
            ServerPrestart? prestart = _prestart;
            _prestart = null;
            prestart?.Abandon();
        }

        // InitializeCore から呼ぶ。事前起動分があり、同じ種類なら受け取って true。
        // 受け取れなかった(違う種類・起動失敗)ときは破棄して false(従来どおり起動する)。
        private bool TryTakePrestartedServer(WaveLoggerEdition selectedEdition, Action<string>? timingLog,
            out object? instance, out HashSet<int>? existingProcesses)
        {
            instance = null;
            existingProcesses = null;
            ServerPrestart? prestart = _prestart;
            if (prestart is null) return false;
            _prestart = null;

            if (prestart.Edition != selectedEdition)
            {
                prestart.Abandon();
                timingLog?.Invoke(
                    $"LOGGER内訳：事前起動した{GetEditionDisplayName(prestart.Edition)}は使いません（選択={GetEditionDisplayName(selectedEdition)}）");
                return false;
            }

            var stepTimer = Stopwatch.StartNew();
            using (ObserveOperation?.Invoke("LOGGER.Prestart.Take"))
            {
                if (!prestart.TryTake(out instance, out string? failure))
                {
                    timingLog?.Invoke($"LOGGER内訳：事前起動を使えません（{failure}）。この場で起動します。");
                    return false;
                }
            }

            existingProcesses = prestart.ExistingProcesses;
            timingLog?.Invoke(
                $"LOGGER内訳：COMオブジェクト生成（事前起動を使用。起動開始から {prestart.Elapsed.TotalSeconds:F3} 秒、受け取り {stepTimer.Elapsed.TotalSeconds:F3} 秒）");
            return true;
        }

        // 1 回分の事前起動。専用の STA スレッドで COM オブジェクトを作り、UI スレッドへ渡す参照を用意する。
        // 渡し終える(または破棄される)までスレッドを生かし、最後に自分の参照を解放する。
        private sealed class ServerPrestart
        {
            private static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");

            private readonly Type _type;
            private readonly TaskCompletionSource<bool> _completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            // STA スレッドの待ちはカーネルイベントの WaitOne にする(CLR が待ちの間に COM のメッセージを処理する)。
            private readonly ManualResetEvent _handoff = new(false);
            private readonly object _gate = new();
            private readonly Stopwatch _timer = new();
            private IntPtr _stream;
            private bool _taken;
            private bool _abandoned;

            public ServerPrestart(Type type, WaveLoggerEdition edition, HashSet<int>? existingProcesses)
            {
                _type = type;
                Edition = edition;
                ExistingProcesses = existingProcesses;
            }

            public WaveLoggerEdition Edition { get; }
            public HashSet<int>? ExistingProcesses { get; }
            public Task Completion => _completion.Task;
            public TimeSpan Elapsed { get; private set; }
            public Exception? Error { get; private set; }

            public void Start()
            {
                _timer.Start();
                var thread = new Thread(Run) { IsBackground = true, Name = "WaveLogger.Prestart" };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }

            private void Run()
            {
                object? instance = null;
                try
                {
                    instance = Activator.CreateInstance(_type)
                        ?? throw new InvalidOperationException("WAVE LOGGERのCOMオブジェクトを生成できませんでした。");
                    IntPtr unknown = Marshal.GetIUnknownForObject(instance);
                    try
                    {
                        Guid iid = IidIUnknown;
                        Marshal.ThrowExceptionForHR(
                            CoMarshalInterThreadInterfaceInStream(ref iid, unknown, out IntPtr stream));
                        lock (_gate) _stream = stream;
                    }
                    finally
                    {
                        Marshal.Release(unknown);
                    }
                    Elapsed = _timer.Elapsed;
                    _completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    Error = ex;
                    Elapsed = _timer.Elapsed;
                    _completion.TrySetResult(false);
                }

                // 受け取り(または破棄)まで、この STA を生かしておく。
                _handoff.WaitOne();

                IntPtr leftover;
                lock (_gate)
                {
                    leftover = _taken ? IntPtr.Zero : _stream;
                    _stream = IntPtr.Zero;
                }
                if (leftover != IntPtr.Zero)
                {
                    // 誰も受け取らなかった参照は、このスレッドで受け取り直してから解放する。
                    Guid iid = IidIUnknown;
                    if (CoGetInterfaceAndReleaseStream(leftover, ref iid, out IntPtr pointer) >= 0)
                        Marshal.Release(pointer);
                }
                if (instance is not null && Marshal.IsComObject(instance))
                    Marshal.FinalReleaseComObject(instance);
            }

            // Initialize を呼ぶスレッド(UI スレッド)で呼ぶ。起動が終わるまで待つ(事前に Completion を await していれば即戻る)。
            public bool TryTake(out object? instance, out string? failure)
            {
                instance = null;
                failure = null;
                _completion.Task.Wait();
                lock (_gate)
                {
                    if (_abandoned) { failure = "破棄済み"; return false; }
                    if (Error is not null)
                    {
                        failure = Error.Message;
                        _handoff.Set();
                        return false;
                    }
                    IntPtr stream = _stream;
                    _stream = IntPtr.Zero;
                    Guid iid = IidIUnknown;
                    int hr = CoGetInterfaceAndReleaseStream(stream, ref iid, out IntPtr pointer);
                    if (hr < 0)
                    {
                        failure = $"参照の受け取りに失敗 0x{hr:X8}";
                        _handoff.Set();
                        return false;
                    }
                    try
                    {
                        instance = Marshal.GetObjectForIUnknown(pointer);
                    }
                    finally
                    {
                        Marshal.Release(pointer);
                    }
                    _taken = true;
                    _handoff.Set();
                    return true;
                }
            }

            public void Abandon()
            {
                lock (_gate) _abandoned = true;
                _handoff.Set();
            }

            [DllImport("ole32.dll")]
            private static extern int CoMarshalInterThreadInterfaceInStream(ref Guid riid, IntPtr pUnk, out IntPtr ppStm);

            [DllImport("ole32.dll")]
            private static extern int CoGetInterfaceAndReleaseStream(IntPtr pStm, ref Guid riid, out IntPtr ppv);
        }
    }
}
