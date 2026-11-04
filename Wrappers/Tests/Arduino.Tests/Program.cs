using PSCAutomation.Wrappers.Arduino;

// Arduino(元: AS110AllCheck / AS100201SwitchController の Arduino/)の検査。モノレポで追加。
// 実 COM ポートは開かない(ConnectAsync はポートを総当たりして接続確認文字列を送るので呼ばない)。
// 確かめるのは、異常ログの種別・Configure・保存先・保存の流れと、ポートがないときの ArduinoConnection の振る舞い。
void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
void Fails<T>(Action action, string name) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"{name}: {typeof(T).Name} が出るべき操作が成功した");
}
async Task FailsAsync<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception($"{name}: {typeof(T).Name} が出るべき操作が成功した");
}

string logDirectory = ArduinoDiagnosticLog.PrimaryDirectory;
var existing = Directory.Exists(logDirectory)
    ? Directory.GetFiles(logDirectory, "ArduinoIncident_*.log").ToHashSet(StringComparer.OrdinalIgnoreCase)
    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var created = new List<string>();
string[] NewLogs()
{
    if (!Directory.Exists(logDirectory)) return Array.Empty<string>();
    var files = Directory.GetFiles(logDirectory, "ArduinoIncident_*.log")
        .Where(f => !existing.Contains(f) && !created.Contains(f, StringComparer.OrdinalIgnoreCase)).ToArray();
    created.AddRange(files);
    return files;
}

