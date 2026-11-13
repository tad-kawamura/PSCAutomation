# Installs the desktop overview updater on this PC:
#   - copies Update-DevStatus.ps1 and the template to %USERPROFILE%\.pscautomation\dev-status
#   - creates a read-only cache repository there (never touches a working clone)
#   - registers a scheduled task (at logon and every hour) for the current user
#
# Keep this file ASCII only (see Update-DevStatus.ps1). Japanese explanation: README.md.
#
# Usage (from any clone of PSCAutomation):
#   powershell -ExecutionPolicy Bypass -File tools\dev-status\Install-DevStatus.ps1
#   powershell -ExecutionPolicy Bypass -File tools\dev-status\Install-DevStatus.ps1 -Uninstall

param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$taskName = 'PSCAutomation-DevStatus'
# Not under AppData: a packaged app (the Claude desktop app) redirects new files
# under AppData to its own private folder, where Task Scheduler cannot see them.
$installDir = Join-Path $env:USERPROFILE '.pscautomation\dev-status'

if ($Uninstall) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "Scheduled task removed. Files remain in: $installDir"
    return
}

New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Update-DevStatus.ps1') -Destination $installDir -Force
Get-ChildItem -LiteralPath $installDir -Filter '*.template.md' | Remove-Item -Force
Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.template.md' | Copy-Item -Destination $installDir -Force

$gitDir = Join-Path $installDir 'repo.git'
if (-not (Test-Path -LiteralPath $gitDir)) {
    $url = (& git -C $PSScriptRoot remote get-url origin)
    if ($LASTEXITCODE -ne 0 -or -not $url) { throw 'Could not read the URL of "origin" from this clone.' }
    & git init --quiet --bare $gitDir
    if ($LASTEXITCODE -ne 0) { throw 'git init failed.' }
    & git --git-dir $gitDir remote add origin $url.Trim()
    if ($LASTEXITCODE -ne 0) { throw 'git remote add failed.' }
}

$script = Join-Path $installDir 'Update-DevStatus.ps1'
# conhost --headless keeps a console window from flashing every hour.
# -ExecutionPolicy Bypass applies to this one process only; the PC's policy is not changed.
$argument = '--headless powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}"' -f $script
$action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument $argument
$hourly = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Hours 1)
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 5) -MultipleInstances IgnoreNew
$description = 'Rebuilds the desktop overview from the STATUS.md files on GitHub (PSCAutomation tools/dev-status).'

$logonRegistered = $true
try {
    $logon = New-ScheduledTaskTrigger -AtLogOn -User ([Security.Principal.WindowsIdentity]::GetCurrent().Name)
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger @($logon, $hourly) -Settings $settings `
        -Description $description -Force | Out-Null
} catch {
    # A logon trigger can need administrator rights. The hourly trigger with
    # StartWhenAvailable still runs soon after logon or wake-up.
    $logonRegistered = $false
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $hourly -Settings $settings `
        -Description $description -Force | Out-Null
}

Write-Host "Installed to: $installDir"
Write-Host "Scheduled task: $taskName (logon trigger: $logonRegistered, hourly: True)"
Write-Host 'Running the first update...'
& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $script
Write-Host "First update exit code: $LASTEXITCODE (log: $(Join-Path $installDir 'update.log'))"
