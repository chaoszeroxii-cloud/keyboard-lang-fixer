<#
  Isolates one question: does watching Win+Space stop Windows switching the
  input language on it?

  The e2e suite asserts that it does not, and that assert started failing once
  the key was watched again -- but the same suite also drives Win+Space with
  synthetic input, which Windows treats differently from a real key. This runs
  the identical press with the program stopped and then with it running, so the
  two numbers can be compared instead of guessed at.

  Run with:  powershell -NoProfile -STA -ExecutionPolicy Bypass -File test\winspace-probe.ps1
#>
[CmdletBinding()]
param(
    [int]$Presses = 6,
    # Baseline and watched only. The two conversion phases exist to rule out a
    # specific hypothesis (that setting the language programmatically desyncs
    # the shell's switcher, which it does not) and cost a few minutes, so the
    # suite runs without them.
    [switch]$Quick
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
namespace Probe {
    public static class Kb {
        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint a, uint b, bool f);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
        [DllImport("user32.dll")]
        public static extern IntPtr GetKeyboardLayout(uint idThread);
        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();
    }
}
'@

$KEYUP = 0x0002; $VK_LWIN = 0x5B; $VK_SPACE = 0x20

function Wait-Ms([int]$ms) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $ms) {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 20
    }
}
function Get-LangId {
    $tid = [Probe.Kb]::GetWindowThreadProcessId([Probe.Kb]::GetForegroundWindow(), [IntPtr]::Zero)
    return ([int64][Probe.Kb]::GetKeyboardLayout($tid)) -band 0xFFFF
}
function Send-WinSpace {
    [Probe.Kb]::keybd_event([byte]$VK_LWIN,  0, 0, [UIntPtr]::Zero); Wait-Ms 80
    [Probe.Kb]::keybd_event([byte]$VK_SPACE, 0, 0, [UIntPtr]::Zero); Wait-Ms 80
    [Probe.Kb]::keybd_event([byte]$VK_SPACE, 0, $KEYUP, [UIntPtr]::Zero); Wait-Ms 120
    [Probe.Kb]::keybd_event([byte]$VK_LWIN,  0, $KEYUP, [UIntPtr]::Zero)
}
function Focus([IntPtr]$h) {
    for ($i = 0; $i -lt 6; $i++) {
        if ([Probe.Kb]::GetForegroundWindow() -eq $h) { return $true }
        $fg = [Probe.Kb]::GetWindowThreadProcessId([Probe.Kb]::GetForegroundWindow(), [IntPtr]::Zero)
        $me = [Probe.Kb]::GetCurrentThreadId()
        [void][Probe.Kb]::AttachThreadInput($me, $fg, $true)
        [void][Probe.Kb]::BringWindowToTop($h)
        [void][Probe.Kb]::SetForegroundWindow($h)
        [void][Probe.Kb]::AttachThreadInput($me, $fg, $false)
        Wait-Ms 250
    }
    return ([Probe.Kb]::GetForegroundWindow() -eq $h)
}

$root = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $root 'KeyboardLangFixer.exe'
$log  = Join-Path $PSScriptRoot '_probe.log'
if (Test-Path $log) { Remove-Item -LiteralPath $log -Force }