try
{
    // 種別: 共通の異常種別(SwitchController 版 + AllCheck 版の SHUTDOWN_ERROR)。AS110 固有の種別は既定では含まない
    Check(ArduinoDiagnosticLog.ApplicationName == AppDomain.CurrentDomain.FriendlyName, "未設定時のアプリ名");
    foreach (var kind in new[] { "COM_DISAPPEARED", "PORT_CLOSED", "RX_STALE", "SERIAL_ERROR", "READ_ERROR", "READ_TIMEOUT",
        "WRITE_ERROR", "VERIFY_ERROR", "CONNECT_ERROR", "CONNECT_FAILED", "MONITOR_ERROR", "APP_RECEIVE_HANDLER_ERROR",
        "ARDUINO_RESTART", "RX_PARTIAL_STALE", "RX_LINE_TOO_LONG", "RECEIVE_HANDLER_SLOW", "READER_ERROR", "ARDUINO_COMMAND",
        "READ_SLOW", "RX_LOCK_SLOW", "STARTUP_SLOW", "SHUTDOWN_ERROR" })
        Check(ArduinoDiagnosticLog.IsIncident(kind), "共通の異常種別が抜けた: " + kind);
    foreach (var kind in new[] { "NF_STALE", "FRAME_GAP", "NF_RECOVERY_START", "HEARTBEAT_STALE", "RX", "TX", "START", "END", "RECEIVING", "STATUS" })
        Check(!ArduinoDiagnosticLog.IsIncident(kind), "既定で異常扱いになっている: " + kind);
    Console.WriteLine("PASS ArduinoDiagnosticLog: 共通の異常種別(SHUTDOWN_ERROR を含む)。アプリ固有の種別は既定で含まない");

    // Configure: アプリ名と固有の異常種別を注入。呼び直すと前の固有種別は置き換わる
    Fails<ArgumentException>(() => ArduinoDiagnosticLog.Configure(""), "空のアプリ名");
    Fails<ArgumentException>(() => ArduinoDiagnosticLog.Configure(null!), "アプリ名なし");
    Fails<ArgumentNullException>(() => ArduinoDiagnosticLog.Configure("App", null!), "種別の配列なし");
    ArduinoDiagnosticLog.Configure("SomeOtherApp", "HEARTBEAT_STALE");
    Check(ArduinoDiagnosticLog.IsIncident("HEARTBEAT_STALE"), "Configure の固有種別が効かない");
    ArduinoDiagnosticLog.Configure("ArduinoTestsApp", "NF_STALE", "FRAME_GAP", "NF_RECOVERY_START");
    Check(ArduinoDiagnosticLog.ApplicationName == "ArduinoTestsApp", "Configure のアプリ名");
    Check(ArduinoDiagnosticLog.IsIncident("NF_STALE") && ArduinoDiagnosticLog.IsIncident("NF_RECOVERY_START"), "AS110 の固有種別が効かない");
    Check(!ArduinoDiagnosticLog.IsIncident("HEARTBEAT_STALE"), "前の Configure の固有種別が残った");
    Check(ArduinoDiagnosticLog.IsIncident("WRITE_ERROR"), "Configure で共通の種別が消えた");
    Console.WriteLine("PASS ArduinoDiagnosticLog.Configure: アプリ名と固有の異常種別を注入し、呼び直すと置き換わる。空の名前は止める");

    // 保存先: 実行ファイルの隣の Logs。書けないときは LocalApplicationData\<アプリ名>\Logs(元 AllCheck は "AS110BoltCheck" 直書き)
    Check(logDirectory == Path.Combine(AppContext.BaseDirectory, "Logs"), "通常の保存先");
    Check(ArduinoDiagnosticLog.FallbackDirectory == Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArduinoTestsApp", "Logs"), "代わりの保存先がアプリ名に従わない");
    Console.WriteLine("PASS ArduinoDiagnosticLog: 保存先は実行ファイルの隣の Logs、代わりは LocalApplicationData\\<Configure のアプリ名>\\Logs");

    // 正常時はファイルを作らない(直近 1 分をメモリ保持するだけ)
    ArduinoDiagnosticLog.Write("TEST_NORMAL", "normal-marker");
    ArduinoDiagnosticLog.FlushOnExit();
    Check(NewLogs().Length == 0, "異常がないのにファイルが作られた");
    Console.WriteLine("PASS ArduinoDiagnosticLog: 正常な記録だけではファイルを作らない");

    // 異常時: 直近 1 分の記録を先頭に付けて 1 ファイルへ保存。改行はエスケープ。復旧の記録も同じファイルへ
    ArduinoDiagnosticLog.Write("TEST_BEFORE", "before-marker\r\nline2");
    ArduinoDiagnosticLog.Write("NF_STALE", "incident-marker");
    ArduinoDiagnosticLog.CompleteIfActive("recover-marker");
    ArduinoDiagnosticLog.FlushOnExit();
    var first = NewLogs();
    Check(first.Length == 1, $"異常 1 回でファイルが {first.Length} 個");
    Check(ArduinoDiagnosticLog.PathName == first[0], "PathName が保存したファイルを指さない");
    string text = File.ReadAllText(first[0]);
    Check(text.StartsWith("# 異常前の直近1分"), "見出しがない");
    Check(text.Contains("[TEST_NORMAL] normal-marker") && text.Contains("[TEST_BEFORE] before-marker\\r\\nline2"), "異常前の記録・改行のエスケープ");
    Check(text.Contains("[NF_STALE] incident-marker") && text.Contains("[RECEIVING] recover-marker"), "異常と復旧の記録");
    Check(text.IndexOf("normal-marker", StringComparison.Ordinal) < text.IndexOf("incident-marker", StringComparison.Ordinal), "記録の順番");
    Console.WriteLine("PASS ArduinoDiagnosticLog: 異常時は直近の記録ごと 1 ファイルへ保存し、改行をエスケープ、復旧(RECEIVING)も同じファイルへ");

    // 異常ファイルを閉じた後の CompleteIfActive は何も書かない。SHUTDOWN_ERROR は新しいファイルを始める
    ArduinoDiagnosticLog.CompleteIfActive("ghost-marker");
    ArduinoDiagnosticLog.Write("SHUTDOWN_ERROR", "shutdown-marker");
    ArduinoDiagnosticLog.FlushOnExit();
    var second = NewLogs();
    Check(second.Length == 1, $"SHUTDOWN_ERROR でファイルが {second.Length} 個");
    string secondText = File.ReadAllText(second[0]);
    Check(secondText.Contains("[SHUTDOWN_ERROR] shutdown-marker") && !secondText.Contains("ghost-marker"), "異常がないときの CompleteIfActive が書いた");
    Console.WriteLine("PASS ArduinoDiagnosticLog: SHUTDOWN_ERROR で新しいファイルを始める。異常がないときの CompleteIfActive は書かない");

    // ArduinoConnection: ポートを開かずに確かめられる範囲
    var connection = new ArduinoConnection();
    int disconnected = 0;
    connection.Disconnected += () => disconnected++;
    Check(!connection.IsConnected, "接続前に IsConnected");
    Fails<InvalidOperationException>(() => connection.SendLine("HELLO"), "接続前の送信");
    await FailsAsync<ArgumentException>(() => connection.VerifyAsync(" ", _ => true), "空の確認文字列");
    await FailsAsync<ArgumentNullException>(() => connection.ConnectAsync("HELLO", null!), "応答判定なし");
    await connection.DisconnectAsync();
    Check(!connection.IsConnected, "切断後に IsConnected");
    connection.Dispose();
    Check(!connection.IsConnected && disconnected == 0, "Dispose 後の状態・切断通知");
    Fails<ObjectDisposedException>(() => connection.SendLine("HELLO"), "Dispose 後の送信");
    await FailsAsync<ObjectDisposedException>(() => connection.ConnectAsync("HELLO", _ => true), "Dispose 後の接続");
    await FailsAsync<ObjectDisposedException>(() => connection.VerifyAsync("HELLO", _ => true), "Dispose 後の確認");
    Console.WriteLine("PASS ArduinoConnection: 接続前の送信・空の確認文字列・Dispose 後の操作を止め、ポートなしで切断通知を出さない");

    // ArduinoConnection は起動・切断・終了を異常ログへ書く(次の異常ファイルの先頭に入る)
    ArduinoDiagnosticLog.Write("CONNECT_FAILED", "after-connection-marker");
    ArduinoDiagnosticLog.FlushOnExit();
    var third = NewLogs();
    Check(third.Length == 1, $"CONNECT_FAILED でファイルが {third.Length} 個");
    string thirdText = File.ReadAllText(third[0]);
    Check(thirdText.Contains("[START] アプリ起動") && thirdText.Contains("[CLOSE] 対象=なし") && thirdText.Contains("[END] Arduino通信を終了"),
        "接続クラスの起動・切断・終了の記録がない");
    Console.WriteLine("PASS ArduinoConnection: 起動・切断・終了を ArduinoDiagnosticLog へ書く");
    Console.WriteLine("Arduino: all checks passed.");
}
finally
{
    NewLogs();
    foreach (var file in created) File.Delete(file);
}
