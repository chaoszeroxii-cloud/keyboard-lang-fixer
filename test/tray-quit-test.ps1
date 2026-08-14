<#
  Checks the parts the main e2e run skips: the tray icon is created without
  killing the message loop, and the quit hotkey actually stops the process.

  Run with:  powershell -NoProfile -ExecutionPolicy Bypass -File test\tray-quit-test.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

Add-Type -Namespace TQ -Name Kb -MemberDefinition @'
[DllImport("user32.dll")]
public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
'@

$KEYUP = 0x0002
$VK_CTRL = 0x11; $VK_ALT = 0x12; $VK_SHIFT = 0x10; $VK_X = 0x58

$root  = Split-Path -Parent $PSScriptRoot
$fixer = Join-Path $root 'KeyboardLangFixer.ps1'
$out   = Join-Path $PSScriptRoot '_tray_out.txt'
$err   = Join-Path $PSScriptRoot '_tray_err.txt'
$out2  = Join-Path $PSScriptRoot '_tray_out2.txt'
$err2  = Join-Path $PSScriptRoot '_tray_err2.txt'

$failures = 0
$proc = $null

try {
    # Any copy already running holds the single-instance mutex, which would make
    # the one this test starts refuse to run. Clear the field first.
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
        Where-Object { $_.CommandLine -like '*-File *KeyboardLangFixer.ps1*' -and
                       $_.CommandLine -notlike '*Get-CimInstance*' } |
        ForEach-Object {
            Write-Host "stopping a running copy (PID $($_.ProcessId)) ..." -ForegroundColor DarkGray
            try { Stop-Process -Id $_.ProcessId -Force } catch { }
        }
    Start-Sleep -Seconds 2

    Write-Host "starting fixer with the tray icon enabled ..." -ForegroundColor Cyan
    $proc = Start-Process powershell.exe -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $out -RedirectStandardError $err -ArgumentList @(
            '-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass',
            '-File', "`"$fixer`"", '-Relaunched'
        )
    Start-Sleep -Seconds 6

    if ($proc.HasExited) {
        $failures++
        Write-Host "  FAIL  process died during startup (exit $($proc.ExitCode))" -ForegroundColor Red
        Get-Content $err -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "        $_" -ForegroundColor Red }
    } else {
        Write-Host "  PASS  survived startup with a tray icon" -ForegroundColor Green
    }

    if (-not $proc.HasExited) {
        Write-Host "starting a second instance, which must refuse to run ..." -ForegroundColor Cyan
        $second = Start-Process powershell.exe -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput $out2 -RedirectStandardError $err2 -ArgumentList @(
                '-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass',
                '-File', "`"$fixer`"", '-Relaunched'
            )
        # Touching .Handle makes Start-Process -PassThru keep the process handle
        # open; without it .ExitCode comes back empty after the process ends.
        $null = $second.Handle
        $exited = $second.WaitForExit(15000)
        $said = if (Test-Path $out2) { (Get-Content $out2 -Raw) } else { '' }

        if ($exited -and $second.ExitCode -eq 2 -and $said -match 'already running') {
            Write-Host "  PASS  second instance refused to start (exit 2)" -ForegroundColor Green
        } else {
            $failures++
            $state = if ($second.HasExited) { "exit code '$($second.ExitCode)', said '$($said.Trim())'" } else { 'still running' }
            Write-Host "  FAIL  second instance should have refused to start ($state)" -ForegroundColor Red
            if (-not $second.HasExited) { try { $second.Kill() } catch { } }
        }
        if ($proc.HasExited) {
            $failures++
            Write-Host "  FAIL  the first instance died when the second one started" -ForegroundColor Red
        } else {
            Write-Host "  PASS  first instance unaffected" -ForegroundColor Green
        }
    }

    if (-not $proc.HasExited) {
        Write-Host "sending the quit hotkey (Ctrl+Alt+Shift+X) ..." -ForegroundColor Cyan
        [TQ.Kb]::keybd_event([byte]$VK_CTRL,  0, 0, [UIntPtr]::Zero)
        [TQ.Kb]::keybd_event([byte]$VK_ALT,   0, 0, [UIntPtr]::Zero)
        [TQ.Kb]::keybd_event([byte]$VK_SHIFT, 0, 0, [UIntPtr]::Zero)
        [TQ.Kb]::keybd_event([byte]$VK_X,     0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
        [TQ.Kb]::keybd_event([byte]$VK_X,     0, $KEYUP, [UIntPtr]::Zero)
        [TQ.Kb]::keybd_event([byte]$VK_SHIFT, 0, $KEYUP, [UIntPtr]::Zero)
        [TQ.Kb]::keybd_event([byte]$VK_ALT,   0, $KEYUP, [UIntPtr]::Zero)
        [TQ.Kb]::keybd_event([byte]$VK_CTRL,  0, $KEYUP, [UIntPtr]::Zero)

        if ($proc.WaitForExit(6000)) {
            Write-Host "  PASS  quit hotkey stopped the process cleanly" -ForegroundColor Green
        } else {
            $failures++
            Write-Host "  FAIL  process still running after the quit hotkey" -ForegroundColor Red
        }
    }

    Write-Host "--- fixer stdout ---" -ForegroundColor DarkGray
    Get-Content $out -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
    $errText = Get-Content $err -ErrorAction SilentlyContinue
    if ($errText) {
        $failures++
        Write-Host "  FAIL  fixer wrote to stderr:" -ForegroundColor Red
        $errText | ForEach-Object { Write-Host "        $_" -ForegroundColor Red }
    }
}
finally {
    if ($proc -and -not $proc.HasExited) { try { $proc.Kill() } catch { } }
    Remove-Item $out, $err, $out2, $err2 -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host ("tray/quit: {0} failure(s)." -f $failures) -ForegroundColor $(if ($failures) { 'Red' } else { 'Green' })
exit $failures
