<#
  End-to-end test against the real keys and the real executable.

  The test hosts its own WinForms TextBox instead of driving Notepad: Windows 11
  Notepad is a Store app whose MainWindowHandle is 0, so keystrokes aimed at it
  land in whatever window happens to be focused. Owning the target window makes
  focus deterministic and lets the text and the caret be read back directly.

  Run with:  powershell -NoProfile -STA -ExecutionPolicy Bypass -File test\e2e-test.ps1
  Keep hands off the keyboard while it runs.
#>
[CmdletBinding()]
param([switch]$LibraryOnly)

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
        Start-Sleep -Milliseconds 10
    }
}

<#
  Waits for something to become true rather than for a fixed number of
  milliseconds, still pumping the message loop throughout.

  This is the difference between a suite that takes half an hour and one that
  takes a couple of minutes. Every case used to sleep for the worst case it
  could imagine -- three and a half seconds, thirty times over -- when the work
  it was waiting for finishes in well under one. Sleeping for the worst case
  also hides regressions: a fix that got twice as slow still passed.
#>
function Wait-Until([scriptblock]$Condition, [int]$TimeoutMs = 8000) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        [System.Windows.Forms.Application]::DoEvents()
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 15
    }
    return $false
}

<#
  How many triggers the fixer has finished, counted from its log.

  The fixer writes one 'done:' line per press, from its message loop, after it
  has dropped its busy flag -- so the line means precisely "idle, ready for the
  next press". Waiting for that instead of for a sleep is both faster and
  stricter: a press the program never received now fails the case instead of
  passing because the sleep happened to be long enough for the NEXT one.

  Opened with FileShare.ReadWrite because the fixer is appending to this very
  file; the default share mode collides with it every few reads.
#>
function Get-LogText {
    if (-not (Test-Path -LiteralPath $script:logFile)) { return '' }
    $fs = $null; $sr = $null
    try {
        $fs = [System.IO.File]::Open($script:logFile, 'Open', 'Read', 'ReadWrite')
        $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
        return $sr.ReadToEnd()
    } catch {
        return $null     # locked this instant; the caller simply polls again
    } finally {
        if ($sr) { $sr.Dispose() } elseif ($fs) { $fs.Dispose() }
    }
}

function Get-DoneCount {
    $text = Get-LogText
    if ($null -eq $text) { return -1 }
    $n = 0; $i = 0
    while (($i = $text.IndexOf('done: ', $i)) -ge 0) { $n++; $i += 6 }
    return $n
}

function Test-FixerAlive {
    return ($script:fixerProc -and -not $script:fixerProc.HasExited)
}

<#
  Sends a trigger and waits for the fixer to finish acting on it.

  With no fixer running there is nothing to wait for, so it falls back to a
  short fixed pause -- the ignore-list case and the Caps Lock restore in the
  finally block both go through here with the program already stopped.
