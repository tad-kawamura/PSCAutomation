using System.Text;

namespace PSCAutomation.Apps.AS110AllCheck;

// 画面のログ欄に出した行を、日ごとのファイル「画面ログ_yyyyMMdd.txt」に全部残す(2026-11-14、全体見直し・区切り 6、人の決定)。
// それまでファイルに残るのは M5Stack の通信異常のときの直前 1 分だけ(ArduinoDiagnosticLog)で、保存の失敗や判定の食い違いは、
// アプリを閉じるとログが消えた。
// 置き場は異常ログと同じ Logs フォルダー(exe の隣。書けないときは LocalApplicationData\AS110AllCheck\Logs)。
// 画面の処理(測定開始の前後を含む)を待たせないよう、書き込みは別スレッドでまとめて行う。書けない間は 400 万文字までためて、超えた分は捨てる。
// 古いファイルは自動では消さない(人が片づける)。
internal sealed class ScreenLogFile
{
    public const string FilePrefix = "画面ログ_";
    private const int MaxPendingChars = 4 * 1024 * 1024;

    // アプリが使う 1 つ。置き場は異常ログと同じ。
    public static ScreenLogFile Default { get; } = new(
        () => ArduinoDiagnosticLog.PrimaryDirectory, () => ArduinoDiagnosticLog.FallbackDirectory);

    private readonly Func<string> _primaryDirectory, _fallbackDirectory;
    private readonly object _gate = new();
    private readonly List<(DateTime Day, string Line)> _pending = [];
    private readonly AutoResetEvent _wake = new(false);
    private int _pendingChars;
    private long _dropped;
    private bool _started;
    private string? _lastPath;

    public ScreenLogFile(Func<string> primaryDirectory, Func<string> fallbackDirectory)
    {
        _primaryDirectory = primaryDirectory;
        _fallbackDirectory = fallbackDirectory;
    }

    /// <summary>最後に書けたファイル(まだ書いていなければ null)。</summary>
    public string? LastPath { get { lock (_gate) return _lastPath; } }

    public static string FileName(DateTime day) => $"{FilePrefix}{day:yyyyMMdd}.txt";

    public void Write(DateTime now, string message)
    {
        string line = $"{now:yyyy-MM-dd HH:mm:ss.fff}  {message.Replace("\r", "\\r").Replace("\n", "\\n")}{Environment.NewLine}";
        lock (_gate)
        {
            if (_pendingChars + line.Length > MaxPendingChars) { _dropped++; return; }
            _pending.Add((now.Date, line));
            _pendingChars += line.Length;
            if (!_started)
            {
                _started = true;
                // アプリの終了を妨げない専用スレッド。
                new Thread(SaveLoop) { IsBackground = true, Name = "Screen log writer" }.Start();
            }
        }
    }

    /// <summary>ためた行を書き終えるまで待つ(終了のとき)。ディスクが応答しなくても timeout で進める。書き終えたら true。</summary>
    public bool Flush(TimeSpan timeout)
    {
        var limit = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            lock (_gate) if (_pending.Count == 0 && !_saving) return true;
            if (limit.Elapsed >= timeout) return false;
            _wake.Set();
            Thread.Sleep(20);
        }
    }

    private bool _saving;

    private void SaveLoop()
    {
        while (true)
        {
            _wake.WaitOne(500);
            List<(DateTime Day, string Line)> batch;
            long dropped;
            lock (_gate)
            {
                if (_pending.Count == 0) continue;
                batch = [.. _pending];
                dropped = _dropped;
                _saving = true;
            }
            int written = 0;
            try
            {
                // 日付が変わる行で分ける(0 時をまたいで使っても、行はその日のファイルに入る)。
                while (written < batch.Count)
                {
                    DateTime day = batch[written].Day;
                    var text = new StringBuilder();
                    int count = 0;
                    for (int i = written; i < batch.Count && batch[i].Day == day; i++, count++) text.Append(batch[i].Line);
                    if (dropped > 0 && written == 0) text.Insert(0, $"（書き込めない間にたまりすぎたため、{dropped} 行を省きました）{Environment.NewLine}");
                    if (!TrySave(day, text.ToString())) break;
                    written += count;
                }
            }
            finally
            {
                lock (_gate)
                {
                    for (int i = 0; i < written; i++) _pendingChars -= _pending[i].Line.Length;
                    _pending.RemoveRange(0, written);
                    if (written > 0) _dropped -= dropped;
                    _saving = false;
                }
            }
        }
    }

    private bool TrySave(DateTime day, string text)
    {
        foreach (Func<string> directory in new[] { _primaryDirectory, _fallbackDirectory })
        {
            try
            {
                string folder = directory();
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, FileName(day));
                // メモ帳でそのまま読めるよう BOM 付きの UTF-8(追記のときは BOM を足さない)。
                using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(stream.Length == 0)))
                    writer.Write(text);
                lock (_gate) _lastPath = path;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
            { System.Diagnostics.Trace.WriteLine("画面ログ保存失敗: " + ex.Message); }
        }
        return false;
    }
}
