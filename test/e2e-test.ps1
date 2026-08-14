<#
  End-to-end test against the real Win+Space key and the real executable.

  The test hosts its own WinForms TextBox instead of driving Notepad: Windows 11
  Notepad is a Store app whose MainWindowHandle is 0, so keystrokes aimed at it
  land in whatever window happens to be focused. Owning the target window makes
  focus deterministic and lets the text and the caret be read back directly.

  Run with:  powershell -NoProfile -STA -ExecutionPolicy Bypass -File test\e2e-test.ps1
  Keep hands off the keyboard while it runs.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
namespace E2E {
    public static class Kb {
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
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();
    }
}
'@

$KEYUP = 0x0002
$VK_LWIN = 0x5B; $VK_SPACE = 0x20; $VK_ESCAPE = 0x1B

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

<#
  Held long enough to look like a person pressing it. Windows' input switcher is
  driven by the language flyout, and a 60 ms synthetic press was short enough
  that it sometimes ignored the switch entirely -- which shows up as this test
  failing on something Windows does, not something the fixer does.
#>
function Send-WinSpace {
    [E2E.Kb]::keybd_event([byte]$VK_LWIN,  0, 0, [UIntPtr]::Zero)
    Wait-Ms 80
    [E2E.Kb]::keybd_event([byte]$VK_SPACE, 0, 0, [UIntPtr]::Zero)
    Wait-Ms 80
    [E2E.Kb]::keybd_event([byte]$VK_SPACE, 0, $KEYUP, [UIntPtr]::Zero)
    Wait-Ms 120
    [E2E.Kb]::keybd_event([byte]$VK_LWIN,  0, $KEYUP, [UIntPtr]::Zero)
}

function Get-ForegroundClass {
    $sb = New-Object System.Text.StringBuilder 256
    [void][E2E.Kb]::GetClassName([E2E.Kb]::GetForegroundWindow(), $sb, $sb.Capacity)
    return $sb.ToString()
}

