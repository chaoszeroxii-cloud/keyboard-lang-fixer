<#
  Verifies the two things a user actually needs to trust:

    1. the chosen hotkey survives a restart (settings.json)
    2. uninstalling really removes everything the tool created

  It installs, checks, uninstalls, then sweeps every place the tool could have
  left something behind. Finally it re-installs, so running the test leaves the
  machine the way it found it.

  Run with:  powershell -NoProfile -ExecutionPolicy Bypass -File test\install-test.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$root        = Split-Path -Parent $PSScriptRoot
$installer   = Join-Path $root 'Install.ps1'
$target      = Join-Path $env:LOCALAPPDATA 'KeyboardLangFixer'
$startupDir  = [Environment]::GetFolderPath('Startup')
$startupLink = Join-Path $startupDir 'Keyboard Language Fixer.lnk'

$failures = 0
$started = [Diagnostics.Stopwatch]::StartNew()

# Waits for a condition instead of sleeping for the worst case it could take.
function Wait-Until([scriptblock]$Condition, [int]$TimeoutMs = 15000) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 50
    }
    return $false
}

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { Write-Host ("  PASS  {0,-46} {1}" -f $name, $detail) -ForegroundColor Green }
    else { $script:failures++; Write-Host ("  FAIL  {0,-46} {1}" -f $name, $detail) -ForegroundColor Red }
}

function Get-RunningCount {
    return @(Get-Process KeyboardLangFixer -ErrorAction SilentlyContinue).Count
}

function Invoke-Installer([string[]]$extra) {
    $psArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$installer`"", '-Quiet') + $extra
    $p = Start-Process powershell.exe -PassThru -WindowStyle Hidden -ArgumentList $psArgs
    $null = $p.Handle
    [void]$p.WaitForExit(90000)
    return $p.ExitCode
}

Write-Host '=== install ===' -ForegroundColor Cyan
$code = Invoke-Installer @()
Check 'installer exited cleanly' ($code -eq 0) "exit $code"
Check 'program folder created' (Test-Path -LiteralPath $target) $target
Check 'executable copied' (Test-Path -LiteralPath (Join-Path $target 'KeyboardLangFixer.exe'))
Check 'icon copied' (Test-Path -LiteralPath (Join-Path $target 'icon.ico'))
Check 'uninstaller copied' (Test-Path -LiteralPath (Join-Path $target 'Uninstall.cmd'))
Check 'startup shortcut created' (Test-Path -LiteralPath $startupLink)
Check 'running after install' ((Get-RunningCount) -eq 1) "$(Get-RunningCount) instance(s)"

Write-Host '=== the saved hotkey survives a restart ===' -ForegroundColor Cyan
# Simulates what a reboot does: the settings file is all that carries over.
$settingsPath = Join-Path $target 'settings.json'
@{ Hotkey = 'Ctrl+Alt+K' } | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding UTF8

Get-Process KeyboardLangFixer -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
[void](Wait-Until { @(Get-Process KeyboardLangFixer -ErrorAction SilentlyContinue).Count -eq 0 } 5000)

$out = Join-Path $PSScriptRoot '_install_out.txt'
$p = Start-Process (Join-Path $target 'KeyboardLangFixer.exe') -PassThru `
    -RedirectStandardOutput $out -ArgumentList '--no-tray'
# 'Quit:' is the last line of the banner, so it means the whole thing -- the
# hotkey line included -- has been written and is safe to read.
[void](Wait-Until {
    $p.HasExited -or
    ((Test-Path $out) -and ((Get-Content $out -Raw -ErrorAction SilentlyContinue) -match 'Quit:'))
} 20000)
$banner = if (Test-Path $out) { Get-Content $out -Raw } else { '' }
Check 'restarted copy uses the saved hotkey' ($banner -match 'Ctrl\+Alt\+K') ($banner -split "`n" | Select-Object -First 1)
if (-not $p.HasExited) { $p.Kill() }
[void](Wait-Until { $p.HasExited } 5000)
Remove-Item $out, $settingsPath -ErrorAction SilentlyContinue

Write-Host '=== uninstall ===' -ForegroundColor Cyan
$code = Invoke-Installer @('-Uninstall')
Check 'uninstaller exited cleanly' ($code -eq 0) "exit $code"
Check 'program folder deleted' (-not (Test-Path -LiteralPath $target)) $target
Check 'startup shortcut deleted' (-not (Test-Path -LiteralPath $startupLink))
Check 'nothing left running' ((Get-RunningCount) -eq 0) "$(Get-RunningCount) instance(s)"

Write-Host '=== sweep for leftovers ===' -ForegroundColor Cyan
$strays = @()
$strays += @(Get-ChildItem -LiteralPath $startupDir -Filter '*Keyboard*' -ErrorAction SilentlyContinue |
             ForEach-Object { $_.FullName })
$strays += @(Get-ChildItem -LiteralPath $env:LOCALAPPDATA -Filter '*KeyboardLangFixer*' -ErrorAction SilentlyContinue |
             ForEach-Object { $_.FullName })
$strays += @(Get-ChildItem -LiteralPath $env:APPDATA -Filter '*KeyboardLangFixer*' -ErrorAction SilentlyContinue |
             ForEach-Object { $_.FullName })
Check 'no files left in Startup / AppData' ($strays.Count -eq 0) ($strays -join '; ')

# The tool never writes to the registry; prove it rather than claim it.
$runKeys = @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
             'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run')
$regHits = @()
foreach ($k in $runKeys) {
    if (-not (Test-Path $k)) { continue }
    $props = Get-ItemProperty -Path $k
    foreach ($n in $props.PSObject.Properties.Name) {
        if ($n -like 'PS*') { continue }
        if ("$n $($props.$n)" -like '*KeyboardLangFixer*' -or "$n" -like '*Keyboard Language Fixer*') {
            $regHits += "$k\$n"
        }
    }
}
Check 'no Run-key registry entries' ($regHits.Count -eq 0) ($regHits -join '; ')
Check 'no dedicated registry key' (-not (Test-Path 'HKCU:\Software\KeyboardLangFixer'))

Write-Host '=== put it back ===' -ForegroundColor Cyan
$code = Invoke-Installer @()
Check 'reinstalled for continued use' (($code -eq 0) -and (Test-Path -LiteralPath $target) -and ((Get-RunningCount) -eq 1))

Write-Host ''
Write-Host ("install: {0} failure(s) in {1:N1}s." -f $failures, $started.Elapsed.TotalSeconds) `
    -ForegroundColor $(if ($failures) { 'Red' } else { 'Green' })
exit $failures
