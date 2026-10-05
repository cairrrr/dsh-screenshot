<#
  build.ps1 - compile the screenshot tools with the C# compiler that ships
  with the .NET Framework (no .NET SDK, no Visual Studio required).

    ShotCore.cs + ShotMain.cs   -> shot.exe   one-shot command line capture
    ShotCore.cs + ShotDaemon.cs -> shotd.exe  tray daemon owning the hotkeys

  The sources are UTF-8 without BOM, so /codepage:65001 is passed explicitly -
  otherwise the Chinese tray strings are decoded with the ANSI codepage.

  Re-run after editing any .cs file. If shotd.exe is running it is stopped for
  the rebuild and started again afterwards.
#>
[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$csc = @(
    (Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

if (-not $csc) { throw 'csc.exe not found (.NET Framework 4.x is required)' }

$targets = @(
    [pscustomobject]@{ Out = 'shot.exe'; Sources = @('ShotCore.cs', 'ShotMain.cs') }
    [pscustomobject]@{ Out = 'shotd.exe'; Sources = @('ShotCore.cs', 'ShotDaemon.cs') }
)

$refs = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll') |
    ForEach-Object { '/reference:' + $_ }

# stop the daemon so its exe is not locked while it is being replaced
$running = Get-Process -Name 'shotd' -ErrorAction SilentlyContinue
if ($running) {
    "stopping shotd (pid $($running.Id -join ', ')) for rebuild"
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 300
}

$built = @()
foreach ($t in $targets) {
    $out = Join-Path $PSScriptRoot $t.Out
    $sources = $t.Sources | ForEach-Object { Join-Path $PSScriptRoot $_ }

    foreach ($s in $sources) {
        if (-not (Test-Path -LiteralPath $s)) { throw "source not found: $s" }
    }

    if ((Test-Path -LiteralPath $out) -and -not $Force) {
        $newestSource = ($sources | ForEach-Object { Get-Item -LiteralPath $_ } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1).LastWriteTime
        if ((Get-Item -LiteralPath $out).LastWriteTime -ge $newestSource) {
            "up to date: $($t.Out)"
            $built += $out
            continue
        }
    }

    $log = & $csc /nologo /target:winexe /optimize+ /platform:anycpu /codepage:65001 `
        ('/out:' + $out) $refs $sources 2>&1

    if (-not (Test-Path -LiteralPath $out)) {
        ($log | Out-String).Trim() | Write-Host
        throw "compile failed: $($t.Out)"
    }

    "built: {0}  ({1} KB)" -f $t.Out, [math]::Round((Get-Item -LiteralPath $out).Length / 1KB, 1)
    $built += $out
}

if ($running) {
    Start-Process -FilePath (Join-Path $PSScriptRoot 'shotd.exe') | Out-Null
    "restarted shotd"
}
