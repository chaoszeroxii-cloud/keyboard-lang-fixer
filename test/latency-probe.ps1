<#
  Stage-by-stage latency of a fix on a real selection, from key release.

    powershell -NoProfile -ExecutionPolicy Bypass -File test\latency-probe.ps1
        [-Samples 10] [-Targets textbox,richtextbox,edge,chrome]
        [-Triggers ctrlaltspace,winspace] [-Executable path\to\KeyboardLangFixer.exe]
        [-FixerArgs --no-type,--no-direct]

  Unlike selection-latency.ps1, the target pumps its messages as they arrive
  (Application.Run, or a real Chromium window), so the numbers are the fixer's
  and the target's, not a polling loop's. Keep hands off the keyboard.
#>
[CmdletBinding()]
param(
    [int]$Samples = 10,
    [string]$Targets = 'textbox,richtextbox,edge',
    [string]$Triggers = 'ctrlaltspace,winspace',
    [string]$Executable,
    [string]$Out,
    # Extra fixer switches, e.g. '--no-type,--no-direct', to compare paths. A
    # string rather than an array: powershell -File cannot pass an array.
    [string]$FixerArgs = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Executable) { $Executable = & (Join-Path $root 'tools\Build.ps1') -Quiet }
if (-not $Out) { $Out = Join-Path $PSScriptRoot '_latency-probe.json' }
$log = Join-Path $PSScriptRoot '_latency-probe.log'
$probe = Join-Path $PSScriptRoot '_latency-probe.exe'
$page = Join-Path $PSScriptRoot 'latency-page.html'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:exe /optimize+ "/out:$probe" /reference:System.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll (Join-Path $PSScriptRoot 'latency-probe.cs')
if ($LASTEXITCODE -ne 0) { throw 'probe build failed' }

$browser = $null
if ($Targets -match 'edge') { $browser = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe" }
if ($Targets -match 'chrome') { $browser = "$env:ProgramFiles\Google\Chrome\Application\chrome.exe" }

# The single-instance mutex means a running copy must go; it is put back after.
$running = @(Get-Process KeyboardLangFixer -ErrorAction SilentlyContinue | ForEach-Object { $_.Path } | Sort-Object -Unique)
Get-Process KeyboardLangFixer -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit() }

if (Test-Path $log) { Remove-Item -LiteralPath $log -Force }
$fixer = Start-Process -FilePath $Executable -PassThru -ArgumentList (@('--no-tray', '--log', "`"$log`"") + @($FixerArgs -split '[ ,]+' | Where-Object { $_ }))
try {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (-not ((Test-Path $log) -and ((Get-Content -LiteralPath $log -Raw) -match 'started, hotkey'))) {
        if ($sw.ElapsedMilliseconds -gt 20000) { throw 'fixer did not start' }
        Start-Sleep -Milliseconds 100
    }
    Start-Sleep -Milliseconds 300
    $probeArgs = @('--log', $log, '--targets', $Targets, '--triggers', $Triggers,
                   '--samples', $Samples, '--page', $page, '--out', $Out)
    if ($browser) { $probeArgs += @('--browser', $browser) }
    & $probe @probeArgs
    $code = $LASTEXITCODE
} finally {
    if (-not $fixer.HasExited) { $fixer.Kill(); $fixer.WaitForExit() }
    Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
    # The throwaway browser profile is ~350 MB. Its processes were killed by
    # the probe, but can take a moment to let go of their files.
    foreach ($dir in @(Get-ChildItem -LiteralPath $PSScriptRoot -Directory -Filter '_browser-profile-*')) {
        for ($i = 0; $i -lt 10 -and (Test-Path -LiteralPath $dir.FullName); $i++) {
            Remove-Item -LiteralPath $dir.FullName -Recurse -Force -ErrorAction SilentlyContinue
            if (Test-Path -LiteralPath $dir.FullName) { Start-Sleep -Milliseconds 300 }
        }
    }
    foreach ($p in $running) { if ($p) { Start-Process -FilePath $p } }
}
exit $code
