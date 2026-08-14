<#
  End-to-end test against the real Win+Space key.

  The test hosts its own WinForms TextBox instead of driving Notepad: Windows 11
  Notepad is a Store app whose MainWindowHandle is 0, so keystrokes aimed at it
  land in whatever window happens to be focused. Owning the target window makes
  focus deterministic and lets the text be read back directly.

  Covers the two behaviours that matter:
    1. nothing selected  -> text untouched, Windows still switches the language
    2. text selected     -> selection converted, in both directions

  Run with:  powershell -NoProfile -STA -ExecutionPolicy Bypass -File test\e2e-test.ps1
  Keep hands off the keyboard while it runs.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type -Namespace E2E -Name Kb -MemberDefinition @'
[DllImport("user32.dll")]
public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
[DllImport("user32.dll")]
public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")]
public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")]
public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);
[DllImport("user32.dll")]
public static extern IntPtr GetKeyboardLayout(uint idThread);
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
[DllImport("user32.dll")]
public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
[DllImport("user32.dll")]
public static extern bool BringWindowToTop(IntPtr hWnd);
[DllImport("kernel32.dll")]
public static extern uint GetCurrentThreadId();
'@

function Get-ForegroundClass {
    $sb = New-Object System.Text.StringBuilder 256
    [void][E2E.Kb]::GetClassName([E2E.Kb]::GetForegroundWindow(), $sb, $sb.Capacity)
    return $sb.ToString()
}

<#
  Windows only lets the current foreground process hand focus around. Attaching
  to the foreground thread's input queue borrows that right for a moment, which
  is the documented way to force focus onto a window we own.
#>
function Set-WindowForeground([IntPtr]$hWnd) {
    for ($i = 0; $i -lt 5; $i++) {
        if ([E2E.Kb]::GetForegroundWindow() -eq $hWnd) { return $true }
        $fgThread = [E2E.Kb]::GetWindowThreadProcessId([E2E.Kb]::GetForegroundWindow(), [IntPtr]::Zero)
        $myThread = [E2E.Kb]::GetCurrentThreadId()
        [void][E2E.Kb]::AttachThreadInput($myThread, $fgThread, $true)
        [void][E2E.Kb]::BringWindowToTop($hWnd)
        [void][E2E.Kb]::SetForegroundWindow($hWnd)
        [void][E2E.Kb]::AttachThreadInput($myThread, $fgThread, $false)
        Wait-Ms 250
    }
    return ([E2E.Kb]::GetForegroundWindow() -eq $hWnd)
}

$KEYUP = 0x0002
$VK_LWIN = 0x5B; $VK_SPACE = 0x20

# Pumps the message loop while waiting, so the test's TextBox keeps handling the
# copy/paste keystrokes the fixer sends it.
function Wait-Ms([int]$ms) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $ms) {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 20
    }
}

function Send-Key([int]$vk) {
    [E2E.Kb]::keybd_event([byte]$vk, 0, 0, [UIntPtr]::Zero)
    Wait-Ms 40
    [E2E.Kb]::keybd_event([byte]$vk, 0, $KEYUP, [UIntPtr]::Zero)
    Wait-Ms 60
}

function Send-WinSpace {
    [E2E.Kb]::keybd_event([byte]$VK_LWIN,  0, 0, [UIntPtr]::Zero)
    [E2E.Kb]::keybd_event([byte]$VK_SPACE, 0, 0, [UIntPtr]::Zero)
    Wait-Ms 60
    [E2E.Kb]::keybd_event([byte]$VK_SPACE, 0, $KEYUP, [UIntPtr]::Zero)
    [E2E.Kb]::keybd_event([byte]$VK_LWIN,  0, $KEYUP, [UIntPtr]::Zero)
}

function Get-ForegroundLangId {
    $tid = [E2E.Kb]::GetWindowThreadProcessId([E2E.Kb]::GetForegroundWindow(), [IntPtr]::Zero)
    return ([int64][E2E.Kb]::GetKeyboardLayout($tid)) -band 0xFFFF
}

