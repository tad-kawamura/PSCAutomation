using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PSCAutomation.Wrappers.WaveLogger.Automation;

public sealed partial class WaveLoggerSession
{
        public bool IsSessionClosed()
        {
            dynamic app = GetApplication();
            // X版では画面の×でVisibleがtrueのまま、機器IDが255になる。
            // 起動直後の未選択状態と区別し、本体確認済みの接続だけを監視する。
            return !Convert.ToBoolean(app.Visible) ||
                (_connectedIdentifier is not null && ActiveEdition == WaveLoggerEdition.WaveLoggerX &&
                 Convert.ToInt64(app.GetIdentifier()) == 255);
        }

        // トリガ待ちも収集動作中。画面を閉じても残るAutomation Serverを
        // 停止確認 → Quit → COM解放の順で終了する。失敗時は参照を保持し、
        // 新しいサーバーを重ねて起動しない。
        public async Task ShutdownAsync(Action<string> log)
        {
            if (_application is null)
            {
                await CompleteProcessShutdownAsync(log);
                return;
            }
            dynamic? app = GetApplication();
            _waveLoggerXStartCompleted = false;
            bool hardwareResponsive = true;
            try
            {
                LogSessionState("終了処理開始", log);
                long identifier = Convert.ToInt64(app.GetIdentifier());
                log($"LOGGER終了：GetIdentifier={identifier} / 確認済みID={_connectedIdentifier}");
                if (_connectedIdentifier is int expected && identifier != expected)
                {
                    long restore = Convert.ToInt64(app.SetIdentifier(expected));
                    log($"LOGGER終了：接続先復元 SetIdentifier({expected})={restore}");
                    if (restore != 0)
                        throw new InvalidOperationException($"LOGGERの停止先IDを復元できません（戻り値 {restore}）。");
                }
                if (_connectedIdentifier is not null)
                {
                    long unit = GetUnitInfo(0);
                    hardwareResponsive = unit > 0;
                    log($"LOGGER終了：停止先との通信確認 GetUnitInfo={unit}");
                    if (unit <= 0)
                        log("LOGGER終了：本体通信不可のため、本体の停止は未確認です。LOGGER側の終了を確認します。");
                    await Task.Delay(300);
                }
                app.Stop();
                log("LOGGER終了：Stop送信済み。停止を確認します。");
                var timer = Stopwatch.StartNew();
                long? previous = null;
                // 状態更新は約200ms間隔。Stop直前のエラー停止等を使わない。
                await Task.Delay(300);
                while (true)
                {
                    long state = Convert.ToInt64(app.GetState());
                    if (state != previous) log($"LOGGER終了：GetState={state}");
                    previous = state;
                    // 11は収集エラーが残る状態で、次回Startまで解除されない。
                    // 停止命令後に待ち、Quitのファイル数・可視状態でも終了を確認する。
                    if (state is 1 or 11) break;
                    if (state <= 0)
                        throw new InvalidOperationException($"LOGGERの停止を確認できません（GetState={state}）。");
                    if (timer.Elapsed >= TimeSpan.FromSeconds(10))
                        throw new TimeoutException($"LOGGERの停止が完了しません（GetState={state}）。旧接続を保持します。LOGGER画面を確認してください。");
                    await Task.Delay(100);
                }
                app.Quit();
                timer.Restart();
                while (true)
                {
                    long count = Convert.ToInt64(app.GetFileCount());
                    bool visible = Convert.ToBoolean(app.Visible);
                    log($"LOGGER終了：Quit送信後 / ファイル数={count} / Visible={visible}");
                    if (count == 0 && !visible) break;
                    if (timer.Elapsed >= TimeSpan.FromSeconds(10))
                        throw new TimeoutException("LOGGERの終了を確認できません。旧接続を保持します。");
                    await Task.Delay(300);
                }
                // QuitだけではAutomationオブジェクトが存在し、機器IDも残り得る。
                if (ActiveEdition == WaveLoggerEdition.WaveLoggerX)
                {
                    // 接続方式を255へ変える前にLANを切断する。
                    if (_lanConnectedByAutomation)
                    {
                        if (Convert.ToBoolean(app.IsConnectLan()))
                        {
                            long disconnect = Convert.ToInt64(app.DisconnectLan());
                            log($"LOGGER終了：DisconnectLan={disconnect}");
                            if (disconnect != 0)
                                throw new InvalidOperationException($"LOGGERのLAN切断に失敗しました（戻り値 {disconnect}）。");
                        }
                        _lanConnectedByAutomation = false;
                    }
                    long result = Convert.ToInt64(app.SetIdentifier(255));
                    log($"LOGGER終了：SetIdentifier(255)={result}");
                    if (result != 0)
                        throw new InvalidOperationException($"LOGGERの機器IDを解除できません（戻り値 {result}）。");
                }
                _pendingTerminationAllowed = hardwareResponsive;
                DetachShutdownProcess();
                Release();
                app = null;
                log("LOGGER終了：COM解放完了。");
            }
            catch (COMException ex) when (ex.HResult is unchecked((int)0x80010108) or
                unchecked((int)0x800706BA) or unchecked((int)0x80010007))
            {
                // サーバー自体が終了済みのCOM参照だけを破棄する。
                log($"LOGGER終了：COMサーバー終了済み / HRESULT=0x{ex.HResult:X8}");
                // COM切断だけではStop/Quitの成功を証明できない。強制終了しない。
                _pendingTerminationAllowed = false;
                DetachShutdownProcess();
                Release();
                app = null;
            }
            await CompleteProcessShutdownAsync(log);
        }

        private void DetachShutdownProcess()
        {
            _pendingProcess = _serverProcess;
            _serverProcess = null;
        }

        private async Task CompleteProcessShutdownAsync(Action<string> log)
        {
            if (_pendingProcess is null) return;
            await _pendingProcess.CompleteAsync(_pendingTerminationAllowed, log);
            _pendingProcess.Dispose();
            _pendingProcess = null;
            _pendingTerminationAllowed = false;
        }

}
