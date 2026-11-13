# Rebuilds the desktop overview (one row per app) from the STATUS.md files
# that have been pushed to GitHub (origin/main).
#
# Keep this file ASCII only. Windows PowerShell 5.1 reads a script without a
# BOM as the system code page, so Japanese text here could break parsing.
# All Japanese text lives in the *.template.md file next to this script.
# The Japanese explanation is in README.md in this folder.

param(
    # Git directory to read from. Default: the cache repository next to this script.
    [string]$GitDir = (Join-Path $PSScriptRoot 'repo.git'),
    # Commit or tree to read the STATUS.md files from.
    [string]$Ref = 'refs/remotes/origin/main',
    # Folder the overview is written to.
    [string]$OutDir = [Environment]::GetFolderPath('Desktop'),
    # Folder for the log and the time of the last successful fetch.
    [string]$StateDir = $PSScriptRoot,
    # Skip "git fetch" (for testing).
    [switch]$NoFetch
)

$ErrorActionPreference = 'Stop'

# Never open a prompt or a sign-in window from the scheduled task.
$env:GIT_TERMINAL_PROMPT = '0'
$env:GCM_INTERACTIVE = 'never'

$utf8 = New-Object System.Text.UTF8Encoding($false)
$logPath = Join-Path $StateDir 'update.log'
$lastFetchPath = Join-Path $StateDir 'last-fetch.txt'
$separatorPattern = '^\s*\|[\s:\-|]+$'

function Write-Log([string]$message) {
    $line = '{0} {1}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $message
    try {
        $lines = @()
        if (Test-Path -LiteralPath $logPath) {
            $lines = @([IO.File]::ReadAllLines($logPath, $utf8) | Select-Object -Last 300)
        }
        [IO.File]::WriteAllLines($logPath, [string[]]($lines + $line), $utf8)
    } catch {
        # The log is only a convenience. Never fail because of it.
    }
}

function Format-GitArgument([string]$value) {
    if ($value -match '[\s"]') { return '"' + ($value -replace '"', '\"') + '"' }
    return $value
}

# Runs git and returns its output decoded as UTF-8 (PowerShell 5.1 would
# otherwise decode it as the console code page and garble Japanese text).
function Invoke-Git([string[]]$gitArguments, [int]$timeoutMs = 120000) {
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = 'git'
    $startInfo.Arguments = (($gitArguments | ForEach-Object { Format-GitArgument $_ }) -join ' ')
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = $utf8
    $startInfo.StandardErrorEncoding = $utf8

    $process = [System.Diagnostics.Process]::Start($startInfo)
    $outTask = $process.StandardOutput.ReadToEndAsync()
    $errTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($timeoutMs)) {
        try { $process.Kill() } catch { }
        return [pscustomobject]@{ Code = -1; Out = ''; Err = 'timeout' }
    }
    $process.WaitForExit()
    return [pscustomobject]@{ Code = $process.ExitCode; Out = $outTask.Result; Err = $errTask.Result }
}

# Reads the block between the first two "---" lines: key: "value"
function Read-FrontMatter([string]$text) {
    $values = @{}
    $lines = $text.TrimStart([char]0xFEFF) -split "\r?\n"
    if ($lines.Length -lt 2 -or $lines[0].Trim() -ne '---') { return $null }
    for ($i = 1; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        if ($line.Trim() -eq '---') { return $values }
        if ($line -match '^([A-Za-z_]+):\s*(.*)$') {
            $value = $Matches[2].Trim()
            if ($value.Length -ge 2 -and $value.StartsWith('"') -and $value.EndsWith('"')) {
                $value = $value.Substring(1, $value.Length - 2)
            }
            $values[$Matches[1]] = $value
        }
    }
    return $null   # the closing "---" is missing
}

function ConvertTo-Cell([string]$value) {
    return (($value -replace '\r?\n', ' ') -replace '\|', '\|').Trim()
}

function Format-Updated([string]$value) {
    $date = [datetime]::MinValue
    $invariant = [Globalization.CultureInfo]::InvariantCulture
    if ([datetime]::TryParseExact($value, 'yyyy-MM-dd', $invariant, [Globalization.DateTimeStyles]::None, [ref]$date)) {
        return $date.ToString('M/d', $invariant)
    }
    return $value
}

