@echo off
rem MeasurementCheck を実機テスト用 PC に持っていく形(自己完結・単一ファイル、.NET のインストール不要)で bin\publish に発行する。
rem 設定は Properties\PublishProfiles\FolderProfile.pubxml(PSCAutomation の決まり: exe 1 つ + 雛形フォルダー)。
rem 発行後、bin\publish フォルダーごと USB などで実機 PC にコピーし、中の 実機確認.cmd を使う。
rem 実機 PC に必要なもの: NI-VISA(Ivi.Visa.Interop は exe に入っているが VISA 本体は必要)、WAVE LOGGER(X または旧)、Excel。
setlocal
cd /d "%~dp0"
set OUT=%~dp0bin\publish
if exist "%OUT%" rmdir /s /q "%OUT%"
dotnet publish MeasurementCheck.csproj -p:PublishProfile=FolderProfile
if errorlevel 1 (
  echo 発行に失敗しました。
  exit /b 1
)
echo.
echo 発行しました: %OUT%
echo   MeasurementCheck.exe / 雛形\ / 実機確認.cmd が入っています。フォルダーごと実機 PC にコピーしてください。
dir /b "%OUT%"
endlocal