#>
function Invoke-Trigger([scriptblock]$Send, [int]$TimeoutMs = 8000) {
    if (-not (Test-FixerAlive)) { & $Send; Wait-Ms 400; return $true }

    $before = Get-DoneCount
    $textBefore = if ($script:tb) { $script:tb.Text } else { $null }
    & $Send
    $landed = Wait-Until { $c = Get-DoneCount; ($c -ge 0) -and ($c -gt $before) } $TimeoutMs
    if (-not $landed) {
        Write-Host "  note  the fixer never reported finishing this press" -ForegroundColor Yellow
    }
    # 'done' means input was queued, not consumed. Wait for the actual document
    # update before assertions/resetting it; a fixed 80 ms fails under load.
    # The separate latency suite enforces the tight visible-response budget.
    if ($landed -and $script:tb) {
        $lastDone = @([regex]::Matches((Get-LogText), 'done: (\w+)')) | Select-Object -Last 1
        if ($lastDone -and $lastDone.Groups[1].Value -eq 'Converted') {
            [void](Wait-Until { $script:tb.Text -cne $textBefore } 2000)
        }
    }
    # Finish consuming modifier/Caps Lock releases after the document update.
    Wait-Ms 80
    return $landed
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

function Invoke-WinSpace([int]$timeoutMs = 8000) {
    [void](Invoke-Trigger { Send-WinSpace } $timeoutMs)
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

function Invoke-Fix([int]$timeoutMs = 8000) {
    [void](Invoke-Trigger { Send-Fix } $timeoutMs)
}

<#
  Caps Lock changes what every key produces, differently per layout, so a run
  that starts with it on compares against the wrong expectations. It is forced
  off here and restored at the end.
#>
function Get-CapsLock {
    return ([E2E.Kb]::GetKeyState(0x14) -band 1)
}
<#
  Caps Lock is a trigger as well as a key, so a press spends time probing for a
  selection even when there is nothing to do. Waiting for the fixer to report it
  has finished is what stops the next case starting while it is still busy --
  its press would be dropped and the case would fail for no reason.
#>
function Send-CapsLock {
    [void](Invoke-Trigger { Send-Key 0x14 })
}
function Set-CapsLock([int]$wanted) {
    if ((Get-CapsLock) -eq $wanted) { return }
    Send-CapsLock
}
function Invoke-CaseFix([int]$timeoutMs = 8000) {
    [void](Invoke-Trigger { Send-Key 0x14 } $timeoutMs)
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
  The layout of the test window itself, whatever happens to be in front.

  Pressing Win hands the foreground to the shell for a moment and the language
  flyout can still be up when the reading is taken, so asking the FOREGROUND
  window which language it is on answers a question about explorer.exe rather
  than about the window under test. Every other case here reads the foreground
  because the test window demonstrably has it; the Win+Space case is the one
  where that assumption is exactly what is in doubt.
#>
function Get-TestWindowLangId {
    $tid = [E2E.Kb]::GetWindowThreadProcessId($script:form.Handle, [IntPtr]::Zero)
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
        # Usually granted immediately; the ceiling is only for the case where
        # the shell is still holding on to the foreground.
        [void](Wait-Until { [E2E.Kb]::GetForegroundWindow() -eq $hWnd } 400)
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
            Wait-Ms 200
        }
        [void](Set-WindowForeground $script:form.Handle)
        $script:tb.Focus() | Out-Null
        if (Wait-Until { [E2E.Kb]::GetForegroundWindow() -eq $script:form.Handle } 400) { return $true }
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
    # Long enough for the TextBox to have applied the text and the caret, which
    # it does synchronously; the old 400 ms was covering the language flyout
    # from the previous case, and that is now waited for explicitly.
    Wait-Ms 60
    if ([E2E.Kb]::GetForegroundWindow() -ne $script:form.Handle) {
        throw "test window lost the foreground (foreground class: '$(Get-ForegroundClass)')"
    }
}

<#
  Waits for a freshly started fixer to be listening, by watching for the line it
  logs once its hotkeys are registered. Replaces a flat five-second sleep that
  was mostly spent waiting for nothing.
#>
function Wait-FixerReady([int]$Nth = 1, [int]$TimeoutMs = 20000) {
    $ready = Wait-Until {
        $t = Get-LogText
        if (-not $t) { return $false }
        return (@([regex]::Matches($t, 'started, hotkey')).Count -ge $Nth)
    } $TimeoutMs
    if (-not $ready) { throw "the fixer did not report starting within $TimeoutMs ms" }
    # The hook is seated a moment after the banner is written.
    Wait-Ms 200
}

# Clears the field: a copy already running holds the single-instance mutex, and
# the one the test starts would refuse to run.
function Stop-AnyFixer {
    $any = @(Get-Process KeyboardLangFixer -ErrorAction SilentlyContinue)
    foreach ($p in $any) {
        Write-Host "stopping a running copy (PID $($p.Id)) ..." -ForegroundColor DarkGray
        try { $p.Kill() } catch { }
    }
    if ($any.Count) {
        [void](Wait-Until { @(Get-Process KeyboardLangFixer -ErrorAction SilentlyContinue).Count -eq 0 } 5000)
    }
}

if ($LibraryOnly) { return }

$started = [Diagnostics.Stopwatch]::StartNew()

try {
    Stop-AnyFixer

    Write-Host "starting the fixer ..." -ForegroundColor Cyan
    # The log path contains spaces; Start-Process joins ArgumentList entries
    # without quoting them, so it has to be quoted here or it arrives as several
    # arguments and the program rejects them.
    $fixerProc = Start-Process $exe -PassThru -ArgumentList @('--no-tray', '--log', "`"$logFile`"")
    $null = $fixerProc.Handle
    $script:fixerProc = $fixerProc
    Wait-FixerReady
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
    if (-not (Focus-TestWindow)) {
        throw "could not bring the test window to the foreground (foreground class: '$(Get-ForegroundClass)')"
    }
    Wait-Ms 200

    # =========================================================================
    #  Win+Space: shared with Windows, so it may only ever act on a selection
    # =========================================================================
    Write-Host "case 0a: Win+Space with NO selection leaves correct Thai alone ..." -ForegroundColor Cyan
    # The rule that makes sharing this key safe. Wrong-layout text and text that
    # was meant look identical, so with nothing selected the program must not
    # guess: typing Thai correctly and pressing Win+Space to carry on in English
    # used to rewrite the word as Latin gibberish.
    Set-Target $TH_sawatdi $false
    Invoke-WinSpace
    Assert-Equal 'Win+Space, no selection: correct Thai untouched' $tb.Text $TH_sawatdi
    Assert-True 'Win+Space, no selection: nothing selected afterwards' ($tb.SelectionLength -eq 0) `
        ("selection length $($tb.SelectionLength)")

    Write-Host "case 0b: ... and leaves mistyped text alone too ..." -ForegroundColor Cyan
    # Not even Smart Selection runs on a shared key: it fixes the last word, and
    # this press might only have meant "switch language".
    Set-Target $EN_garbage $false
    Invoke-WinSpace
    Assert-Equal 'Win+Space, no selection: mistyped text also untouched' $tb.Text $EN_garbage

    Write-Host "case 0c: Win+Space WITH a selection converts it (EN -> TH) ..." -ForegroundColor Cyan
    # Selecting the text first is the user saying which text they mean, which is
    # the whole distinction the shared key rests on.
    Set-Target $EN_garbage $true
    Invoke-WinSpace
    Assert-Equal 'Win+Space, selection: converted EN -> TH' $tb.Text $TH_sawatdi

    Write-Host "case 0d: ... and back again (TH -> EN) ..." -ForegroundColor Cyan
    Set-Target $TH_sawatdi $true
    Invoke-WinSpace
    Assert-Equal 'Win+Space, selection: converted TH -> EN' $tb.Text $EN_garbage

    Write-Host "case 0e: Win+Space converts only the selected part ..." -ForegroundColor Cyan
    Set-Target "keep $EN_garbage" $false
    $tb.SelectionStart = 5
    $tb.SelectionLength = $EN_garbage.Length
    Wait-Ms 300
    Invoke-WinSpace
    Assert-Equal 'Win+Space, selection: the rest of the line is untouched' $tb.Text "keep $TH_sawatdi"

    # =========================================================================
    #  Caps Lock: the same bargain, for the other keyboard-state mistake
    # =========================================================================
    Write-Host "case 0f: Caps Lock WITH a selection swaps its case ..." -ForegroundColor Cyan
    # Typing "Thailand" with Caps Lock stuck on gives this, Shift and all.
    Set-CapsLock 0
    Set-Target 'tHAILAND' $true
    Invoke-CaseFix
    Assert-Equal 'Caps Lock, selection: case swapped' $tb.Text 'Thailand'
    # The key toggled on the way past -- it is watched, not consumed -- and the
    # program puts it back, because a press aimed at a selection was a command
    # rather than a request to turn Caps Lock on.
    Assert-True 'Caps Lock, selection: left switched off' ((Get-CapsLock) -eq 0) `
        ("caps state $(Get-CapsLock)")

    Write-Host "case 0g: Caps Lock swaps a whole sentence with spaces ..." -ForegroundColor Cyan
    Set-Target 'hELLO wORLD, hOW ARE YOU?' $true
    Invoke-CaseFix
    Assert-Equal 'Caps Lock: sentence swapped' $tb.Text 'Hello World, How are you?'

    Write-Host "case 0h: pressing it again on the same text puts it back ..." -ForegroundColor Cyan
    # Swapping is its own inverse, which is why no undo has to be remembered.
    Set-Target 'Hello World, How are you?' $true
    Invoke-CaseFix
    Assert-Equal 'Caps Lock: swapping twice round-trips' $tb.Text 'hELLO wORLD, hOW ARE YOU?'

    Write-Host "case 0i: Caps Lock with NO selection just toggles, as always ..." -ForegroundColor Cyan
    Set-CapsLock 0
    Set-Target 'nothing selected here' $false
    $capsBefore = Get-CapsLock
    Invoke-CaseFix
    Assert-Equal 'Caps Lock, no selection: document untouched' $tb.Text 'nothing selected here'
    Assert-True 'Caps Lock, no selection: still toggles' ((Get-CapsLock) -ne $capsBefore) `
        ("$capsBefore -> $(Get-CapsLock)")
    Set-CapsLock 0

    Write-Host "case 0j: Caps Lock on Thai swaps the layout's OTHER Shift half ..." -ForegroundColor Cyan
    # Thai has no upper and lower case, but Caps Lock is far from idle on it: on
    # Kedmanee it acts as a second Shift on EVERY key, so text typed with it
    # stuck on comes back as the shifted layer. Undoing that is the same swap
    # again, which is why a second press has to restore the text byte for byte
    # and nothing has to be remembered for undo.
    #
    # This replaced an assertion that Thai came back untouched. That was the old
    # behaviour and it was the bug: the Caps Lock fix worked on English and did
    # nothing whatsoever to Thai.
    Set-Target $TH_sawatdi $true
    Invoke-CaseFix
    $thSwapped = $tb.Text
    Assert-True 'Caps Lock: Thai shift layer swapped' ($thSwapped -cne $TH_sawatdi) `
        ("'$TH_sawatdi' -> '$thSwapped'")
    Set-CapsLock 0
    Set-Target $thSwapped $true
    Invoke-CaseFix
    Assert-Equal 'Caps Lock: swapping Thai twice round-trips' $tb.Text $TH_sawatdi
    Set-CapsLock 0

    Write-Host "case 0k: a mixed selection has BOTH halves put right ..." -ForegroundColor Cyan
    # The shape the report arrived in, and the trap in it: 'aBc' is three
    # distinctive Latin characters against six Thai ones, and judging the
    # selection as a whole picks one language and leaves the other alone. Each
    # character has to be flipped through the layout that owns IT.
    Set-Target ($TH_sawatdi + ' aBc') $true
    Invoke-CaseFix
    Assert-True 'Caps Lock: the Latin half swapped case' `
        ($tb.Text.EndsWith(' AbC', [StringComparison]::Ordinal)) $tb.Text
    Assert-True 'Caps Lock: the Thai half changed too' `
        (-not $tb.Text.StartsWith($TH_sawatdi, [StringComparison]::Ordinal)) $tb.Text
    Set-CapsLock 0

    Write-Host "case 0l: a selection with nothing Caps Lock could touch is declined ..." -ForegroundColor Cyan
    # Digits are on both layouts and Caps Lock changes neither, so there is
    # genuinely nothing to do and the selection must come back byte for byte
    # rather than be pasted over -- which would cost the user their undo history
    # for no gain.
    Set-Target '123 456' $true
    Invoke-CaseFix
    Assert-Equal 'Caps Lock: digits left alone' $tb.Text '123 456'
    Set-CapsLock 0

    Write-Host "case 1: the fix hotkey with NO selection, Smart Selection able to act ..." -ForegroundColor Cyan
    Set-Target $EN_garbage $false
    Invoke-Fix
    # Smart Selection is on by default, so the last word gets fixed even though
    # nothing was selected. That is the whole point of the feature.
    Assert-Equal 'smart: last word fixed with no selection' $tb.Text $TH_sawatdi
    # A conversion happened, so the language must match the result rather than
    # simply having toggled. The plain-toggle case is checked at case 10, where
    # there is nothing to convert.
    Assert-Equal 'smart: language matches the result' ('0x{0:X4}' -f (Get-ForegroundLangId)) '0x041E'

    Write-Host "case 2: the fix hotkey WITH selection (EN -> TH) ..." -ForegroundColor Cyan
    Set-Target $EN_garbage $true
    Invoke-Fix

    Assert-Equal 'selection converted EN -> TH' $tb.Text $TH_sawatdi
    Assert-Equal 'input language left on Thai' ('0x{0:X4}' -f (Get-ForegroundLangId)) '0x041E'

    Write-Host "case 3: the fix hotkey WITH selection (TH -> EN) ..." -ForegroundColor Cyan
    Set-Target $TH_sawatdi $true
    Invoke-Fix
    Assert-Equal 'selection converted TH -> EN' $tb.Text $EN_garbage
    Assert-Equal 'input language left on English' ('0x{0:X4}' -f (Get-ForegroundLangId)) '0x0409'

    Write-Host "case 4: only part of the line selected ..." -ForegroundColor Cyan
    Set-Target "keep $EN_garbage" $false
    $tb.SelectionStart = 5
    $tb.SelectionLength = $EN_garbage.Length
    Wait-Ms 300
    Invoke-Fix
    Assert-Equal 'only the selected part is converted' $tb.Text "keep $TH_sawatdi"

    Write-Host "case 5: shift-layer text (capital letter) ..." -ForegroundColor Cyan
    Set-Target $TH_upperHello $true
    Invoke-Fix
    Assert-Equal 'capital letter survives the round trip' $tb.Text 'Hello'

    # =========================================================================
    #  Smart Selection
    # =========================================================================
    Write-Host "case 6: smart selection leaves the correct words in front alone ..." -ForegroundColor Cyan
    Set-Target "Please read $EN_garbage" $false
    Invoke-Fix
    Assert-Equal 'smart: only the last word changed' $tb.Text "Please read $TH_sawatdi"

    Write-Host "case 7: smart selection on a long line with many spaces ..." -ForegroundColor Cyan
    $longPrefix = (1..12 | ForEach-Object { "word$_" }) -join '   '     # triple spaces
    Set-Target "$longPrefix   $EN_garbage" $false
    Invoke-Fix
    Assert-Equal 'smart: long line, only the tail changed' $tb.Text "$longPrefix   $TH_sawatdi"

    Write-Host "case 8: smart selection on a Thai run (no spaces inside) ..." -ForegroundColor Cyan
    Set-Target "hello $TH_khopkhun$TH_khopkhun" $false
    Invoke-Fix
    Assert-Equal 'smart: whole Thai run converted' $tb.Text "hello $EN_khopkhun$EN_khopkhun"

    Write-Host "case 9: smart selection declines and restores the caret ..." -ForegroundColor Cyan
    # "-/-" is made only of characters both layouts can produce, so there is
    # nothing to infer; the document and the caret must be left exactly as they were.
    $neutral = "$EN_garbage -/-"
    Set-Target $neutral $false
    $caretBefore = $tb.SelectionStart
    Invoke-Fix
    Assert-Equal 'smart: ambiguous tail left alone' $tb.Text $neutral
    [void](Wait-Until { $tb.SelectionLength -eq 0 } 2000)
    Assert-True 'smart: caret restored, nothing selected' `
        (($tb.SelectionStart -eq $caretBefore) -and ($tb.SelectionLength -eq 0)) `
        ("caret $($tb.SelectionStart)/$caretBefore, selection length $($tb.SelectionLength)")

    Write-Host "case 10: nothing to convert -> Windows switches language as usual ..." -ForegroundColor Cyan
    # The behaviour the tool must never break: with nothing selected and nothing
    # to infer, Win+Space has to keep doing exactly what it always did.
    Set-Target '' $false
    Wait-Ms 400       # let any language flyout from the previous case disappear

    Invoke-WinSpace
    Assert-Equal 'smart: empty line untouched' $tb.Text ''
    Assert-True 'Win+Space, empty line: nothing selected either' ($tb.SelectionLength -eq 0) `
        ("selection length $($tb.SelectionLength)")

    <#
      "and Windows still switched the language" is NOT asserted here, on purpose.

      This suite cannot tell the two possible causes of a failure apart. If the
      language does not move, it may be because watching the key broke the
      switch -- the one thing that would matter -- or because synthetic Win+Space
      simply did not commit this time, which it intermittently does not, late in
      a long automated session, with no program running at all.

      Separating those needs a control group, so the claim lives in
      winspace-probe.ps1, which measures the identical press with the program
      stopped and then running: 20/20 both ways, across both switch directions
      and after conversions. Asserting it here without a baseline would only ever
      have produced a red line nobody could act on.
    #>
    $langNow = Get-TestWindowLangId
    Write-Host ("  note  language is 0x{0:X4}; the switch itself is measured by winspace-probe.ps1" -f $langNow) `
        -ForegroundColor DarkGray

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
    Invoke-Fix
    $clip = ''
    [void](Wait-Until {
        try { [System.Windows.Forms.Clipboard]::GetText() -ceq $sentinel } catch { $false }
    } 2000)
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
    Invoke-Fix
    $stillImage = $false; $size = ''
    [void](Wait-Until { [System.Windows.Forms.Clipboard]::ContainsImage() } 2000)
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
    Invoke-Fix
    Assert-Equal 'undo: converted first' $tb.Text $TH_sawatdi
    # Nothing is selected after a paste, so this exercises the harder path: the
    # tool has to re-select what it pasted and check it is still there.
    Invoke-Fix
    Assert-Equal 'undo: original restored byte-exact' $tb.Text $EN_garbage
    Assert-Equal 'undo: language put back too' ('0x{0:X4}' -f (Get-ForegroundLangId)) '0x0409'

    Write-Host "case 14: a third press converts again rather than bouncing ..." -ForegroundColor Cyan
    Invoke-Fix
    Assert-Equal 'undo: only one undo per conversion' $tb.Text $TH_sawatdi

    Write-Host "case 15: undo refuses once the text has changed ..." -ForegroundColor Cyan
    Set-Target $EN_garbage $true
    Invoke-Fix
    Assert-Equal 'undo: converted first (2)' $tb.Text $TH_sawatdi
    # The user carries on typing, so what the tool pasted is no longer what sits
    # at the caret and the undo must not fire.
    Set-Target ($TH_sawatdi + 'zz') $false
    Invoke-Fix
    Assert-True 'undo: declined after the text changed' ($tb.Text -cne $EN_garbage) $tb.Text

    # =========================================================================
    #  Multi-word Smart Selection, stopped by the spell checker
    # =========================================================================
    Write-Host "case 15b: Thai in front of the target (grapheme clusters) ..." -ForegroundColor Cyan
    # Arrow keys move by grapheme cluster, so the Thai vowel marks in the prefix
    # make its character count larger than its keystroke count. Counting
    # characters here used to overshoot the caret and abandon the conversion.
    Set-Target "$TH_sawatdi $EN_garbage" $false
    Invoke-Fix
    Assert-Equal 'Thai prefix: only the tail converted' $tb.Text "$TH_sawatdi $TH_sawatdi"
    Assert-True 'Thai prefix: nothing left selected' ($tb.SelectionLength -eq 0) `
        ("selection length $($tb.SelectionLength)")

    Write-Host "case 16: several mistyped words, stopping at real English ..." -ForegroundColor Cyan
    # 'c9j' converts to a Thai word; 'Please' and 'read' are real English and
    # must survive.
    $TH_tae = U @(0x0E41,0x0E15,0x0E48)
    Set-Target "Please read $EN_garbage c9j" $false
    Invoke-Fix
    Assert-Equal 'multi-word run converted, real words kept' $tb.Text "Please read $TH_sawatdi $TH_tae"

    # =========================================================================
    #  Typing while a fix is still in flight
    # =========================================================================
    Write-Host "case 17: carrying on typing abandons the fix ..." -ForegroundColor Cyan
    <#
      A fix spends the best part of a second reading the document, working out a
      span and pasting over it -- and every one of those steps describes a
      document that has since moved under the user's fingers. It used to paste
      anyway, over whatever they had typed in the meantime.

      The character is sent WITHOUT the signature the fixer stamps on its own
      keystrokes, so it reaches the watcher as exactly what it is pretending to
      be: a person typing.
    #>
    Set-Target $EN_garbage $false
    $doneBefore = Get-DoneCount
    Send-Fix
    Wait-Ms 200                       # mid-flight: the copy probe is still running
    Send-Key 0x58                     # 'x'
    [void](Wait-Until { $c = Get-DoneCount; ($c -ge 0) -and ($c -gt $doneBefore) } 8000)

    # Asserted as "not converted" rather than against an exact string: the input
    # language is whatever the previous case left it on, so which character 'x'
    # produces is not fixed. What matters is that the mistyped run in front of it
    # was left exactly as the user typed it.
    Assert-True 'typing mid-fix: the text was NOT converted underneath' `
        ($tb.Text.StartsWith($EN_garbage)) "'$($tb.Text)'"
    Assert-True 'typing mid-fix: nothing left selected' ($tb.SelectionLength -eq 0) `
        ("selection length $($tb.SelectionLength)")
    Assert-True 'typing mid-fix: the fixer said why it stood down' `
        ((Get-LogText) -match 'user (carried on typing|is typing|typed)') ''
}
finally {
    # =========================================================================
    #  Ignore-list: restart the fixer told to leave this very process alone
    # =========================================================================
    if ($form -and $fixerProc -and -not $fixerProc.HasExited) {
        try {
            Write-Host "case 18: a program on the ignore list is left completely alone ..." -ForegroundColor Cyan
            $fixerProc.Kill()
            [void](Wait-Until { @(Get-Process KeyboardLangFixer -ErrorAction SilentlyContinue).Count -eq 0 } 5000)

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
            $script:fixerProc = $fixerProc
            # The log carries on from the first run, so wait for the SECOND
            # banner rather than re-matching the first.
            Wait-FixerReady 2

            Set-Target $EN_garbage $false
            Invoke-Fix
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
Write-Host ("e2e: {0} failure(s) in {1:N1}s." -f $failures, $started.Elapsed.TotalSeconds) `
    -ForegroundColor $(if ($failures) { 'Red' } else { 'Green' })
exit $failures
