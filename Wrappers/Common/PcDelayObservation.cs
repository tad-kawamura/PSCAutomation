using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace PSCAutomation.Wrappers.Common;

// 観測結果は制御に使わない。受信側での追加ログ書き込みも行わない。
// 遅い区間だけを上限付きキューに入れ、専用スレッドで既存ログへ渡す。
// 元: AS110AllCheck 他 Common/PcDelayObservation.cs。元は ArduinoDiagnosticLog.Write へ直接書いていたので、
// wrapper では書き込み先を Start(write) で受け取る(例: PcDelayObservation.Start(ArduinoDiagnosticLog.Write))。
public static class PcDelayObservation
{
    private static readonly ConcurrentDictionary<long, Scope> Active = new();
    private static readonly ConcurrentQueue<(string Kind, string Detail)> Reports = new();
    private static long _sequence;
    private static int _queued, _dropped;
    private static long _uiTick, _poolTick;

    public sealed class Scope : IDisposable
    {
        internal readonly long Id, Started;
        internal readonly int ThreadId;
        internal readonly string Name;
        private int _disposed;

        internal Scope(long id, string name)
        {
            Id = id;
            Name = name;
            Started = Stopwatch.GetTimestamp();
            ThreadId = Environment.CurrentManagedThreadId;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Active.TryRemove(Id, out _);
            long ended = Stopwatch.GetTimestamp();
            double ms = Stopwatch.GetElapsedTime(Started, ended).TotalMilliseconds;
            if (ms >= 100)
                Report("OBS_SLOW", $"区間={Name} / thread={ThreadId} / elapsed={ms:F1}ms / startTick={Started} / endTick={ended}");
        }
    }

    public static Scope Begin(string name)
    {
        var scope = new Scope(Interlocked.Increment(ref _sequence), name);
        Active.TryAdd(scope.Id, scope);
        return scope;
    }