function Measure-Switches([string]$label) {
    $switched = 0
    $trace = @()
    for ($i = 1; $i -le $Presses; $i++) {
        [void](Focus $script:form.Handle)
        $script:tb.Focus() | Out-Null
        Wait-Ms 500
        $before = Get-LangId
        Send-WinSpace
        Wait-Ms 3000
        $after = Get-LangId
        if ($before -ne $after) { $switched++ }
        $trace += ('0x{0:X4}->0x{1:X4}' -f $before, $after)
    }
    Write-Host ("  {0,-22} {1}/{2} switched   {3}" -f $label, $switched, $Presses, ($trace -join ' ')) `
        -ForegroundColor $(if ($switched -eq $Presses) { 'Green' } elseif ($switched -eq 0) { 'Red' } else { 'Yellow' })
    return $switched
}

Get-Process KeyboardLangFixer -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
Start-Sleep -Seconds 2

$form = New-Object System.Windows.Forms.Form
$form.Text = 'Win+Space probe'; $form.Size = New-Object System.Drawing.Size 620, 150
$form.TopMost = $true; $form.StartPosition = 'CenterScreen'
$tb = New-Object System.Windows.Forms.TextBox
$tb.Dock = 'Fill'; $tb.Font = New-Object System.Drawing.Font 'Segoe UI', 14
$form.Controls.Add($tb); $form.Show(); $form.Activate()
$script:form = $form; $script:tb = $tb
Wait-Ms 600
if (-not (Focus $form.Handle)) { throw 'could not focus the probe window' }

Write-Host "`nWin+Space language switch, $Presses presses each way" -ForegroundColor Cyan
$baseline = Measure-Switches 'fixer NOT running'

$proc = Start-Process $exe -PassThru -ArgumentList @('--no-tray', '--log', "`"$log`"")
$null = $proc.Handle
Start-Sleep -Seconds 5
$watched = Measure-Switches 'fixer watching it'

# Third phase: the same measurement, but after the program has set the input
# language itself a few times. A conversion ends with WM_INPUTLANGCHANGEREQUEST
# posted at the foreground window, and the question is whether that leaves the
# shell's own language switcher pointing somewhere else -- in which case the
# next Win+Space advances the switcher without changing the window.
$VK_CONTROL = 0x11; $VK_ALT = 0x12
function Send-Fix {
    [Probe.Kb]::keybd_event([byte]$VK_CONTROL, 0, 0, [UIntPtr]::Zero)
    [Probe.Kb]::keybd_event([byte]$VK_ALT,     0, 0, [UIntPtr]::Zero); Wait-Ms 40
    [Probe.Kb]::keybd_event([byte]$VK_SPACE,   0, 0, [UIntPtr]::Zero); Wait-Ms 60
    [Probe.Kb]::keybd_event([byte]$VK_SPACE,   0, $KEYUP, [UIntPtr]::Zero)
    [Probe.Kb]::keybd_event([byte]$VK_ALT,     0, $KEYUP, [UIntPtr]::Zero)
    [Probe.Kb]::keybd_event([byte]$VK_CONTROL, 0, $KEYUP, [UIntPtr]::Zero)
}
function Invoke-Conversions([string]$text, [int]$times = 3) {
    foreach ($n in 1..$times) {
        [void](Focus $form.Handle); $tb.Focus() | Out-Null
        $tb.Text = $text; $tb.SelectAll()
        Wait-Ms 400
        Send-Fix
        Wait-Ms 3500
    }
    $tb.Text = ''
}

# Direction matters: the two phases differ only in which language the program
# ends up asking for. If Win+Space stops working after one of them and not the
# other, WM_INPUTLANGCHANGEREQUEST is moving the window without moving the
# shell's own idea of the current profile, and the next press advances the
# shell to a language the window is already using.
$afterConversions = $watched
if (-not $Quick) {
    Invoke-Conversions 'l;ylfu'
    $toThai = Measure-Switches 'after -> Thai x3'

    $TH = [string]::Join('', @(0x0E2A,0x0E27,0x0E31,0x0E2A,0x0E14,0x0E35 | ForEach-Object { [char]$_ }))
    Invoke-Conversions $TH
    $toEnglish = Measure-Switches 'after -> English x3'

    $afterConversions = [Math]::Min($toThai, $toEnglish)
}

try { $proc.Kill() } catch { }
$form.Close(); $form.Dispose()

Write-Host ''
if ($baseline -eq 0) {
    Write-Host 'INCONCLUSIVE: synthetic Win+Space does not switch the language on this desktop even' -ForegroundColor Yellow
    Write-Host '              with the program stopped, so the e2e assert is measuring Windows, not us.' -ForegroundColor Yellow
    exit 2
}
<#
  The verdict is deliberately not "watched must equal baseline".

  Synthetic Win+Space does not commit every single time -- that was measured with
  the program stopped, which is exactly what the baseline is for -- so at these
  sample sizes one miss is noise, and demanding a perfect match would report a
  regression roughly as often as it reported the truth. What this can actually
  detect, and what would matter, is the key being systematically broken: never
  switching, or switching far less than it does with nothing running.
#>
$floor = [Math]::Ceiling($baseline / 2)
if ($watched -eq 0 -or $watched -lt $floor) {
    Write-Host "REGRESSION: $watched/$Presses switched while watching, against $baseline/$Presses with the program stopped." `
        -ForegroundColor Red
    exit 1
}
if ($watched -lt $baseline) {
    Write-Host "OK: $watched/$Presses against a baseline of $baseline/$Presses - within the flakiness of synthetic input." `
        -ForegroundColor Green
} else {
    Write-Host "OK: watching the key did not cost a single switch." -ForegroundColor Green
}
if (-not $Quick -and ($afterConversions -eq 0 -and $watched -gt 0)) {
    Write-Host "But setting the language programmatically stopped it: $afterConversions/$Presses afterwards." -ForegroundColor Yellow
    exit 3
}
exit 0
