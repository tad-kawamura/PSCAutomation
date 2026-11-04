using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using PSCAutomation.Wrappers.Common;

namespace PSCAutomation.Wrappers.Arduino;

// 正常時は直近1分をメモリ保持。受信側ではファイルを操作しない。
// 専用スレッドで250msごとにまとめて保存。復旧後30秒は同じ異常にまとめる。
// 保存期限・自動削除はなし。保存不能時の待機データは4Mi文字まで保持する。
// 元: AS100201SwitchController Arduino/ArduinoDiagnosticLog.cs(Configure 方式)。
// AS110AllCheck 版にだけあった異常種別 SHUTDOWN_ERROR を共通の種別に足した。
// AS110 固有の NF_STALE・FRAME_GAP・NF_RECOVERY_START はアプリ側が Configure で渡す。
public static class ArduinoDiagnosticLog
{
    private static string _applicationName = AppDomain.CurrentDomain.FriendlyName;
    private static HashSet<string> _protocolIncidents = new();

    // Configure once on startup, before any communication or background logging.
    // applicationName は保存先 Logs に書けないときの代わりの場所(LocalApplicationData\<applicationName>\Logs)に使う。
    // protocolIncidents はアプリ固有の異常種別(例: "NF_STALE")。共通の種別に追加される。
    public static void Configure(string applicationName, params string[] protocolIncidents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        ArgumentNullException.ThrowIfNull(protocolIncidents);
        _applicationName = applicationName;
        _protocolIncidents = new HashSet<string>(protocolIncidents);
    }

    /// <summary>Configure で設定したアプリ名(未設定ならプロセスのアプリ名)。</summary>
    public static string ApplicationName => _applicationName;

    /// <summary>通常の保存先(実行ファイルの隣の Logs)。書けないときは <see cref="FallbackDirectory"/> へ保存する。</summary>
    public static string PrimaryDirectory => System.IO.Path.Combine(AppContext.BaseDirectory, "Logs");