try {
    $template = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.template.md' | Select-Object -First 1
    if (-not $template) { throw "No *.template.md next to the script: $PSScriptRoot" }
    $outPath = Join-Path $OutDir ($template.Name -replace '\.template\.md$', '.md')

    if (-not (Test-Path -LiteralPath $GitDir)) { throw "Git directory not found: $GitDir (run Install-DevStatus.ps1)" }

    # 1. Fetch what other PCs pushed.
    $fetchFailed = $false
    if (-not $NoFetch) {
        $fetch = Invoke-Git @('--git-dir', $GitDir, 'fetch', '--quiet', '--no-tags', 'origin', '+refs/heads/main:refs/remotes/origin/main')
        if ($fetch.Code -eq 0) {
            [IO.File]::WriteAllText($lastFetchPath, (Get-Date).ToString('yyyy-MM-dd HH:mm'), $utf8)
        } else {
            $fetchFailed = $true
            Write-Log ("fetch failed ({0}): {1}" -f $fetch.Code, $fetch.Err.Trim())
        }
    }
    $fetchedText = '-'
    if (Test-Path -LiteralPath $lastFetchPath) { $fetchedText = [IO.File]::ReadAllText($lastFetchPath, $utf8).Trim() }

    # 2. Find the STATUS.md files. Apps/<app>/ wins over specs/<app>/.
    $tree = Invoke-Git @('--git-dir', $GitDir, 'ls-tree', '-r', '--name-only', $Ref)
    if ($tree.Code -ne 0) { throw "ls-tree failed: $($tree.Err.Trim())" }
    $paths = @($tree.Out -split "\r?\n" | Where-Object { $_ -match '^(Apps|specs)/[^/]+/STATUS\.md$' } | Sort-Object)

    $rows = @{}
    foreach ($path in $paths) {
        $show = Invoke-Git @('--git-dir', $GitDir, 'show', ('{0}:{1}' -f $Ref, $path))
        if ($show.Code -ne 0) { Write-Log "show failed: $path"; continue }
        $values = Read-FrontMatter $show.Out
        if (-not $values -or -not $values['app']) { Write-Log "no front matter: $path"; continue }
        $app = $values['app']
        if ($rows.ContainsKey($app) -and $path.StartsWith('specs/')) { continue }
        $rows[$app] = [pscustomobject]@{
            App     = $app
            Updated = [string]$values['updated']
            Line    = '| {0} | {1} | {2} | {3} |' -f (ConvertTo-Cell $app), (ConvertTo-Cell ([string]$values['state'])), (ConvertTo-Cell ([string]$values['next'])), (ConvertTo-Cell (Format-Updated ([string]$values['updated'])))
        }
    }
    # Without this guard, an empty result would turn every old row into a "manual" row.
    if ($rows.Count -eq 0) { throw "No STATUS.md with front matter found in $Ref" }

    $generatedLines = @($rows.Values |
        Sort-Object @{ Expression = 'Updated'; Descending = $true }, @{ Expression = 'App'; Descending = $false } |
        ForEach-Object { $_.Line })

    # 3. Keep rows of apps that are not in this repository (written by hand or by another project).
    $manualLines = @()
    if (Test-Path -LiteralPath $outPath) {
        $old = [IO.File]::ReadAllLines($outPath, $utf8)
        for ($i = 0; $i -lt $old.Length; $i++) {
            $line = $old[$i]
            if ($line -notmatch '^\s*\|') { continue }
            if ($line -match $separatorPattern) { continue }
            if (($i + 1) -lt $old.Length -and $old[$i + 1] -match $separatorPattern) { continue }   # header row
            $name = $line.Trim().Trim('|').Split('|')[0].Trim()
            if ($name -and -not $rows.ContainsKey($name)) { $manualLines += $line }
        }
    }

    # 4. Fill the template.
    $output = New-Object System.Collections.Generic.List[string]
    foreach ($line in [IO.File]::ReadAllLines($template.FullName, $utf8)) {
        if ($line.Contains('{{ROWS}}')) {
            $output.AddRange([string[]]($generatedLines + $manualLines))
        } elseif ($line.Contains('{{FETCH_FAILED}}')) {
            if ($fetchFailed) { $output.Add($line.Replace('{{FETCH_FAILED}}', '')) }
        } else {
            $output.Add($line.Replace('{{FETCHED}}', $fetchedText))
        }
    }
    $newText = ($output -join "`r`n") + "`r`n"

    $oldText = ''
    if (Test-Path -LiteralPath $outPath) { $oldText = [IO.File]::ReadAllText($outPath, $utf8) }
    if ($newText -ne $oldText) {
        $tempPath = $outPath + '.tmp'
        [IO.File]::WriteAllText($tempPath, $newText, $utf8)
        Move-Item -LiteralPath $tempPath -Destination $outPath -Force
    }
    Write-Log ("ok: {0} generated, {1} kept, fetchFailed={2}" -f $generatedLines.Count, $manualLines.Count, $fetchFailed)
    exit 0
} catch {
    Write-Log ("error: {0}" -f $_.Exception.Message)
    Write-Error $_
    exit 1
}
