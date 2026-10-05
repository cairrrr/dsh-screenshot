<#
  install-daemon.ps1 - hand the screenshot hotkeys over to the resident shotd.exe.

  Why a daemon at all: a shortcut hotkey starts a new process on every press, and on
  this machine process creation alone costs 1-3 s. The daemon registers the hotkeys
  itself (RegisterHotKey) and captures in-process, so a press costs ~100 ms.

  Start/autostart strategy, in order:
    1. a per-user ONLOGON scheduled task (started by the Task Scheduler service, so it
       does not die with the shell that created it) - works when schtasks is allowed
    2. fallback: a shortcut in the Startup folder, plus a best-effort immediate start

  Also removes the per-press hotkey shortcuts from the Desktop: they compete with the
  daemon for the very same key combinations.

  -Uninstall removes the task, the Startup shortcut and the Desktop leftovers.

  NOTE: this file must keep its UTF-8 BOM. Windows PowerShell 5.1 reads a BOM-less
  .ps1 as ANSI, which turns the Chinese below into mojibake.
#>
[CmdletBinding()]
param(
    [string]$DesktopDir,
    [string]$TaskName = '截屏服务',
    [string]$StartupName = '截屏服务',
    [switch]$Uninstall,
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'

$tools = $PSScriptRoot
$exe = Join-Path $tools 'shotd.exe'
$root = Split-Path -Parent $tools
$statusFile = Join-Path $root 'shots\daemon.txt'

if (-not $DesktopDir) { $DesktopDir = [Environment]::GetFolderPath('Desktop') }
$startupDir = [Environment]::GetFolderPath('Startup')

$problems = New-Object System.Collections.Generic.List[string]

# Native stderr must not terminate the script: PS 5.1 turns it into a terminating
# error whenever $ErrorActionPreference is 'Stop'.
function Invoke-Native {
    param([string]$File, [string[]]$Arguments)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & $File @Arguments 2>&1
        return [pscustomobject]@{
            Code   = $LASTEXITCODE
            Output = (($out | Out-String).Trim())
        }
    }
    finally { $ErrorActionPreference = $prev }
}

function Stop-Daemon {
    Get-Process -Name 'shotd' -ErrorAction SilentlyContinue | ForEach-Object {
        "stopping shotd (pid $($_.Id))"
        $_ | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 600
}

function Remove-SlowHotkeys {
    $legacy = @(Get-ChildItem -LiteralPath $DesktopDir -Filter '截屏-*.lnk' -ErrorAction SilentlyContinue)
    if ($legacy.Count -eq 0) { 'no per-press hotkey shortcuts on the Desktop'; return }
    foreach ($f in $legacy) {
        try { Remove-Item -LiteralPath $f.FullName -Force; "removed slow hotkey: $($f.Name)" }
        catch { $problems.Add("cannot remove $($f.FullName): $($_.Exception.Message)") }
    }
    Start-Sleep -Milliseconds 1000   # let explorer release those global hotkeys
}

function New-StartupShortcut {
    try {
        if (-not (Test-Path -LiteralPath $startupDir)) { New-Item -ItemType Directory -Force -Path $startupDir | Out-Null }
        $link = Join-Path $startupDir ($StartupName + '.lnk')
        $ws = New-Object -ComObject WScript.Shell
        $sc = $ws.CreateShortcut($link)
        $sc.TargetPath = $exe
        $sc.WorkingDirectory = $tools
        $sc.Description = '截屏服务 - 常驻热键 Ctrl+Alt+S / W / R'
        $sc.WindowStyle = 1
        $sc.Save()
        return $link
    }
    catch {
        $problems.Add("cannot create Startup shortcut: $($_.Exception.Message)")
        return $null
    }
}

function Wait-Status {
    param([int]$Tries = 40)
    for ($i = 0; $i -lt $Tries; $i++) {
        Start-Sleep -Milliseconds 250
        if (Test-Path -LiteralPath $statusFile) {
            try { return @(Get-Content -LiteralPath $statusFile -Encoding UTF8) } catch { }
        }
    }
    return $null
}

# --- uninstall ---------------------------------------------------------------
if ($Uninstall) {
    Stop-Daemon
    $r = Invoke-Native 'schtasks.exe' @('/delete', '/tn', $TaskName, '/f')
    if ($r.Code -eq 0) { "removed task: $TaskName" } else { "no task removed: $($r.Output)" }
    $link = Join-Path $startupDir ($StartupName + '.lnk')
    if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link -Force; "removed startup shortcut" }
    Remove-SlowHotkeys
    'uninstalled'
    return
}

if (-not (Test-Path -LiteralPath $exe)) { throw "not built yet: $exe  (run build.ps1 first)" }

# --- 1) stop what is running -------------------------------------------------
Stop-Daemon

# --- 2) drop the slow per-press hotkey shortcuts -----------------------------
Remove-SlowHotkeys

# --- 3) autostart mechanism --------------------------------------------------
$taskReady = $false
$r = Invoke-Native 'schtasks.exe' @('/create', '/tn', $TaskName, '/tr', ('"' + $exe + '"'), '/sc', 'onlogon', '/f')
if ($r.Code -eq 0) {
    $taskReady = $true
    "autostart task ready: $TaskName"
}
else {
    # Not fatal: creating a logon task needs privileges this account may not have.
    # The Startup folder gives the same autostart, just a little less control.
    $first = ($r.Output -split "`r?`n" | Where-Object { $_ -match '\S' } | Select-Object -First 1)
    "schtasks not usable ($($r.Code)): $first"
    $link = New-StartupShortcut
    if ($link) { "autostart via Startup folder: $link  (starts at next logon)" }
    else { $problems.Add('no autostart mechanism could be installed') }
}

# --- 4) start it and verify which hotkeys were won ---------------------------
if ($NoStart) {
    'skipping start (-NoStart)'
}
else {
    $attempt = 0
    while ($true) {
        $attempt++
        Remove-Item -LiteralPath $statusFile -Force -ErrorAction SilentlyContinue

        $started = $false
        if ($taskReady) {
            $run = Invoke-Native 'schtasks.exe' @('/run', '/tn', $TaskName)
            $started = ($run.Code -eq 0)
            if (-not $started) { $problems.Add("schtasks /run failed: $($run.Output)") }
        }
        else {
            try { Start-Process -FilePath $exe | Out-Null; $started = $true }
            catch { $problems.Add("cannot start daemon: $($_.Exception.Message)") }
        }
        if (-not $started) { break }

        $status = Wait-Status
        if (-not $status) {
            $problems.Add('daemon did not write shots\daemon.txt - it did not start or died immediately')
            break
        }

        $okCount = @($status | Where-Object { $_ -match 'registered=True' }).Count
        if ($okCount -eq 3 -or $attempt -ge 3) {
            ''
            $status | ForEach-Object { '  ' + $_ }
            ''
            if ($okCount -eq 3) { 'all 3 hotkeys registered - ready' }
            else { $problems.Add("only $okCount/3 hotkeys registered after $attempt attempts") }
            break
        }

        "attempt $attempt : only $okCount/3 registered, retrying"
        Stop-Daemon
    }
}

if ($problems.Count -gt 0) {
    ''
    'PROBLEMS:'
    $problems | ForEach-Object { '  - ' + $_ }
    exit 1
}