$root    = Split-Path -Parent $PSScriptRoot
$fixer   = Join-Path $root 'KeyboardLangFixer.ps1'
$logFile = Join-Path $PSScriptRoot '_e2e.log'
Remove-Item $logFile -ErrorAction SilentlyContinue

$TH_sawatdi = [string]::Join('', (0x0E2A,0x0E27,0x0E31,0x0E2A,0x0E14,0x0E35 | ForEach-Object { [char]$_ }))
$EN_garbage = 'l;ylfu'
# "Hello" as it comes out when typed with the Thai layout active: the capital H
# lands on the shift layer (U+0E47), plain h would be U+0E49.
$TH_upperHello = [string]::Join('', (0x0E47,0x0E33,0x0E2A,0x0E2A,0x0E19 | ForEach-Object { [char]$_ }))

$fixerProc = $null
$form      = $null
$failures  = 0

function Assert-Equal([string]$name, [string]$actual, [string]$expected) {
    # -ceq: PowerShell's default -eq ignores case and would pass 'Hello' for 'hello'.
    if ($actual -ceq $expected) {
        Write-Host ("  PASS  {0,-40} '{1}'" -f $name, $actual) -ForegroundColor Green
    } else {
        $script:failures++
        Write-Host ("  FAIL  {0,-40} got '{1}', expected '{2}'" -f $name, $actual, $expected) -ForegroundColor Red
    }
}

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

    Write-Host "starting fixer (Win+Space, hook mode) ..." -ForegroundColor Cyan
    $fixerProc = Start-Process powershell.exe -PassThru -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass',
        '-File', "`"$fixer`"", '-NoTrayIcon', '-Relaunched', '-LogPath', "`"$logFile`""
    )
    Start-Sleep -Seconds 5
    if ($fixerProc.HasExited) { throw "fixer exited immediately (code $($fixerProc.ExitCode))" }

    Write-Host "opening the test window ..." -ForegroundColor Cyan
    $form = New-Object System.Windows.Forms.Form
    $form.Text = 'KeyboardLangFixer e2e target'
    $form.Size = New-Object System.Drawing.Size 520, 140
    $form.TopMost = $true
    $form.StartPosition = 'CenterScreen'
    $tb = New-Object System.Windows.Forms.TextBox
    $tb.Font = New-Object System.Drawing.Font 'Segoe UI', 16
    $tb.Dock = 'Fill'
    $form.Controls.Add($tb)
    $form.Show()
    $form.Activate()
    Wait-Ms 400
    if (-not (Set-WindowForeground $form.Handle)) {
        throw "could not bring the test window to the foreground (foreground class: '$(Get-ForegroundClass)')"
    }
    $tb.Focus() | Out-Null
    Wait-Ms 800

    function Set-Target([string]$text, [bool]$selectAll) {
        # Pressing Win hands focus to the taskbar for a moment, and it can take
        # it back again after the window has been re-activated, so this reclaims
        # focus in a loop instead of assuming one attempt sticks.
        for ($try = 0; $try -lt 6; $try++) {
            if ((Get-ForegroundClass) -eq 'Shell_TrayWnd') {
                Send-Key 0x1B                      # Escape: dismiss Start / the taskbar
                Wait-Ms 250
            }
            [void](Set-WindowForeground $form.Handle)
            $tb.Focus() | Out-Null
            Wait-Ms 300
            if ([E2E.Kb]::GetForegroundWindow() -eq $form.Handle) { break }
        }
        if ([E2E.Kb]::GetForegroundWindow() -ne $form.Handle) {
            throw "test window is not in the foreground (foreground class: '$(Get-ForegroundClass)')"
        }

        $tb.Text = $text
        if ($selectAll) { $tb.SelectAll() } else { $tb.SelectionStart = $text.Length; $tb.SelectionLength = 0 }
        Wait-Ms 400
        # A stray window stealing focus silently invalidates the whole test.
        if ([E2E.Kb]::GetForegroundWindow() -ne $form.Handle) {
            throw "test window lost the foreground (foreground class: '$(Get-ForegroundClass)')"
        }
    }

    # -- case 1: nothing selected -------------------------------------------
    Write-Host "case 1: Win+Space with NO selection ..." -ForegroundColor Cyan
    Set-Target $EN_garbage $false
    $langBefore = Get-ForegroundLangId
    Send-WinSpace
    Wait-Ms 3000
    $langAfter = Get-ForegroundLangId
    Assert-Equal 'no selection: text is left alone' $tb.Text $EN_garbage
    if ($langBefore -ne $langAfter) {
        Write-Host ("  PASS  {0,-40} 0x{1:X4} -> 0x{2:X4}" -f 'no selection: language still switched', $langBefore, $langAfter) -ForegroundColor Green
    } else {
        $failures++
        Write-Host ("  FAIL  {0,-40} stayed 0x{1:X4}" -f 'no selection: language still switched', $langBefore) -ForegroundColor Red
    }

    # -- case 2: selection, EN -> TH ----------------------------------------
    Write-Host "case 2: Win+Space WITH selection (EN -> TH) ..." -ForegroundColor Cyan
    Set-Target $EN_garbage $true
    Send-WinSpace
    Wait-Ms 3000
    Assert-Equal 'selection converted EN -> TH' $tb.Text $TH_sawatdi
    # The text is Thai now, so typing should continue in Thai.
    Assert-Equal 'input language left on Thai' ('0x{0:X4}' -f (Get-ForegroundLangId)) '0x041E'

    # -- case 3: selection, TH -> EN ----------------------------------------
    Write-Host "case 3: Win+Space WITH selection (TH -> EN) ..." -ForegroundColor Cyan
    Set-Target $TH_sawatdi $true
    Send-WinSpace
    Wait-Ms 3000
    Assert-Equal 'selection converted TH -> EN' $tb.Text $EN_garbage
    Assert-Equal 'input language left on English' ('0x{0:X4}' -f (Get-ForegroundLangId)) '0x0409'

    # -- case 4: partial selection ------------------------------------------
    Write-Host "case 4: Win+Space with only part of the line selected ..." -ForegroundColor Cyan
    Set-Target "keep $EN_garbage" $false
    $tb.SelectionStart = 5
    $tb.SelectionLength = $EN_garbage.Length
    Wait-Ms 300
    Send-WinSpace
    Wait-Ms 3000
    Assert-Equal 'only the selected part is converted' $tb.Text "keep $TH_sawatdi"

    # -- case 5: shift layer through the real hotkey -------------------------
    Write-Host "case 5: Win+Space on shift-layer text (capital letter) ..." -ForegroundColor Cyan
    Set-Target $TH_upperHello $true
    Send-WinSpace
    Wait-Ms 3000
    Assert-Equal 'capital letter survives the round trip' $tb.Text 'Hello'

    # -- case 6: the clipboard must survive a plain language switch ----------
    Write-Host "case 6: clipboard is untouched when nothing is selected ..." -ForegroundColor Cyan
    $sentinel = 'clipboard-sentinel-42'
    Set-Target $EN_garbage $false
    for ($i = 0; $i -lt 10; $i++) {
        try { [System.Windows.Forms.Clipboard]::SetText($sentinel); break } catch { Wait-Ms 50 }
    }
    Wait-Ms 300
    Send-WinSpace
    Wait-Ms 3000
    $clip = ''
    try { if ([System.Windows.Forms.Clipboard]::ContainsText()) { $clip = [System.Windows.Forms.Clipboard]::GetText() } } catch { }
    Assert-Equal 'clipboard preserved on plain switch' $clip $sentinel
}
finally {
    if ($form) { try { $form.Close(); $form.Dispose() } catch { } }
    if ($fixerProc -and -not $fixerProc.HasExited) {
        try { $fixerProc.Kill() } catch { }
    }
    Write-Host ""
    if (Test-Path $logFile) {
        Write-Host "--- fixer log ---" -ForegroundColor DarkGray
        Get-Content $logFile | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
    }
}

Write-Host ""
Write-Host ("e2e: {0} failure(s)." -f $failures) -ForegroundColor $(if ($failures) { 'Red' } else { 'Green' })
exit $failures
