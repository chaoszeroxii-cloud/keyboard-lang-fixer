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
        [DllImport("user32.dll")]
        public static extern short GetKeyState(int nVirtKey);
    }
}
'@

$KEYUP = 0x0002
$VK_LWIN = 0x5B; $VK_SPACE = 0x20; $VK_ESCAPE = 0x1B
$VK_CONTROL = 0x11; $VK_ALT = 0x12

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

<#
  The gesture that acts on the document: hold Win and tap Space twice. Releasing
  Win between taps also works, but this is what a person actually does and it
  keeps the two triggers close together without depending on Wait-Ms timing.
  One press on its own must never change the text.
#>
function Send-WinSpaceTwice {
    # Plain sleeps between the taps, not Wait-Ms: pumping the message loop
    # stretches 60 ms into several hundred, which pushed the two taps ~674 ms
    # apart and right against the double-press window. Nothing needs pumping
    # here anyway, because the first press returns without touching the window.
    [E2E.Kb]::keybd_event([byte]$VK_LWIN, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    foreach ($tap in 1, 2) {
        [E2E.Kb]::keybd_event([byte]$VK_SPACE, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 45
        [E2E.Kb]::keybd_event([byte]$VK_SPACE, 0, $KEYUP, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 45
    }
    [E2E.Kb]::keybd_event([byte]$VK_LWIN, 0, $KEYUP, [UIntPtr]::Zero)
    Wait-Ms 100
}

<#
  The fix hotkey: a combination the program owns outright, delivered by
  RegisterHotKey. No retry and no tolerance here -- unlike the shared Win+Space
  gesture it replaced, this is expected to arrive every single time, and a
  missed press should fail the run rather than be papered over.
#>
function Send-Fix {
    [E2E.Kb]::keybd_event([byte]$VK_CONTROL, 0, 0, [UIntPtr]::Zero)
    [E2E.Kb]::keybd_event([byte]$VK_ALT,     0, 0, [UIntPtr]::Zero)
    Wait-Ms 40
    [E2E.Kb]::keybd_event([byte]$VK_SPACE,   0, 0, [UIntPtr]::Zero)
    Wait-Ms 60
    [E2E.Kb]::keybd_event([byte]$VK_SPACE,   0, $KEYUP, [UIntPtr]::Zero)
    [E2E.Kb]::keybd_event([byte]$VK_ALT,     0, $KEYUP, [UIntPtr]::Zero)
    [E2E.Kb]::keybd_event([byte]$VK_CONTROL, 0, $KEYUP, [UIntPtr]::Zero)
}

function Invoke-Fix([int]$waitMs = 3500) {
    Send-Fix
    Wait-Ms $waitMs
}

<#
  Caps Lock changes what every key produces, differently per layout, so a run
  that starts with it on compares against the wrong expectations. It is forced
  off here and restored at the end.
#>
function Get-CapsLock {
    return ([E2E.Kb]::GetKeyState(0x14) -band 1)
}
function Set-CapsLock([int]$wanted) {
    if ((Get-CapsLock) -eq $wanted) { return }
    Send-Key 0x14
    Wait-Ms 200
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
$script:logFile = $logFile
if (Test-Path $logFile) { Remove-Item -LiteralPath $logFile -Force }
if (-not (Test-Path -LiteralPath $exe)) {
    throw "$exe is missing - run build.cmd first."
}

# Thai strings, written as code points so this file's encoding cannot break them.
function U { param([int[]]$c) return [string]::Join('', ($c | ForEach-Object { [char]$_ })) }
$TH_sawatdi    = U @(0x0E2A,0x0E27,0x0E31,0x0E2A,0x0E14,0x0E35)   # EN-mode 'l;ylfu'
$TH_upperHello = U @(0x0E47,0x0E33,0x0E2A,0x0E2A,0x0E19)          # TH-mode 'Hello'
$EN_garbage    = 'l;ylfu'
$TH_khopkhun   = U @(0x0E02,0x0E2D,0x0E1A,0x0E04,0x0E38,0x0E13)   # EN-mode '-v[86I'
$EN_khopkhun   = '-v[86I'

$fixerProc = $null
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

# The window and text box the cases drive; filled in once the form is up.
$form = $null
$tb = $null

<#
  Reclaims the foreground in a loop rather than assuming one attempt sticks.
  Pressing Win hands focus to the taskbar for a moment and it can take it back
  again, and a stray app window (a UWP 'ApplicationFrameWindow', say) can be in
  front when the run starts.
#>
function Focus-TestWindow {
    for ($try = 0; $try -lt 8; $try++) {
        $cls = Get-ForegroundClass
        if ($cls -eq 'Shell_TrayWnd' -or $cls -eq 'ApplicationFrameWindow') {
            Send-Key $VK_ESCAPE
            Wait-Ms 250
        }
        [void](Set-WindowForeground $script:form.Handle)
        $script:tb.Focus() | Out-Null
        Wait-Ms 300
        if ([E2E.Kb]::GetForegroundWindow() -eq $script:form.Handle) { return $true }
    }
    return ([E2E.Kb]::GetForegroundWindow() -eq $script:form.Handle)
}

# Defined at file scope so the ignore-list case in the finally block can still
# use it even when the run threw before reaching the main body.
function Set-Target([string]$text, [bool]$selectAll) {
    if (-not (Focus-TestWindow)) {
        throw "test window is not in the foreground (foreground class: '$(Get-ForegroundClass)')"
    }
    $script:tb.Text = $text
    if ($selectAll) { $script:tb.SelectAll() }
    else { $script:tb.SelectionStart = $text.Length; $script:tb.SelectionLength = 0 }
    # Written into the fixer's own log so the two sides line up exactly when a
    # case fails; guessing which trigger belonged to which case wastes time.
    try {
        Add-Content -LiteralPath $script:logFile -Encoding UTF8 `
            -Value ("            >>> target = '" + $text + "' selectAll=" + $selectAll)
    } catch { }
    Wait-Ms 400
    if ([E2E.Kb]::GetForegroundWindow() -ne $script:form.Handle) {
        throw "test window lost the foreground (foreground class: '$(Get-ForegroundClass)')"
    }
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

    $capsAtStart = Get-CapsLock
    if ($capsAtStart -ne 0) { Write-Host "turning Caps Lock off for the run ..." -ForegroundColor DarkGray }
    Set-CapsLock 0

    Write-Host "opening the test window ..." -ForegroundColor Cyan
    $form = New-Object System.Windows.Forms.Form
    $script:form = $form
    $form.Text = 'KeyboardLangFixer e2e target'
    $form.Size = New-Object System.Drawing.Size 620, 150
    $form.TopMost = $true
    $form.StartPosition = 'CenterScreen'
    $tb = New-Object System.Windows.Forms.TextBox
    $script:tb = $tb
    $tb.Font = New-Object System.Drawing.Font 'Segoe UI', 14
    $tb.Dock = 'Fill'
    $form.Controls.Add($tb)
    $form.Show()
    $form.Activate()
    Wait-Ms 400
    if (-not (Focus-TestWindow)) {
        throw "could not bring the test window to the foreground (foreground class: '$(Get-ForegroundClass)')"
    }
    Wait-Ms 600

    # =========================================================================
    #  Explicit selection
    # =========================================================================
    # =========================================================================
    #  One press must never change the document
    # =========================================================================
    Write-Host "case 0a: ONE press leaves correctly typed Thai alone ..." -ForegroundColor Cyan
    # The reason the double press exists. Typing Thai correctly and pressing the
    # hotkey to carry on in English used to rewrite the word as Latin gibberish,
    # because wrong-layout text and intended text look identical.
    Set-Target $TH_sawatdi $false
    Send-WinSpace
    Wait-Ms 3000
    Assert-Equal 'one press: correct Thai untouched' $tb.Text $TH_sawatdi
    Assert-True 'one press: nothing selected afterwards' ($tb.SelectionLength -eq 0) `
        ("selection length $($tb.SelectionLength)")

    Write-Host "case 0b: ONE press leaves mistyped text alone too ..." -ForegroundColor Cyan
    Set-Target $EN_garbage $false
    Send-WinSpace
    Wait-Ms 3000
    Assert-Equal 'one press: mistyped text also untouched' $tb.Text $EN_garbage

    Write-Host "case 1: Win+Space with NO selection and Smart Selection able to act ..." -ForegroundColor Cyan
    Set-Target $EN_garbage $false
    Invoke-Fix 3500
    # Smart Selection is on by default, so the last word gets fixed even though
    # nothing was selected. That is the whole point of the feature.
    Assert-Equal 'smart: last word fixed with no selection' $tb.Text $TH_sawatdi
    # A conversion happened, so the language must match the result rather than
    # simply having toggled. The plain-toggle case is checked at case 10, where
    # there is nothing to convert.
    Assert-Equal 'smart: language matches the result' ('0x{0:X4}' -f (Get-ForegroundLangId)) '0x041E'

    Write-Host "case 2: Win+Space WITH selection (EN -> TH) ..." -ForegroundColor Cyan
    Set-Target $EN_garbage $true
    Invoke-Fix 3500

    Assert-Equal 'selection converted EN -> TH' $tb.Text $TH_sawatdi
    Assert-Equal 'input language left on Thai' ('0x{0:X4}' -f (Get-ForegroundLangId)) '0x041E'

    Write-Host "case 3: Win+Space WITH selection (TH -> EN) ..." -ForegroundColor Cyan
    Set-Target $TH_sawatdi $true
    Invoke-Fix 3500
    Assert-Equal 'selection converted TH -> EN' $tb.Text $EN_garbage
    Assert-Equal 'input language left on English' ('0x{0:X4}' -f (Get-ForegroundLangId)) '0x0409'

    Write-Host "case 4: only part of the line selected ..." -ForegroundColor Cyan
    Set-Target "keep $EN_garbage" $false
    $tb.SelectionStart = 5
    $tb.SelectionLength = $EN_garbage.Length
    Wait-Ms 300
    Invoke-Fix 3500
    Assert-Equal 'only the selected part is converted' $tb.Text "keep $TH_sawatdi"

    Write-Host "case 5: shift-layer text (capital letter) ..." -ForegroundColor Cyan
    Set-Target $TH_upperHello $true
    Invoke-Fix 3500
    Assert-Equal 'capital letter survives the round trip' $tb.Text 'Hello'

    # =========================================================================
    #  Smart Selection
    # =========================================================================
    Write-Host "case 6: smart selection leaves the correct words in front alone ..." -ForegroundColor Cyan
    Set-Target "Please read $EN_garbage" $false
    Invoke-Fix 3500
    Assert-Equal 'smart: only the last word changed' $tb.Text "Please read $TH_sawatdi"

    Write-Host "case 7: smart selection on a long line with many spaces ..." -ForegroundColor Cyan
    $longPrefix = (1..12 | ForEach-Object { "word$_" }) -join '   '     # triple spaces
    Set-Target "$longPrefix   $EN_garbage" $false
    Invoke-Fix 4000
    Assert-Equal 'smart: long line, only the tail changed' $tb.Text "$longPrefix   $TH_sawatdi"

    Write-Host "case 8: smart selection on a Thai run (no spaces inside) ..." -ForegroundColor Cyan
    Set-Target "hello $TH_khopkhun$TH_khopkhun" $false
    Invoke-Fix 3500
    Assert-Equal 'smart: whole Thai run converted' $tb.Text "hello $EN_khopkhun$EN_khopkhun"

    Write-Host "case 9: smart selection declines and restores the caret ..." -ForegroundColor Cyan
    # "-/-" is made only of characters both layouts can produce, so there is
    # nothing to infer; the document and the caret must be left exactly as they were.
    $neutral = "$EN_garbage -/-"
    Set-Target $neutral $false
    $caretBefore = $tb.SelectionStart
    Invoke-Fix 3500
    Assert-Equal 'smart: ambiguous tail left alone' $tb.Text $neutral
    Assert-True 'smart: caret restored, nothing selected' `
        (($tb.SelectionStart -eq $caretBefore) -and ($tb.SelectionLength -eq 0)) `
        ("caret $($tb.SelectionStart)/$caretBefore, selection length $($tb.SelectionLength)")

    Write-Host "case 10: nothing to convert -> Windows switches language as usual ..." -ForegroundColor Cyan
    # The behaviour the tool must never break: with nothing selected and nothing
    # to infer, Win+Space has to keep doing exactly what it always did.
    Set-Target '' $false
    Wait-Ms 1200      # let any language flyout from the previous case disappear

    # Win+Space belongs entirely to Windows now: the program neither hooks it
    # nor reacts to it, so this is a regression guard that it stayed that way.
    $switched = $false
    $trace = ''
    for ($attempt = 1; $attempt -le 3 -and -not $switched; $attempt++) {
        $langBefore = Get-ForegroundLangId
        Send-WinSpace
        Wait-Ms 3000
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
    Invoke-Fix 4000
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
    Invoke-Fix 4500
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

    # =========================================================================
    #  Smart Undo
    # =========================================================================
    Write-Host "case 13: press again straight after -> the original comes back ..." -ForegroundColor Cyan
    Set-Target $EN_garbage $true
    Invoke-Fix 3500
    Assert-Equal 'undo: converted first' $tb.Text $TH_sawatdi
    # Nothing is selected after a paste, so this exercises the harder path: the
    # tool has to re-select what it pasted and check it is still there.
    Invoke-Fix 4000
    Assert-Equal 'undo: original restored byte-exact' $tb.Text $EN_garbage
    Assert-Equal 'undo: language put back too' ('0x{0:X4}' -f (Get-ForegroundLangId)) '0x0409'

    Write-Host "case 14: a third press converts again rather than bouncing ..." -ForegroundColor Cyan
    Invoke-Fix 4000
    Assert-Equal 'undo: only one undo per conversion' $tb.Text $TH_sawatdi

    Write-Host "case 15: undo refuses once the text has changed ..." -ForegroundColor Cyan
    Set-Target $EN_garbage $true
    Invoke-Fix 3500
    Assert-Equal 'undo: converted first (2)' $tb.Text $TH_sawatdi
    # The user carries on typing, so what the tool pasted is no longer what sits
    # at the caret and the undo must not fire.
    Set-Target ($TH_sawatdi + 'zz') $false
    Invoke-Fix 4000
    Assert-True 'undo: declined after the text changed' ($tb.Text -cne $EN_garbage) $tb.Text

    # =========================================================================
    #  Multi-word Smart Selection, stopped by the spell checker
    # =========================================================================
    Write-Host "case 15b: Thai in front of the target (grapheme clusters) ..." -ForegroundColor Cyan
    # Arrow keys move by grapheme cluster, so the Thai vowel marks in the prefix
    # make its character count larger than its keystroke count. Counting
    # characters here used to overshoot the caret and abandon the conversion.
    Set-Target "$TH_sawatdi $EN_garbage" $false
    Invoke-Fix 4000
    Assert-Equal 'Thai prefix: only the tail converted' $tb.Text "$TH_sawatdi $TH_sawatdi"
    Assert-True 'Thai prefix: nothing left selected' ($tb.SelectionLength -eq 0) `
        ("selection length $($tb.SelectionLength)")

    Write-Host "case 16: several mistyped words, stopping at real English ..." -ForegroundColor Cyan
    # 'c9j' converts to a Thai word; 'Please' and 'read' are real English and
    # must survive.
    $TH_tae = U @(0x0E41,0x0E15,0x0E48)
    Set-Target "Please read $EN_garbage c9j" $false
    Invoke-Fix 4500
    Assert-Equal 'multi-word run converted, real words kept' $tb.Text "Please read $TH_sawatdi $TH_tae"
}
finally {
    # =========================================================================
    #  Ignore-list: restart the fixer told to leave this very process alone
    # =========================================================================
    if ($form -and $fixerProc -and -not $fixerProc.HasExited) {
        try {
            Write-Host "case 17: a program on the ignore list is left completely alone ..." -ForegroundColor Cyan
            $fixerProc.Kill()
            Start-Sleep -Seconds 2

            # The test window belongs to this powershell.exe, so naming it is a
            # true end-to-end check of the ignore path.
            $settings = Join-Path $root 'settings.json'
            Set-Content -LiteralPath $settings -Encoding UTF8 -Value @'
{
  "Hotkey": "Ctrl+Alt+Space",
  "IgnoreApps": ["powershell.exe"]
}
'@
            $fixerProc = Start-Process $exe -PassThru -ArgumentList @('--no-tray', '--log', "`"$logFile`"")
            $null = $fixerProc.Handle
            Start-Sleep -Seconds 5

            Set-Target $EN_garbage $false
            Invoke-Fix 4000
            Assert-Equal 'ignore list: text untouched' $tb.Text $EN_garbage
            Assert-True 'ignore list: nothing selected either' ($tb.SelectionLength -eq 0) `
                ("selection length $($tb.SelectionLength)")

            Remove-Item -LiteralPath $settings -Force -ErrorAction SilentlyContinue
        } catch {
            $failures++
            Write-Host "  FAIL  ignore-list case threw: $_" -ForegroundColor Red
            Remove-Item -LiteralPath (Join-Path $root 'settings.json') -Force -ErrorAction SilentlyContinue
        }
    }

    try { if ($capsAtStart -ne $null) { Set-CapsLock $capsAtStart } } catch { }
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
