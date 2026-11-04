@echo off
rem 測定シーケンスの実機確認(docs/測定シーケンス実機確認.md の手順)をまとめたバッチ。
rem 発行した exe(発行.cmd → bin\publish)の隣に置いて使う。リポジトリ内で使えば dotnet run で動く。
rem
rem   実機確認 logger                      1. LOGGER だけ実機(電流は模擬)。5・10mA の 2 本、Excel なし
rem   実機確認 logger-again                1-2. 同じ条件でもう 1 回(同名 xdt が _2 になるか、上書き確認画面が出ないか)
rem   実機確認 real  <SN> [備忘録]         2. 全部実機。5・10mA の 2 本、Excel 表示
rem   実機確認 full  <SN> [備忘録]         3. 全部実機。7〜30mA 1mA 刻み(24 本、約 17 分)
rem   実機確認 cancel <SN> [備忘録]        記録中(20 秒後)に中断して後始末(LOGGER 停止・出力 OFF)を確かめる
rem   実機確認 sim                         機器なしの模擬(exe が動くかの確認)
rem 共通: 保存先 %OUT%(既定 C:\AS110Noise\確認)。環境変数 OUT で変更可。
rem        LOGGER の版を指定するときは環境変数 LOGGER=x または legacy(既定 legacy = 旧 WAVE LOGGER。実機 PC は NR-500 を旧 LOGGER で使う)。NR-500 の USB 機器 ID は USBID(既定 0)。
setlocal
cd /d "%~dp0"
if "%OUT%"=="" set OUT=C:\AS110Noise\確認
if "%LOGGER%"=="" set LOGGER=legacy
if "%USBID%"=="" set USBID=0
set COMMON=--out "%OUT%" --logger %LOGGER% --usb-id %USBID%

if exist "%~dp0MeasurementCheck.exe" (
  set RUN="%~dp0MeasurementCheck.exe"
) else (
  set RUN=dotnet run --project "%~dp0MeasurementCheck.csproj" --
)

set MODE=%~1
set SN=%~2
set MEMO=%~3
if "%MEMO%"=="" set MEMO=実機確認

if "%MODE%"=="sim"          goto sim
if "%MODE%"=="logger"       goto logger
if "%MODE%"=="logger-again" goto logger
if "%MODE%"=="real"         goto real
if "%MODE%"=="full"         goto full
if "%MODE%"=="cancel"       goto cancel
echo 使い方: 実機確認 sim ^| logger ^| logger-again ^| real ^<SN^> [備忘録] ^| full ^<SN^> [備忘録] ^| cancel ^<SN^> [備忘録]
echo   保存先は環境変数 OUT(既定 C:\AS110Noise\確認)、LOGGER の版は LOGGER=x/legacy、USB ID は USBID。
exit /b 2

:sim
%RUN% --sim --no-excel
goto end

:logger
echo === LOGGER だけ実機: 5・10mA(電流は模擬)。ほかの LOGGER 画面は閉じておくこと ===
%RUN% --logger-only --start 5 --step 5 --end 10 --no-excel %COMMON%
goto end

:real
if "%SN%"=="" goto nosn
echo === 全部実機: SN %SN% / 備忘録 %MEMO% / 5・10mA ===
%RUN% --real --sn %SN% --memo %MEMO% --start 5 --step 5 --end 10 --visible %COMMON%
goto end

:full
if "%SN%"=="" goto nosn
echo === 全部実機(通し): SN %SN% / 備忘録 %MEMO% / 7〜30mA 1mA 刻み 24 本(約 17 分) ===
%RUN% --real --sn %SN% --memo %MEMO% --start 7 --step 1 --end 30 --visible %COMMON%
goto end

:cancel
if "%SN%"=="" goto nosn
echo === 全部実機: 記録開始 20 秒後に中断 → LOGGER 停止・WF1973 出力 OFF を確かめる ===
%RUN% --real --sn %SN% --memo %MEMO% --start 5 --step 5 --end 5 --no-excel --cancel-after 20 %COMMON%
goto end

:nosn
echo SN を指定してください。例: 実機確認 %MODE% SN60226 実機確認
exit /b 2

:end
echo.
echo 終了コード %ERRORLEVEL%(0 なら正常)
endlocal
