using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PSCAutomation.Wrappers.WaveLogger.Automation
{
    // WAVE LOGGER／WAVE LOGGER Xを共通の操作で制御する。
    // オートメーション経由で起動・初期化し、NR本体の接続を確認する。
    // 測定開始・停止・状態取得・ファイルの開閉を担当する。
    // データをXDT／CSVへ保存する操作も用意されている。
    // LOGGERの種類による操作の違いを、このファイル内で吸収する。
    public enum WaveLoggerEdition
    {
        Auto,
        Legacy,
        WaveLoggerX
    }

    /// <summary>
    /// 接続・終了の扱い方。既定はManagedShutdown。
    /// ReferenceOnlyはLOGGERを停止・Quitせず、COM参照だけを解放するアプリ用(元: AS100201SwitchController)。
    /// </summary>
    public enum WaveLoggerLifecycle { ManagedShutdown, ReferenceOnly }

    /// <summary>
    /// NR-X本体とWAVE LOGGER Xの接続方式。
    /// </summary>
    public enum NrXConnectionMethod
    {
        Usb,
        Wifi
    }

    /// <summary>
    /// WAVE LOGGER / WAVE LOGGER XのCOM接続を所有する共通ラッパー。
    /// 同じSTAスレッド・同期コンテキストで直列に使用し、通常の終了はShutdownAsyncで行う。
    /// COMオブジェクトは外部へ公開しない。接続・停止・解放の状態はこのインスタンスが保持する。
    /// </summary>
    public sealed partial class WaveLoggerSession
    {
        private const string LegacyProgId = "WaveLogger2.Application";
        private const string WaveLoggerXProgId = "WaveLoggerX.Application";
        private const long WaveLoggerXNr500UnitCode = 193;
        private const long WaveLoggerXNrX100UnitCode = 198;
        private const long WaveLoggerXNrXr01UnitCode = 199;

        private object? _application;
        private LoggerProcessLifetime? _serverProcess;
        // COM解放後に終了確認が失敗しても、次回「確認」で同じ対象だけを再試行する。
        private LoggerProcessLifetime? _pendingProcess;
        private bool _pendingTerminationAllowed;

        /// <summary>処理時間の観測用。ホストのログ実装を注入できる。LOGGER制御には使用しない。</summary>
        public Func<string, IDisposable>? ObserveOperation { get; init; }

        /// <summary>ReferenceOnlyでは接続時の追加の状態照会とLOGGERプロセスの追跡も行わない。</summary>
        public WaveLoggerLifecycle Lifecycle { get; init; } = WaveLoggerLifecycle.ManagedShutdown;

        public bool HasPendingShutdown => _pendingProcess is not null;
        private Action<string>? _connectionDiagnosticLog;
        private bool _lanConnectedByAutomation;
        private int? _connectedIdentifier;
        // X版は初回Start成功後、次回以降をマニュアルTriggerで開始する。
        // Stopや自然停止では維持し、ファイル開閉・接続処理でリセットする。
        private bool _waveLoggerXStartCompleted;

        public WaveLoggerEdition ActiveEdition { get; private set; }
            = WaveLoggerEdition.Auto;

        public bool IsInitialized => _application is not null;

        /// <summary>
        /// 呼び出し元で使用する初期化メソッド。
        /// WAVE LOGGER Xを優先し、未登録の場合はWAVE LOGGERを使用する。
        /// </summary>
        public void Initialize()
        {
            InitializeCore(WaveLoggerEdition.Auto, visible: true);
        }

        /// <summary>
        /// 指定されたLOGGERを初期化する。
        /// </summary>
        public void Initialize(WaveLoggerEdition edition, Action<string>? timingLog = null)
        {
            InitializeCore(edition, visible: true, timingLog: timingLog);
        }

        /// <summary>
        /// PCにインストールされているLOGGERを取得する。
        /// WAVE LOGGER X、WAVE LOGGERの順で返す。
        /// </summary>
        public static IReadOnlyList<WaveLoggerEdition> GetInstalledEditions()
        {
            var editions = new List<WaveLoggerEdition>(2);

            if (Type.GetTypeFromProgID(WaveLoggerXProgId) is not null)
            {
                editions.Add(WaveLoggerEdition.WaveLoggerX);
            }

            if (Type.GetTypeFromProgID(LegacyProgId) is not null)
            {
                editions.Add(WaveLoggerEdition.Legacy);
            }

            return editions;
        }

        public static string GetEditionDisplayName(WaveLoggerEdition edition)
        {
            return edition switch
            {
                WaveLoggerEdition.WaveLoggerX => "WAVE LOGGER X",
                WaveLoggerEdition.Legacy => "WAVE LOGGER",
                _ => "自動選択"
            };
        }

        /// <summary>
        /// 呼び出し元で使用するNR-500 USB機器ID設定メソッド。
        /// </summary>
        public long SetIdentifier(int id)
        {
            using var observation = ObserveOperation?.Invoke("LOGGER.SetIdentifier");
            return SetNr500Identifier(id);
        }

        /// <summary>
        /// 呼び出し元で使用するNR-500確認結果。
        /// </summary>
        public sealed class Nr500CheckResult
        {
            public Nr500CheckResult(
                bool isDetected,
                string loggerApi,
                string description)
            {
                IsDetected = isDetected;
                LoggerApi = loggerApi;
                Description = description;
            }

            public bool IsDetected { get; }
            public string LoggerApi { get; }
            public string Description { get; }
        }

        /// <summary>
        /// NR本体の自動判定結果。
        /// </summary>
        public sealed class HardwareCheckResult
        {
            public HardwareCheckResult(
                bool isDetected,
                string hardwareName,
                string loggerApi,
                string description)
            {
                IsDetected = isDetected;
                HardwareName = hardwareName;
                LoggerApi = loggerApi;
                Description = description;
            }

            public bool IsDetected { get; }
            public string HardwareName { get; }
            public string LoggerApi { get; }
            public string Description { get; }
        }

        /// <summary>
        /// USB IDを基準にNR-500またはNR-Xを自動判定する。
        /// WAVE LOGGER XではNR-500用IDとNR-X用IDを順番に確認する。
        /// WAVE LOGGERはNR-500だけに対応する。
        /// </summary>
        public HardwareCheckResult DetectHardware(int usbId = 0)
        {
            if (usbId is < 0 or > 3)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(usbId), usbId, "USB機器IDは0～3です。");
            }

            return ActiveEdition == WaveLoggerEdition.WaveLoggerX
                ? DetectHardwareWithWaveLoggerX(usbId)
                : DetectHardwareWithLegacyLogger(usbId);
        }

        /// <summary>
        /// NR-500をUSBで確認する。
        /// </summary>
        public HardwareCheckResult DetectNr500Usb(int usbId = 0)
        {
            using var observation = ObserveOperation?.Invoke("LOGGER.DetectNr500Usb");
            if (usbId is < 0 or > 3)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(usbId), usbId, "USB機器IDは0～3です。");
            }

            return ActiveEdition == WaveLoggerEdition.WaveLoggerX
                ? DetectNr500WithWaveLoggerX(usbId)
                : DetectHardwareWithLegacyLogger(usbId);
        }

        /// <summary>
        /// NR-XをWAVE LOGGER Xで確認する。
        /// USBはSetIdentifier(4～7)、Wi-Fi/LANはSetIdentifier(8～11)を使用する。
        /// </summary>
        public HardwareCheckResult DetectNrX(
            int deviceId,
            NrXConnectionMethod connectionMethod)
        {
            using var observation = ObserveOperation?.Invoke("LOGGER.DetectNrX");
            if (deviceId is < 0 or > 3)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(deviceId), deviceId, "NR-Xの機器IDは0～3です。");
            }

            if (ActiveEdition != WaveLoggerEdition.WaveLoggerX)
            {
                return new HardwareCheckResult(
                    false,
                    "未検出",
                    "WAVE LOGGER / WaveLogger2.Application",
                    "NR-X本体はWAVE LOGGER Xで接続してください。");
            }

            return DetectNrXWithWaveLoggerX(deviceId, connectionMethod);
        }

        /// <summary>
        /// NR-X用Wi-Fiへ切替済みの状態で、WAVE LOGGER XのLAN接続を確立して本体を確認する。
        /// </summary>
        public HardwareCheckResult ConnectAndDetectNrXWifi(
            int deviceId,
            int address1,
            int address2,
            int address3,
            int address4,
            int tcpPort)
        {
            if (deviceId is < 0 or > 3)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(deviceId), deviceId, "NR-XのLAN機器IDは0～3です。");
            }

            ValidateIpAddressPart(address1, nameof(address1));
            ValidateIpAddressPart(address2, nameof(address2));
            ValidateIpAddressPart(address3, nameof(address3));
            ValidateIpAddressPart(address4, nameof(address4));

            if (tcpPort is < 1025 or > 65535)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(tcpPort), tcpPort, "LANポート番号は1025～65535です。");
            }

            if (ActiveEdition != WaveLoggerEdition.WaveLoggerX)
            {
                return new HardwareCheckResult(
                    false,
                    "未検出",
                    "WAVE LOGGER / WaveLogger2.Application",
                    "NR-X本体のLAN接続にはWAVE LOGGER Xが必要です。");
            }

            return ConnectAndDetectNrXWithWaveLoggerX(
                deviceId,
                address1,
                address2,
                address3,
                address4,
                tcpPort);
        }

        /// <summary>
        /// 呼び出し元で使用するNR-500本体確認メソッド。
        /// SetIdentifier実行後に呼び出す。
        /// </summary>
        public Nr500CheckResult CheckNr500(int id)
        {
            long unitInfo = GetUnitInfo(id);

            if (ActiveEdition == WaveLoggerEdition.WaveLoggerX)
            {
                bool detected = unitInfo == WaveLoggerXNr500UnitCode;
                string systemError = GetSystemErrorMessage();
                string description = detected
                    ? "GetUnitInfo(0, 0) = 193（NR-500）"
                    : string.IsNullOrWhiteSpace(systemError)
                        ? $"GetUnitInfo(0, 0) = {unitInfo}"
                        : $"GetUnitInfo(0, 0) = {unitInfo} / {systemError}";

                return new Nr500CheckResult(
                    detected,
                    "WAVE LOGGER X / WaveLoggerX.Application",
                    description);
            }

            // WAVE LOGGERではGetUnitInfo(id)が負数でなければ通信成功とする。
            bool legacyDetected = unitInfo >= 0;
            return new Nr500CheckResult(
                legacyDetected,
                "WAVE LOGGER / WaveLogger2.Application",
                $"GetUnitInfo({id}) = {unitInfo}");
        }

        /// <summary>
        /// 呼び出し元で使用する測定開始メソッド。
        /// </summary>
        public Task<long> StartAsync(Action<string> log, Action beforeTrigger)
        {
            return StartCoreAsync(log, beforeTrigger);
        }

        // トリガなし・1回収集の雛形用。毎回Startし、Triggerは送らない。
        public long StartSingleAcquisition()
        {
            ResetMeasurementStart();
            using var observation = ObserveOperation?.Invoke("LOGGER.WaveLog_Start");
            dynamic app = GetApplication();
            return Convert.ToInt64(app.Start());
        }

        /// <summary>
        /// 旧版・X版とも、メーカーのStart()を1回だけ呼ぶ(元: AS100201SwitchController)。
        /// Trigger・状態待ち・再試行・開始準備のリセットは行わない。戻り値と例外はそのまま返す。
        /// StartSingleAcquisition()と違い、ResetMeasurementStart()を呼ばない。
        /// </summary>
        public long Start()
        {
            using var observation = ObserveOperation?.Invoke("LOGGER.WaveLog_Start");
            dynamic app = GetApplication();
            return Convert.ToInt64(app.Start());
        }

        // 参照波形読み込みのため停止した場合も、次回はStartから準備する。
        public void ResetMeasurementStart()
        {
            _waveLoggerXStartCompleted = false;
        }

        /// <summary>
        /// 呼び出し元で使用する状態取得メソッド。
        /// </summary>
        public long GetState()
        {
            return ReadStateCore();
        }

        /// <summary>
        /// 初期化失敗時などのCOM参照解放。通常の終了にはShutdownAsyncを使用する。
        /// </summary>
        public void Release()
        {
            ReleaseCore();
        }

        /// <summary>
        /// LOGGERを起動して初期化する。
        /// AutoはWAVE LOGGER Xを優先し、未登録ならWAVE LOGGERを使用する。
        /// 両方がインストールされているPCで明示的に選ぶ場合は引数を指定する。
        /// </summary>
        private void InitializeCore(
            WaveLoggerEdition edition = WaveLoggerEdition.Auto,
            bool visible = true,
            Action<string>? timingLog = null)
        {
            using var observation = ObserveOperation?.Invoke("LOGGER.WaveLog_Init");
            if (_application is not null)
            {
                throw new InvalidOperationException("WAVE LOGGERは既に初期化されています。");
            }
            if (_pendingProcess is not null)
                throw new InvalidOperationException("旧LOGGERのプロセス終了が未確認です。「確認」で終了を再試行してください。");

            var stepTimer = Stopwatch.StartNew();
            (Type type, WaveLoggerEdition selectedEdition) = ResolveServer(edition);
            var existingProcesses = Lifecycle == WaveLoggerLifecycle.ManagedShutdown &&
                selectedEdition == WaveLoggerEdition.WaveLoggerX
                ? LoggerProcessLifetime.Snapshot("NRWLX") : null;
            timingLog?.Invoke($"LOGGER内訳：COM登録確認 = {stepTimer.Elapsed.TotalSeconds:F3} 秒");

            object? instance = null;
            try
            {
                stepTimer.Restart();
                using (ObserveOperation?.Invoke("LOGGER.Activator.CreateInstance"))
                    instance = Activator.CreateInstance(type)
                        ?? throw new InvalidOperationException(
                            "WAVE LOGGERのCOMオブジェクトを生成できませんでした。");

                timingLog?.Invoke($"LOGGER内訳：COMオブジェクト生成（サーバー起動） = {stepTimer.Elapsed.TotalSeconds:F3} 秒");
                dynamic app = instance;
                stepTimer.Restart();
                using (ObserveOperation?.Invoke("LOGGER.Initialize.COM"))
                    app.Initialize();
                timingLog?.Invoke($"LOGGER内訳：Initialize = {stepTimer.Elapsed.TotalSeconds:F3} 秒");
                if (existingProcesses is not null)
                    _serverProcess = LoggerProcessLifetime.Capture(instance, existingProcesses, visible, timingLog);
                stepTimer.Restart();
                using (ObserveOperation?.Invoke("LOGGER.Visible.COM"))
                    app.Visible = visible;
                timingLog?.Invoke($"LOGGER内訳：画面表示 = {stepTimer.Elapsed.TotalSeconds:F3} 秒");

                _application = instance;
                _waveLoggerXStartCompleted = false;
                ActiveEdition = selectedEdition;
                _connectionDiagnosticLog = Lifecycle == WaveLoggerLifecycle.ManagedShutdown ? timingLog : null;
                if (_connectionDiagnosticLog is not null) LogSessionState("Initialize・表示後", _connectionDiagnosticLog);
            }
            catch
            {
                _serverProcess?.Dispose();
                _serverProcess = null;
                if (instance is not null && Marshal.IsComObject(instance))
                {
                    Marshal.FinalReleaseComObject(instance);
                }

                throw;
            }
        }

        public void SetVisible(bool onOff)
        {
            dynamic app = GetApplication();
            app.Visible = onOff;
        }

        /// <summary>
        /// COMオブジェクトを解放する。
        /// アプリ自体も終了する場合はquitApplication=trueを指定する。
        /// </summary>
        private void ReleaseCore(bool quitApplication = false)
        {
            using var observation = ObserveOperation?.Invoke("LOGGER.WaveLog_Close");
            _waveLoggerXStartCompleted = false;
            object? instance = _application;
            if (instance is null)
            {
                return;
            }

            try
            {
                if (ActiveEdition == WaveLoggerEdition.WaveLoggerX &&
                    _lanConnectedByAutomation)
                {
                    try
                    {
                        dynamic app = instance;
                        if (Convert.ToBoolean(app.IsConnectLan()))
                        {
                            app.DisconnectLan();
                        }
                    }
                    catch
                    {
                        // COM解放を優先する。Wi-Fi復帰は呼出し元で継続する。
                    }
                    finally
                    {
                        _lanConnectedByAutomation = false;
                    }
                }

                if (quitApplication)
                {
                    dynamic app = instance;
                    app.Quit();
                }
            }
            finally
            {
                try
                {
                    if (Marshal.IsComObject(instance))
                        Marshal.FinalReleaseComObject(instance);
                }
                finally
                {
                    _application = null;
                    _serverProcess?.Dispose();
                    _serverProcess = null;
                    _connectedIdentifier = null;
                    _connectionDiagnosticLog = null;
                    _lanConnectedByAutomation = false;
                    _waveLoggerXStartCompleted = false;
                    ActiveEdition = WaveLoggerEdition.Auto;
                }
            }
        }

        /// <summary>
        /// NR-500 USB機器IDを設定する。IDは0～3。
        /// </summary>
        public long SetNr500Identifier(int id)
        {
            if (id is < 0 or > 3)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(id), id, "NR-500のUSB機器IDは0～3です。");
            }

            dynamic app = GetApplication();
            _waveLoggerXStartCompleted = false;
            return Convert.ToInt64(app.SetIdentifier(id));
        }

        public bool ZeroCalibrate(int id)
        {
            dynamic app = GetApplication();
            return Convert.ToBoolean(app.Adjust(id));
        }

        /// <summary>
        /// NR-500本体の接続状態を取得する。
        /// WAVE LOGGER: GetUnitInfo(id)
        /// X版 : GetUnitInfo(0, 0)。正常時は193（NR-500）。
        /// </summary>
        public long GetUnitInfo(long id)
        {
            using var observation = ObserveOperation?.Invoke("LOGGER.WaveLog_Unit_Info");
            dynamic app = GetApplication();

            return ActiveEdition == WaveLoggerEdition.WaveLoggerX
                ? Convert.ToInt64(app.GetUnitInfo(0, 0))
                : Convert.ToInt64(app.GetUnitInfo(id));
        }

        /// <summary>
        /// NR-500をID設定から機種確認まで行う。戻り値はGetUnitInfoの値。
        /// </summary>
        public long ConnectNr500(int id = 0)
        {
            long setIdentifierResult = SetNr500Identifier(id);
            if (setIdentifierResult != 0)
            {
                throw new InvalidOperationException(
                    $"SetIdentifier({id})に失敗しました。戻り値={setIdentifierResult}");
            }

            long unitInfo = GetUnitInfo(id);

            if (ActiveEdition == WaveLoggerEdition.WaveLoggerX &&
                unitInfo != WaveLoggerXNr500UnitCode)
            {
                string systemError = GetSystemErrorMessage();
                string detail = string.IsNullOrWhiteSpace(systemError)
                    ? string.Empty
                    : $" / {systemError}";

                throw new InvalidOperationException(
                    $"NR-500を確認できません。GetUnitInfo(0, 0)={unitInfo}{detail}");
            }

            if (ActiveEdition == WaveLoggerEdition.Legacy && unitInfo < 0)
            {
                throw new InvalidOperationException(
                    $"NR-500の接続確認に失敗しました。GetUnitInfo({id})={unitInfo}");
            }

            return unitInfo;
        }

        /// <summary>
        /// X版は初回Startで準備し、トリガ待ち確認後にTrigger。
        /// 次回以降もTriggerを発行する。旧LOGGERは毎回Startだけを発行する。
        /// await後も呼出し元のUIスレッドでCOM操作を続ける。
        /// </summary>
        private async Task<long> StartCoreAsync(
            Action<string> log, Action beforeTrigger)
        {
            dynamic app = GetApplication();
            if (ActiveEdition != WaveLoggerEdition.WaveLoggerX)
            {
                using var observation = ObserveOperation?.Invoke("LOGGER.WaveLog_Start");
                return Convert.ToInt64(app.Start());
            }

            if (!_waveLoggerXStartCompleted)
            {
                long startResult;
                using (ObserveOperation?.Invoke("LOGGER.WaveLog_Start"))
                    startResult = Convert.ToInt64(app.Start());
                log($"LOGGER X Start：戻り値={startResult}");
                if (startResult != 0) return startResult;

                // Start成功は収集開始とは限らない。必ずトリガ待ちまで確認する。
                var timer = Stopwatch.StartNew();
                long? previousState = null;
                while (true)
                {
                    if (!ReferenceEquals(_application, (object)app))
                        throw new InvalidOperationException("LOGGERの接続が変更されました。");
                    long state = ReadStateCore();
                    if (previousState != state)
                    {
                        log($"LOGGER X 初回準備：GetState={state}");
                        previousState = state;
                    }
                    if (state == 3)
                    {
                        _waveLoggerXStartCompleted = true;
                        break;
                    }
                    if (state <= 0 || state == 11)
                        throw new InvalidOperationException(
                            $"LOGGER Xの初回準備に失敗しました（GetState={state}）。");
                    if (timer.Elapsed >= TimeSpan.FromSeconds(10))
                        throw new TimeoutException(
                            $"LOGGER Xがトリガ待ちになりません（GetState={state}）。" +
                            "雛形のマニュアルトリガ設定とLOGGER画面を確認してください。");
                    await Task.Delay(100);
                }
            }

            // 初回準備待ちの間に通電や接続が変わっていないか呼出し元で再確認する。
            beforeTrigger();
            long triggerResult;
            using (ObserveOperation?.Invoke("LOGGER.WaveLog_Trigger"))
                triggerResult = Convert.ToInt64(app.Trigger());
            log($"LOGGER X Trigger：戻り値={triggerResult}");
            // 失敗時にStartやTriggerを自動で再送しない。
            return triggerResult;
        }

        public void Stop()
        {
            using var observation = ObserveOperation?.Invoke("LOGGER.WaveLog_Stop");
            dynamic app = GetApplication();
            app.Stop();
        }

        public string GetLoggerModuleName()
        {
            dynamic app = GetApplication();
            string name = Convert.ToString(app.GetModuleName()) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("LOGGERの実行ファイル名を取得できません。");
            return name;
        }

        private long ReadStateCore()
        {
            using var observation = ObserveOperation?.Invoke("LOGGER.WaveLog_GetState");
            dynamic app = GetApplication();
            return Convert.ToInt64(app.GetState());
        }

        public void LogSessionState(string stage, Action<string> log)
        {
            if (!IsInitialized) { log($"LOGGER接続診断：{stage} / COM接続なし"); return; }
            // 読み取りだけ。雛形なしではGetStateが失敗しても接続失敗にしない。
            string file, state;
            try { file = HasActiveFile() ? "あり" : "なし"; }
            catch (Exception ex) { file = "取得不可 / " + ex.Message; }
            try { state = ReadStateCore().ToString(); }
            catch (Exception ex) { state = "取得不可 / " + ex.Message; }
            log($"LOGGER接続診断：{stage} / 版={ActiveEdition} / アクティブファイル={file} / GetState={state}");
        }

        public string GetSystemErrorMessage()
        {
            if (ActiveEdition != WaveLoggerEdition.WaveLoggerX)
            {
                return string.Empty;
            }

            dynamic app = GetApplication();
            return Convert.ToString(app.GetSystemErrorMessage()) ?? string.Empty;
        }

        private HardwareCheckResult DetectHardwareWithWaveLoggerX(int usbId)
        {
            HardwareCheckResult nr500 = DetectNr500WithWaveLoggerX(usbId);
            if (nr500.IsDetected)
            {
                return nr500;
            }

            HardwareCheckResult nrX =
                DetectNrXWithWaveLoggerX(usbId, NrXConnectionMethod.Usb);
            if (nrX.IsDetected)
            {
                return nrX;
            }

            return new HardwareCheckResult(
                false,
                "未検出",
                "WAVE LOGGER X / WaveLoggerX.Application",
                $"NR-500確認: {nr500.Description} / NR-X確認: {nrX.Description}");
        }

        // 本体確認中の呼び出しだけを観測する。順序・戻り値・例外はそのまま維持する。
        // GetStateは更新遅延があるため、前後の値だけで原因の呼び出しとは断定しない。
        private T ObserveHardwareCheck<T>(string operationName, Func<T> operation)
        {
            LogHardwareCheckState(operationName + " / 呼び出し前");
            var timer = Stopwatch.StartNew();
            try
            {
                T result = operation();
                WriteHardwareCheckLog($"本体確認診断：{operationName} / 戻り値={result}" +
                    $" / 呼び出し時間={timer.Elapsed.TotalMilliseconds:F1} ms");
                return result;
            }
            catch (Exception ex)
            {
                WriteHardwareCheckLog($"本体確認診断：{operationName} / 例外={ex.GetType().Name}" +
                    $" / HRESULT=0x{ex.HResult:X8} / {ex.Message}");
                throw;
            }
            finally
            {
                LogHardwareCheckState(operationName + " / 呼び出し後");
            }
        }

        private void LogHardwareCheckState(string stage)
        {
            Action<string>? log = _connectionDiagnosticLog;
            if (log is null) return;
            try { LogSessionState("本体確認 / " + stage, log); }
            catch { /* 診断ログの失敗で、本来の接続結果や例外を置き換えない。 */ }
        }

        private void WriteHardwareCheckLog(string message)
        {
            try { _connectionDiagnosticLog?.Invoke(message); }
            catch { /* 診断ログの失敗で接続処理を変更しない。 */ }
        }

        private HardwareCheckResult DetectNr500WithWaveLoggerX(int usbId)
        {
            dynamic app = GetApplication();
            _waveLoggerXStartCompleted = false;
            long identifierResult = ObserveHardwareCheck<long>(
                $"NR-500確認 / SetIdentifier({usbId})",
                () => Convert.ToInt64(app.SetIdentifier(usbId)));
            long unitInfo = long.MinValue;

            if (identifierResult == 0)
            {
                unitInfo = ObserveHardwareCheck<long>(
                    "NR-500確認 / GetUnitInfo(0, 0)",
                    () => Convert.ToInt64(app.GetUnitInfo(0, 0)));
            }

            bool detected = unitInfo == WaveLoggerXNr500UnitCode;
            if (detected) _connectedIdentifier = usbId;
            return new HardwareCheckResult(
                detected,
                detected ? "NR-500" : "未検出",
                "WAVE LOGGER X / WaveLoggerX.Application",
                $"SetIdentifier({usbId}) = {identifierResult}, " +
                $"GetUnitInfo(0, 0) = {FormatUnitInfo(unitInfo)}");
        }

        private HardwareCheckResult DetectNrXWithWaveLoggerX(
            int deviceId,
            NrXConnectionMethod connectionMethod)
        {
            dynamic app = GetApplication();

            // 4～7はNR-X USB、8～11はNR-X LAN（Wi-Fiを含む）。
            int identifier = deviceId +
                (connectionMethod == NrXConnectionMethod.Usb ? 4 : 8);
            _waveLoggerXStartCompleted = false;
            long identifierResult = ObserveHardwareCheck<long>(
                $"NR-X確認（{connectionMethod}） / SetIdentifier({identifier})",
                () => Convert.ToInt64(app.SetIdentifier(identifier)));
            long unitInfo = long.MinValue;

            if (identifierResult == 0)
            {
                unitInfo = ObserveHardwareCheck<long>(
                    $"NR-X確認（{connectionMethod}） / GetUnitInfo(0, 0)",
                    () => Convert.ToInt64(app.GetUnitInfo(0, 0)));
            }

            bool detected = unitInfo is
                WaveLoggerXNrX100UnitCode or WaveLoggerXNrXr01UnitCode;
            if (detected) _connectedIdentifier = identifier;
            string hardwareName = unitInfo switch
            {
                WaveLoggerXNrX100UnitCode => "NR-X100",
                WaveLoggerXNrXr01UnitCode => "NR-XR01",
                _ => "未検出"
            };
            string methodName = connectionMethod == NrXConnectionMethod.Usb
                ? "USB"
                : "Wi-Fi/LAN";
            string systemError = Convert.ToString(app.GetSystemErrorMessage()) ?? string.Empty;
            string errorText = detected || string.IsNullOrWhiteSpace(systemError)
                ? string.Empty
                : $" / {systemError}";

            return new HardwareCheckResult(
                detected,
                hardwareName,
                "WAVE LOGGER X / WaveLoggerX.Application",
                $"{methodName}: SetIdentifier({identifier}) = {identifierResult}, " +
                $"GetUnitInfo(0, 0) = {FormatUnitInfo(unitInfo)}{errorText}");
        }

        private HardwareCheckResult ConnectAndDetectNrXWithWaveLoggerX(
            int deviceId,
            int address1,
            int address2,
            int address3,
            int address4,
            int tcpPort)
        {
            dynamic app = GetApplication();
            int identifier = deviceId + 8;
            _waveLoggerXStartCompleted = false;
            long identifierResult = Convert.ToInt64(app.SetIdentifier(identifier));
            long lanConfigResult = long.MinValue;
            long connectResult = long.MinValue;
            bool lanConnected = false;
            long unitInfo = long.MinValue;

            if (identifierResult == 0)
            {
                lanConfigResult = Convert.ToInt64(app.SetLanConfig(
                    deviceId,
                    address1,
                    address2,
                    address3,
                    address4,
                    tcpPort));
            }

            if (lanConfigResult == 0)
            {
                lanConnected = Convert.ToBoolean(app.IsConnectLan());
                connectResult = lanConnected
                    ? 0
                    : Convert.ToInt64(app.ConnectLan());

                if (connectResult == 0 && !lanConnected)
                {
                    for (int retry = 0; retry < 40; retry++)
                    {
                        lanConnected = Convert.ToBoolean(app.IsConnectLan());
                        if (lanConnected)
                        {
                            break;
                        }

                        Thread.Sleep(250);
                    }
                }
            }

            _lanConnectedByAutomation = lanConnected;
            if (lanConnected)
            {
                unitInfo = Convert.ToInt64(app.GetUnitInfo(0, 0));
            }

            bool detected = unitInfo is
                WaveLoggerXNrX100UnitCode or WaveLoggerXNrXr01UnitCode;
            if (detected) _connectedIdentifier = identifier;
            string hardwareName = unitInfo switch
            {
                WaveLoggerXNrX100UnitCode => "NR-X100",
                WaveLoggerXNrXr01UnitCode => "NR-XR01",
                _ => "未検出"
            };
            string systemError = Convert.ToString(app.GetSystemErrorMessage()) ?? string.Empty;
            string errorText = detected || string.IsNullOrWhiteSpace(systemError)
                ? string.Empty
                : $" / {systemError}";
            string ipAddress = $"{address1}.{address2}.{address3}.{address4}:{tcpPort}";

            return new HardwareCheckResult(
                detected,
                hardwareName,
                "WAVE LOGGER X / WaveLoggerX.Application",
                $"Wi-Fi/LAN {ipAddress}: SetIdentifier({identifier}) = {identifierResult}, " +
                $"SetLanConfig({deviceId}) = {lanConfigResult}, " +
                $"ConnectLan = {connectResult}, IsConnectLan = {lanConnected}, " +
                $"GetUnitInfo(0, 0) = {FormatUnitInfo(unitInfo)}{errorText}");
        }

        private void ValidateIpAddressPart(int value, string parameterName)
        {
            if (value is < 0 or > 255)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName, value, "IPアドレスの各値は0～255です。");
            }
        }

        private HardwareCheckResult DetectHardwareWithLegacyLogger(int usbId)
        {
            dynamic app = GetApplication();
            _waveLoggerXStartCompleted = false;
            long identifierResult = Convert.ToInt64(app.SetIdentifier(usbId));

            if (identifierResult != 0)
            {
                return new HardwareCheckResult(
                    false,
                    "未検出",
                    "WAVE LOGGER / WaveLogger2.Application",
                    $"SetIdentifier({usbId}) = {identifierResult}");
            }

            long unitInfo = Convert.ToInt64(app.GetUnitInfo(usbId));
            bool detected = unitInfo >= 0;
            if (detected) _connectedIdentifier = usbId;

            return new HardwareCheckResult(
                detected,
                detected ? "NR-500" : "未検出",
                "WAVE LOGGER / WaveLogger2.Application",
                detected
                    ? $"GetUnitInfo({usbId}) = {unitInfo}"
                    : $"GetUnitInfo({usbId}) = {unitInfo}。WAVE LOGGERはNR-X本体に対応していません。");
        }

        private string FormatUnitInfo(long value)
        {
            return value == long.MinValue ? "未実行" : value.ToString();
        }

        private object GetApplication()
        {
            return _application
                ?? throw new InvalidOperationException(
                    "WAVE LOGGERが初期化されていません。先にInitialize()を実行してください。");
        }

        private (Type Type, WaveLoggerEdition Edition) ResolveServer(
            WaveLoggerEdition edition)
        {
            if (edition == WaveLoggerEdition.Auto)
            {
                Type? xType = Type.GetTypeFromProgID(WaveLoggerXProgId);
                if (xType is not null)
                {
                    return (xType, WaveLoggerEdition.WaveLoggerX);
                }

                Type? legacyType = Type.GetTypeFromProgID(LegacyProgId);
                if (legacyType is not null)
                {
                    return (legacyType, WaveLoggerEdition.Legacy);
                }

                throw new InvalidOperationException(
                    "WAVE LOGGER XおよびWAVE LOGGERのCOMサーバが見つかりません。");
            }

            string progId = edition == WaveLoggerEdition.WaveLoggerX
                ? WaveLoggerXProgId
                : LegacyProgId;

            Type? type = Type.GetTypeFromProgID(progId);
            if (type is null)
            {
                throw new InvalidOperationException(
                    $"COMサーバ '{progId}' が登録されていません。");
            }

            return (type, edition);
        }

        private void ReleaseComObject(object? value)
        {
            if (value is not null && Marshal.IsComObject(value))
            {
                Marshal.FinalReleaseComObject(value);
            }
        }
    }
}