    public static void Report(string kind, string detail)
    {
        if (Interlocked.Increment(ref _queued) > 256)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _dropped);
            return;
        }
        Reports.Enqueue((kind, $"observedTick={Stopwatch.GetTimestamp()} / {detail}"));
    }

    public static void UiTick()
    {
        long now = Stopwatch.GetTimestamp();
        long previous = Interlocked.Exchange(ref _uiTick, now);
        if (previous != 0)
        {
            double ms = Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds;
            if (ms >= 1000) Report("OBS_UI_DELAY", $"500ms GUI観測タイマーの実行間隔={ms:F1}ms");
        }
    }

    private static void PoolTick(object? state)
    {
        long now = Stopwatch.GetTimestamp();
        long previous = Interlocked.Exchange(ref _poolTick, now);
        if (previous != 0)
        {
            double ms = Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds;
            if (ms >= 1000) Report("OBS_POOL_DELAY", $"500ms ThreadPool観測タイマーの実行間隔={ms:F1}ms");
        }
    }

    private static double AgeMs(long tick) => tick == 0 ? -1 :
        Stopwatch.GetElapsedTime(tick).TotalMilliseconds;

    /// <summary>
    /// 観測を始める。write(種別, 内容) は専用スレッドから呼ばれる。例外は観測停止として扱い、呼び出し元へ戻さない。
    /// </summary>
    public static IDisposable Start(Action<string, string> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        var monitor = new Monitor(write);
        monitor.Start();
        return monitor;
    }

    private sealed class Monitor : IDisposable
    {
        private readonly ManualResetEvent _stop = new(false);
        private readonly System.Threading.Timer _pool = new(PoolTick, null, Timeout.Infinite, Timeout.Infinite);
        private readonly Thread _thread;
        private readonly Action<string, string> _write;
        private int _disposed;

        public Monitor(Action<string, string> write)
        {
            _write = write;
            _thread = new Thread(Run) { IsBackground = true, Name = "PC delay observer" };
        }

        public void Start()
        {
            Interlocked.Exchange(ref _uiTick, Stopwatch.GetTimestamp());
            Interlocked.Exchange(ref _poolTick, Stopwatch.GetTimestamp());
            _pool.Change(500, 500);
            _thread.Start();
        }

        private void Run()
        {
            try
            {
                _write("OBS_START",
                    $"観測版=20261014_ObserveOnly / CPU論理数={Environment.ProcessorCount} / 64bit={Environment.Is64BitProcess} / StopwatchHz={Stopwatch.Frequency} / 各時刻は処理観測時刻でUSB到着時刻ではありません");
                using var process = Process.GetCurrentProcess();
                long previous = Stopwatch.GetTimestamp(), sampleAt = previous, pendingAt = 0;
                TimeSpan lastCpu = process.TotalProcessorTime;
                int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                while (!_stop.WaitOne(500))
                {
                    long now = Stopwatch.GetTimestamp();
                    double interval = Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds;
                    previous = now;
                    if (interval >= 1000)
                        _write("OBS_THREAD_DELAY", $"500ms専用観測スレッドの実行間隔={interval:F1}ms / tick={now}");
                    Drain();
                    if (Stopwatch.GetElapsedTime(pendingAt, now).TotalSeconds >= 1)
                    {
                        pendingAt = now;
                        foreach (var scope in Active.Values)
                        {
                            double ms = Stopwatch.GetElapsedTime(scope.Started, now).TotalMilliseconds;
                            if (ms >= 500)
                                _write("OBS_PENDING", $"区間={scope.Name} / thread={scope.ThreadId} / elapsed={ms:F1}ms / startTick={scope.Started} / tick={now}");
                        }
                    }
                    if (Stopwatch.GetElapsedTime(sampleAt, now).TotalSeconds >= 5)
                    {
                        process.Refresh();
                        TimeSpan cpu = process.TotalProcessorTime;
                        int n0 = GC.CollectionCount(0), n1 = GC.CollectionCount(1), n2 = GC.CollectionCount(2);
                        ThreadPool.GetAvailableThreads(out int workers, out int io);
                        double sampleMs = Stopwatch.GetElapsedTime(sampleAt, now).TotalMilliseconds;
                        _write("OBS_RUNTIME",
                            $"tick={now} / sampleMs={sampleMs:F1} / CPU使用時間増分={(cpu - lastCpu).TotalMilliseconds:F1}ms / GC増分={n0-g0},{n1-g1},{n2-g2} / heap={GC.GetTotalMemory(false)}bytes / ThreadPool空きworker={workers},IO={io} / poolAgeMs={AgeMs(Interlocked.Read(ref _poolTick)):F1} / uiAgeMs={AgeMs(Interlocked.Read(ref _uiTick)):F1} / active={Active.Count}");
                        sampleAt = now;
                        lastCpu = cpu;
                        g0 = n0; g1 = n1; g2 = n2;
                    }
                }
                Drain();
            }
            catch (Exception ex)
            {
                // 観測だけを停止する。通信・測定・終了処理へ例外を戻さない。
                Trace.WriteLine("PC遅延観測を停止: " + ex);
                try { _write("OBS_ERROR", ex.ToString()); } catch { }
            }
            finally
            {
                _pool.Dispose();
                _stop.Dispose();
            }
        }

        private void Drain()
        {
            // 1回の処理量を制限し、専用スレッドの監視を長く妨げない。
            for (int i = 0; i < 256 && Reports.TryDequeue(out var item); i++)
            {
                Interlocked.Decrement(ref _queued);
                _write(item.Kind, item.Detail);
            }
            int dropped = Interlocked.Exchange(ref _dropped, 0);
            if (dropped != 0) _write("OBS_DROPPED", $"観測キュー上限で{dropped}件を省略");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try { _stop.Set(); }
                catch (ObjectDisposedException) { /* 観測スレッドが先に終了した場合 */ }
            }
            // 終了時に観測スレッドを待たない。
        }
    }
}