function Get-ForegroundLangId {
    $tid = [E2E.Kb]::GetWindowThreadProcessId([E2E.Kb]::GetForegroundWindow(), [IntPtr]::Zero)
    return ([int64][E2E.Kb]::GetKeyboardLayout($tid)) -band 0xFFFF
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

$root    = Split-Path -Parent $PSScriptRoot
$exe     = Join-Path $root 'KeyboardLangFixer.exe'
$logFile = Join-Path $PSScriptRoot '_e2e.log'
if (Test-Path $logFile) { Remove-Item -LiteralPath $logFile -Force }
if (-not (Test-Path -LiteralPath $exe)) {
    throw "$exe is missing - run build.cmd first."
}

# Thai strings, written as code points so this file's encoding cannot break them.
function U { param([int[]]$c) return [string]::Join('', ($c | ForEach-Object { [char]$_ })) }
$TH_sawatdi    = U @(0x0E2A,0x0E27,0x0E31,0x0E2A,0x0E14,0x0E35)   # EN-mode 'l;ylfu'
$TH_upperHello = U @(0x0E47,0x0E33,0x0E2A,0x0E2A,0x0E19)          # TH-mode 'Hello'
$EN_garbage    = 'l;ylfu'

$fixerProc = $null
$form      = $null
$failures  = 0

function Assert-Equal([string]$name, [string]$actual, [string]$expected) {
    # -ceq: PowerShell's default -eq ignores case and would pass 'Hello' for 'hello'.
    if ($actual -ceq $expected) {
        Write-Host ("  PASS  {0,-44} '{1}'" -f $name, $actual) -ForegroundColor Green
    } else {
        $script:failures++
        Write-Host ("  FAIL  {0,-44} got '{1}', expected '{2}'" -f $name, $actual, $expected) -ForegroundColor Red
    }
}

function Assert-True([string]$name, [bool]$cond, [string]$detail) {
    if ($cond) { Write-Host ("  PASS  {0,-44} {1}" -f $name, $detail) -ForegroundColor Green }
    else { $script:failures++; Write-Host ("  FAIL  {0,-44} {1}" -f $name, $detail) -ForegroundColor Red }
}

try {
    # Any copy already running holds the single-instance mutex, which would make
    # the one this test starts refuse to run. Clear the field first.
    Get-Process KeyboardLangFixer -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "stopping a running copy (PID $($_.Id)) ..." -ForegroundColor DarkGray
        try { $_.Kill() } catch { }
    }
    Start-Sleep -Seconds 2

    Write-Host "starting the fixer (Win+Space, hook mode) ..." -ForegroundColor Cyan
    # The log path contains spaces; Start-Process joins ArgumentList entries
    # without quoting them, so it has to be quoted here or it arrives as several
    # arguments and the program rejects them.
    $fixerProc = Start-Process $exe -PassThru -ArgumentList @('--no-tray', '--log', "`"$logFile`"")
    $null = $fixerProc.Handle
    Start-Sleep -Seconds 5
    if ($fixerProc.HasExited) { throw "the fixer exited immediately (code $($fixerProc.ExitCode))" }

    Write-Host "opening the test window ..." -ForegroundColor Cyan
    $form = New-Object System.Windows.Forms.Form
    $form.Text = 'KeyboardLangFixer e2e target'
    $form.Size = New-Object System.Drawing.Size 620, 150
    $form.TopMost = $true
    $form.StartPosition = 'CenterScreen'
    $tb = New-Object System.Windows.Forms.TextBox
    $tb.Font = New-Object System.Drawing.Font 'Segoe UI', 14
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
                Send-Key $VK_ESCAPE
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
        if ([E2E.Kb]::GetForegroundWindow() -ne $form.Handle) {
            throw "test window lost the foreground (foreground class: '$(Get-ForegroundClass)')"
        }
    }

    # =========================================================================
    #  Explicit selection
    # =========================================================================
    Write-Host "case 1: Win+Space with NO selection and Smart Selection able to act ..." -ForegroundColor Cyan
    Set-Target $EN_garbage $false
    Send-WinSpace
    Wait-Ms 3500
    # Smart Selection is on by default, so the last word gets fixed even though
    # nothing was selected. That is the whole point of the feature.
    Assert-Equal 'smart: last word fixed with no selection' $tb.Text $TH_sawatdi
    # A conversion happened, so the language must match the result rather than
    # simply having toggled. The plain-toggle case is checked at case 10, where
    # there is nothing to convert.
    Assert-Equal 'smart: language matches the result' ('0x{0:X4}' -f (Get-ForegroundLangId)) '0x041E'

    Write-Host "case 2: Win+Space WITH selection (EN -> TH) ..." -ForegroundColor Cyan
    Set-Target $EN_garbage $true
    Send-WinSpace
    Wait-Ms 3500
    Assert-Equal 'selection converted EN -> TH' $tb.Text $TH_sawatdi
    Assert-Equal 'input language left on Thai' ('0x{0:X4}' -f (Get-ForegroundLangId)) '0x041E'

    Write-Host "case 3: Win+Space WITH selection (TH -> EN) ..." -ForegroundColor Cyan
    Set-Target $TH_sawatdi $true
    Send-WinSpace
    Wait-Ms 3500
    Assert-Equal 'selection converted TH -> EN' $tb.Text $EN_garbage
    Assert-Equal 'input language left on English' ('0x{0:X4}' -f (Get-ForegroundLangId)) '0x0409'

    Write-Host "case 4: only part of the line selected ..." -ForegroundColor Cyan
    Set-Target "keep $EN_garbage" $false
    $tb.SelectionStart = 5
    $tb.SelectionLength = $EN_garbage.Length
    Wait-Ms 300
    Send-WinSpace
    Wait-Ms 3500
    Assert-Equal 'only the selected part is converted' $tb.Text "keep $TH_sawatdi"

    Write-Host "case 5: shift-layer text (capital letter) ..." -ForegroundColor Cyan
    Set-Target $TH_upperHello $true
    Send-WinSpace
    Wait-Ms 3500
    Assert-Equal 'capital letter survives the round trip' $tb.Text 'Hello'

    # =========================================================================
    #  Smart Selection
    # =========================================================================
    Write-Host "case 6: smart selection leaves the correct words in front alone ..." -ForegroundColor Cyan
    Set-Target "Please read $EN_garbage" $false
    Send-WinSpace
    Wait-Ms 3500
    Assert-Equal 'smart: only the last word changed' $tb.Text "Please read $TH_sawatdi"

    Write-Host "case 7: smart selection on a long line with many spaces ..." -ForegroundColor Cyan
    $longPrefix = (1..12 | ForEach-Object { "word$_" }) -join '   '     # triple spaces
    Set-Target "$longPrefix   $EN_garbage" $false
    Send-WinSpace
    Wait-Ms 4000
    Assert-Equal 'smart: long line, only the tail changed' $tb.Text "$longPrefix   $TH_sawatdi"

    Write-Host "case 8: smart selection on a Thai run (no spaces inside) ..." -ForegroundColor Cyan
    Set-Target "hello $TH_sawatdi$TH_sawatdi" $false
    Send-WinSpace
    Wait-Ms 3500
    Assert-Equal 'smart: whole Thai run converted' $tb.Text "hello $EN_garbage$EN_garbage"

    Write-Host "case 9: smart selection declines and restores the caret ..." -ForegroundColor Cyan
    # "-/-" is made only of characters both layouts can produce, so there is
    # nothing to infer; the document and the caret must be left exactly as they were.
    $neutral = "$EN_garbage -/-"
    Set-Target $neutral $false
    $caretBefore = $tb.SelectionStart
    Send-WinSpace
    Wait-Ms 3500
    Assert-Equal 'smart: ambiguous tail left alone' $tb.Text $neutral
    Assert-True 'smart: caret restored, nothing selected' `
        (($tb.SelectionStart -eq $caretBefore) -and ($tb.SelectionLength -eq 0)) `
        ("caret $($tb.SelectionStart)/$caretBefore, selection length $($tb.SelectionLength)")

    Write-Host "case 10: nothing to convert -> Windows switches language as usual ..." -ForegroundColor Cyan
    # The behaviour the tool must never break: with nothing selected and nothing
    # to infer, Win+Space has to keep doing exactly what it always did.
    Set-Target '' $false
    Wait-Ms 1200      # let any language flyout from the previous case disappear

    # Windows commits its own Win+Space switch through the language flyout, and
    # firing synthetic presses back to back can catch it mid-animation so one
    # press appears to do nothing. Each attempt is a full toggle, so retrying is
    # a fair test of "the key was not swallowed" rather than a way to pass.
    $switched = $false
    $trace = ''
    for ($attempt = 1; $attempt -le 3 -and -not $switched; $attempt++) {
        $langBefore = Get-ForegroundLangId
        Send-WinSpace
        Wait-Ms 3000
        $langAfter = Get-ForegroundLangId
        $trace += ("attempt {0}: 0x{1:X4} -> 0x{2:X4}  " -f $attempt, $langBefore, $langAfter)
        if ($langBefore -ne $langAfter) { $switched = $true } else { Wait-Ms 1500 }
    }
    Assert-Equal 'smart: empty line untouched' $tb.Text ''
    Assert-True 'plain Win+Space still switches language' $switched $trace.Trim()

    # =========================================================================
    #  Clipboard preservation
    # =========================================================================
    Write-Host "case 11: text clipboard survives a conversion ..." -ForegroundColor Cyan
    $sentinel = 'clipboard-sentinel-42'
    Set-Target $EN_garbage $true
    for ($i = 0; $i -lt 10; $i++) {
        try { [System.Windows.Forms.Clipboard]::SetText($sentinel); break } catch { Wait-Ms 50 }
    }
    Wait-Ms 300
    Send-WinSpace
    Wait-Ms 4000
    $clip = ''
    try { if ([System.Windows.Forms.Clipboard]::ContainsText()) { $clip = [System.Windows.Forms.Clipboard]::GetText() } } catch { }
    Assert-Equal 'text clipboard restored after converting' $clip $sentinel
    Assert-Equal 'and the conversion still happened' $tb.Text $TH_sawatdi

    Write-Host "case 12: IMAGE clipboard survives a conversion ..." -ForegroundColor Cyan
    # The old version could only ever restore text, so a copied image was lost.
    $bmp = New-Object System.Drawing.Bitmap 40, 24
    for ($x = 0; $x -lt 40; $x++) { for ($y = 0; $y -lt 24; $y++) { $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(255, 10, 200, 30)) } }
    Set-Target $EN_garbage $true
    for ($i = 0; $i -lt 10; $i++) {
        try { [System.Windows.Forms.Clipboard]::SetImage($bmp); break } catch { Wait-Ms 50 }
    }
    Wait-Ms 400
    $hadImage = $false
    try { $hadImage = [System.Windows.Forms.Clipboard]::ContainsImage() } catch { }
    Assert-True 'image was on the clipboard to begin with' $hadImage ''
    Send-WinSpace
    Wait-Ms 4500
    $stillImage = $false; $size = ''
    try {
        if ([System.Windows.Forms.Clipboard]::ContainsImage()) {
            $img = [System.Windows.Forms.Clipboard]::GetImage()
            if ($img) { $stillImage = $true; $size = "$($img.Width)x$($img.Height)"; $img.Dispose() }
        }
    } catch { }
    Assert-True 'image clipboard restored after converting' $stillImage $size
    Assert-Equal 'and the conversion still happened (image case)' $tb.Text $TH_sawatdi
    $bmp.Dispose()
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
