<#
  install.ps1 - install (or remove) the dsh-shot-button GUI plugin.

  Preferred path is the documented one:
      dsh plugin --profile web add link:<this folder>
  which forwards to pnpm and adds the package to dsh.profile.bundles.

  pnpm has to carry a path containing non-ASCII characters here, so if that fails the
  script falls back to doing the same two things by hand:
      1. a directory junction inside the profile's node_modules
      2. the bundles + dependencies entries in the profile's package.json

  After installing you must restart the DSH web server and refresh the browser.
  Node caches ES modules, so a running server will not pick the plugin up by itself.

  Usage:
    powershell -ExecutionPolicy Bypass -File install.ps1
    powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall
    powershell -ExecutionPolicy Bypass -File install.ps1 -Manual
#>
[CmdletBinding()]
param(
    [string]$Profile = 'web',
    [string]$PluginName = 'dsh-shot-button',
    [switch]$Uninstall,
    [switch]$Manual
)

$ErrorActionPreference = 'Stop'

$pluginDir = $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $pluginDir 'package.json'))) {
    throw "package.json not found next to this script ($pluginDir)"
}

$profilesRoot = if ($env:DSH_HOME) { Join-Path $env:DSH_HOME 'profiles' } else { Join-Path $env:USERPROFILE '.dsh\profiles' }
$profileDir = Join-Path $profilesRoot $Profile
$manifestPath = Join-Path $profileDir 'package.json'

if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "profile not found: $manifestPath"
}

$problems = New-Object System.Collections.Generic.List[string]

function Invoke-Native {
    param([string]$File, [string[]]$Arguments)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & $File @Arguments 2>&1
        return [pscustomobject]@{ Code = $LASTEXITCODE; Output = (($out | Out-String).Trim()) }
    }
    finally { $ErrorActionPreference = $prev }
}

function Read-Manifest {
    return Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Get-Bundles {
    param($Manifest)
    if ($Manifest.dsh -and $Manifest.dsh.profile -and $Manifest.dsh.profile.bundles) {
        return @($Manifest.dsh.profile.bundles)
    }
    return @()
}

function Test-Installed {
    $m = Read-Manifest
    return ((Get-Bundles $m) -contains $PluginName)
}

# --- manual fallback ---------------------------------------------------------

function Install-Manual {
    $nmDir = Join-Path $profileDir 'node_modules'
    $linkPath = Join-Path $nmDir $PluginName

    if (-not (Test-Path -LiteralPath $nmDir)) { New-Item -ItemType Directory -Force -Path $nmDir | Out-Null }

    if (Test-Path -LiteralPath $linkPath) {
        "manual: link already present ($linkPath)"
    }
    else {
        New-Item -ItemType Junction -Path $linkPath -Target $pluginDir | Out-Null
        "manual: junction created -> $pluginDir"
    }

    $m = Read-Manifest
    $bundles = @(Get-Bundles $m)
    if ($bundles -notcontains $PluginName) { $bundles += $PluginName }

    $deps = [ordered]@{}
    if ($m.dependencies) {
        foreach ($p in $m.dependencies.PSObject.Properties) { $deps[$p.Name] = $p.Value }
    }
    $deps[$PluginName] = 'link:' + $pluginDir

    $m.dsh.profile.bundles = $bundles
    $m.dependencies = $deps

    ($m | ConvertTo-Json -Depth 12) + "`n" | Set-Content -LiteralPath $manifestPath -Encoding UTF8 -NoNewline
    "manual: manifest updated (bundles + dependencies)"
}

function Uninstall-Manual {
    $linkPath = Join-Path (Join-Path $profileDir 'node_modules') $PluginName
    if (Test-Path -LiteralPath $linkPath) {
        # a junction must be removed with -Force and without recursing into the target
        [void](cmd /c rmdir "`"$linkPath`"" 2>&1)
        "manual: link removed"
    }

    $m = Read-Manifest
    $bundles = @(Get-Bundles $m) | Where-Object { $_ -ne $PluginName }
    $m.dsh.profile.bundles = @($bundles)

    $deps = [ordered]@{}
    if ($m.dependencies) {
        foreach ($p in $m.dependencies.PSObject.Properties) {
            if ($p.Name -ne $PluginName) { $deps[$p.Name] = $p.Value }
        }
    }
    $m.dependencies = $deps

    ($m | ConvertTo-Json -Depth 12) + "`n" | Set-Content -LiteralPath $manifestPath -Encoding UTF8 -NoNewline
    "manual: manifest cleaned"
}

# --- uninstall ---------------------------------------------------------------

if ($Uninstall) {
    if (-not $Manual) {
        $r = Invoke-Native 'dsh' @('plugin', '--profile', $Profile, 'remove', $PluginName)
        "dsh plugin remove -> exit $($r.Code)"
        if ($r.Code -ne 0) { "  $($r.Output)" }
    }
    if ($Manual -or -not (Test-Path -LiteralPath (Join-Path (Join-Path $profileDir 'node_modules') $PluginName))) {
        Uninstall-Manual
    }
    if (Test-Installed) { $problems.Add('still listed in dsh.profile.bundles') }
    else { 'uninstalled' }
}

# --- install -----------------------------------------------------------------

else {
    if (Test-Installed) {
        "already installed: $PluginName"
    }
    elseif ($Manual) {
        Install-Manual
    }
    else {
        $r = Invoke-Native 'dsh' @('plugin', '--profile', $Profile, 'add', ('link:' + $pluginDir))
        "dsh plugin add -> exit $($r.Code)"
        if ($r.Output) { ($r.Output -split "`r?`n" | Select-Object -First 6) | ForEach-Object { '  ' + $_ } }

        if (-not (Test-Installed)) {
            'dsh plugin add did not register the bundle; using the manual fallback'
            Install-Manual
        }
    }

    if (Test-Installed) {
        $m = Read-Manifest
        ''
        "bundles now: $(@(Get-Bundles $m) -join ', ')"
    }
    else {
        $problems.Add('installation could not be verified')
    }

    ''
    'NEXT: restart the DSH web server, then refresh the browser (F5).'
    '      Node caches ES modules, so the running server will not pick this up by itself.'
}

if ($problems.Count -gt 0) {
    ''
    'PROBLEMS:'
    $problems | ForEach-Object { '  - ' + $_ }
    exit 1
}