    /// <summary>通常の保存先に書けないときの保存先(LocalApplicationData\アプリ名\Logs)。</summary>
    public static string FallbackDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), _applicationName, "Logs");

    private static readonly object Gate = new();
    private static readonly Queue<(long Timestamp, string Line)> Recent = new();
    private static readonly Queue<Batch> Jobs = new();
    private static readonly AutoResetEvent Wake = new(false);
    private static readonly TimeSpan History = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MergeQuiet = TimeSpan.FromSeconds(30);
    private const int MaxBufferedChars = 4 * 1024 * 1024;
    private static Session? _active;
    private static string? _lastPath;
    private static int _bufferedChars;
    private static long _droppedLines;
    private static long _lastIoReport;

    private sealed class Session
    {
        public readonly string FileName = $"ArduinoIncident_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Environment.ProcessId}_{Guid.NewGuid():N}.log";
        public readonly StringBuilder Pending = new();
        public string? Path;
        public long RecoveredAt;
    }

    private sealed record Batch(Session? Owner, string Text, ManualResetEventSlim? Completion = null);

    static ArduinoDiagnosticLog()
    {
        // ThreadPool不足の影響を受けにくい専用スレッド。アプリ終了を妨げない。
        new Thread(SaveLoop) { IsBackground = true, Name = "Arduino diagnostic writer" }.Start();
    }

    public static string PathName
    {
        get
        {
            lock (Gate)
                return _active?.Path ?? _lastPath ??
                    "正常時は直近1分をメモリ保持。異常時は別スレッドでLogsへ保存（書込不可時はLocalApplicationData）";
        }
    }

    public static void Write(string kind, string message)
    {
        long gateStarted = Stopwatch.GetTimestamp();
        lock (Gate)
        {
            double gateMs = Stopwatch.GetElapsedTime(gateStarted).TotalMilliseconds;
            if (gateMs >= 100)
                PcDelayObservation.Report("OBS_LOG_LOCK", $"診断ログGate待ち={gateMs:F1}ms / kind={kind}");
            long now = Stopwatch.GetTimestamp();
            while (Recent.Count > 0 && Stopwatch.GetElapsedTime(Recent.Peek().Timestamp, now) > History)
                Recent.Dequeue();

            CloseRecoveredSession(now);
            string line = Format(kind, message);
            if (IsIncident(kind))
            {
                if (_active is null)
                {
                    _active = new Session();
                    AppendPending(_active, "# 異常前の直近1分と復旧後30秒まで。短時間の再発は同じファイルへ記録\n");
                    foreach (var item in Recent) AppendPending(_active, item.Line);
                }
                _active.RecoveredAt = 0;
            }

            Recent.Enqueue((now, line));
            if (_active is not null)
            {
                AppendPending(_active, line);
                if (kind == "RECEIVING" && _active.RecoveredAt == 0)
                    _active.RecoveredAt = now;
                if (kind == "END") CloseActive();
            }
        }
        if (kind == "END") Wake.Set();
    }

    public static void CompleteIfActive(string message)
    {
        lock (Gate)
        {
            // 毎回の正常監視で30秒の期限を延長しない。
            if (_active is not null && _active.RecoveredAt == 0) Write("RECEIVING", message);
        }
    }

    // 通信を閉じた後だけ使用する。ディスクが応答しなくても終了を最大2秒で進める。
    public static void FlushOnExit()
    {
        var completion = new ManualResetEventSlim(false);
        lock (Gate)
        {
            CloseActive();
            Jobs.Enqueue(new Batch(null, "", completion));
        }
        Wake.Set();
        // タイムアウト後も保存スレッドがSetするため、ここでDisposeしない。
        if (!completion.Wait(TimeSpan.FromSeconds(2)))
            Trace.WriteLine("Arduino診断ログ：終了時保存が2秒以内に完了しませんでした。");
    }

    private static string Format(string kind, string message) =>
        $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{kind}] {message.Replace("\r", "\\r").Replace("\n", "\\n")}\n";

    private static void AppendPending(Session owner, string line)
    {
        if (_bufferedChars + line.Length > MaxBufferedChars)
        {
            _droppedLines++;
            return;
        }
        owner.Pending.Append(line);
        _bufferedChars += line.Length;
    }

    private static void QueuePending(Session owner)
    {
        if (owner.Pending.Length == 0) return;
        Jobs.Enqueue(new Batch(owner, owner.Pending.ToString()));
        owner.Pending.Clear();
    }

    private static void CloseActive()
    {
        if (_active is null) return;
        QueuePending(_active);
        _active = null;
    }

    private static void CloseRecoveredSession(long now)
    {
        if (_active is not null && _active.RecoveredAt != 0 &&
            Stopwatch.GetElapsedTime(_active.RecoveredAt, now) >= MergeQuiet)
            CloseActive();
    }

    /// <summary>この種別を書いたときに異常ログ(ファイル保存)を始めるか。共通の種別と Configure で渡した種別。</summary>
    public static bool IsIncident(string kind) => _protocolIncidents.Contains(kind) || kind is
        "COM_DISAPPEARED" or "PORT_CLOSED" or "RX_STALE" or
        "SERIAL_ERROR" or "READ_ERROR" or "READ_TIMEOUT" or "WRITE_ERROR" or
        "VERIFY_ERROR" or "CONNECT_ERROR" or "CONNECT_FAILED" or "MONITOR_ERROR" or
        "APP_RECEIVE_HANDLER_ERROR" or "ARDUINO_RESTART" or
        "RX_PARTIAL_STALE" or "RX_LINE_TOO_LONG" or "RECEIVE_HANDLER_SLOW" or
        "READER_ERROR" or "ARDUINO_COMMAND" or "READ_SLOW" or "RX_LOCK_SLOW" or "STARTUP_SLOW" or
        "SHUTDOWN_ERROR";

    private static void SaveLoop()
    {
        Batch? retry = null;
        while (true)
        {
            Wake.WaitOne(retry is null ? 250 : 1000);
            lock (Gate)
            {
                CloseRecoveredSession(Stopwatch.GetTimestamp());
                if (_active is not null) QueuePending(_active);
            }
            while (true)
            {
                Batch? job = retry;
                if (job is null)
                {
                    lock (Gate) job = Jobs.Count == 0 ? null : Jobs.Dequeue();
                }
                if (job is null) break;
                if (job.Owner is null)
                {
                    job.Completion?.Set();
                    continue;
                }
                long started = Stopwatch.GetTimestamp();
                if (!TrySave(job))
                {
                    retry = job;
                    break;
                }
                retry = null;
                double ioMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                long dropped;
                lock (Gate)
                {
                    _bufferedChars -= job.Text.Length;
                    _lastPath = job.Owner.Path;
                    dropped = _droppedLines;
                    _droppedLines = 0;
                }
                if (dropped > 0) Write("LOG_DROPPED", $"保存待ち上限により{dropped}行を省略。通信処理は継続");
                if (ioMs >= 100 || Stopwatch.GetElapsedTime(_lastIoReport).TotalSeconds >= 5)
                {
                    _lastIoReport = Stopwatch.GetTimestamp();
                    Write("LOG_IO", $"別スレッドのまとめ書込={ioMs:F1}ms / chars={job.Text.Length}");
                }
            }
        }
    }

    private static bool TrySave(Batch job)
    {
        Session owner = job.Owner!;
        // 保存開始後に別の場所へ分割しない。I/O中はGateを保持しない。
        string[] paths = owner.Path is not null ? new[] { owner.Path } : new[]
        {
            System.IO.Path.Combine(PrimaryDirectory, owner.FileName),
            System.IO.Path.Combine(FallbackDirectory, owner.FileName)
        };
        foreach (string path in paths)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                File.AppendAllText(path, job.Text, new UTF8Encoding(false));
                lock (Gate) owner.Path = path;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { Trace.WriteLine("診断ログ保存失敗: " + ex.Message); }
        }
        return false;
    }
}
