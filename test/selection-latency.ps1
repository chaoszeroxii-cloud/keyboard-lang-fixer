<#
  Real hotkey -> real TextBox replacement and ready-for-next-press timings.
  Run on an idle desktop: powershell -NoProfile -STA -ExecutionPolicy Bypass
    -File test\selection-latency.ps1 [-Baseline] [-Samples 5]
  Baseline reports old-build timings without enforcing the latency budget.
#>
[CmdletBinding()]
param([switch]$Baseline, [int]$Samples = 5, [string]$Executable)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'e2e-test.ps1') -LibraryOnly
if ($Executable) { $exe = $Executable }
Add-Type -ReferencedAssemblies System.Windows.Forms -TypeDefinition @'
using System.Windows.Forms;
using System.Runtime.InteropServices;
public class CopyTarget : TextBox {
    public bool IgnoreInsert;
    public bool IgnoreCopy;
    public int PasteDelay;
    public string PasteTrace;
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) {
        if (IgnoreInsert && keyData == (Keys.Control | Keys.Insert)) return true;
        if (IgnoreCopy && keyData == (Keys.Control | Keys.C)) return true;
        if (PasteDelay > 0 && keyData == (Keys.Control | Keys.V))
            System.Threading.Thread.Sleep(PasteDelay);
        bool paste = keyData == (Keys.Control | Keys.V);
        if (paste) PasteTrace = "paste seq=" + GetClipboardSequenceNumber() +
            " hasText=" + IsClipboardFormatAvailable(13) + " selection=" + SelectionLength;
        bool handled = base.ProcessCmdKey(ref msg, keyData);
        if (paste) PasteTrace += " result='" + Text + "'";
        return handled;
    }
}
'@
$rows = @()
$clipboardBefore = New-Object Windows.Forms.DataObject
$source = [Windows.Forms.Clipboard]::GetDataObject()
if ($source) {
    foreach ($format in $source.GetFormats($false)) {
        try { $clipboardBefore.SetData($format, $false, $source.GetData($format, $false)) } catch { }
    }
}
$source = $null
try {
    Stop-AnyFixer
    $fixerProc = Start-Process $exe -WindowStyle Hidden -PassThru -ArgumentList @('--no-tray','--log',"`"$logFile`"")
    $script:fixerProc = $fixerProc
    Wait-FixerReady
    $capsAtStart = Get-CapsLock
    Set-CapsLock 0
    $form = New-Object Windows.Forms.Form
    $form.Text = 'KeyboardLangFixer latency regression'
    $form.TopMost = $true
    $tb = New-Object CopyTarget
    $tb.Dock = 'Fill'
    $form.Controls.Add($tb)
    $form.Show()
    $form.Activate()
    foreach ($kind in @('CtrlAltSpace','WinSpace','CtrlC-only')) {
        $tb.IgnoreInsert = $kind -eq 'CtrlC-only'
        for ($i = 0; $i -lt $Samples; $i++) {
            Set-Target $EN_garbage $true
            $tb.PasteTrace = ''
            [Windows.Forms.Clipboard]::SetText('latency-test-original')
            $before = Get-DoneCount
            if ($kind -eq 'WinSpace') { Send-WinSpace } else { Send-Fix }
            $sw = [Diagnostics.Stopwatch]::StartNew()
            while ($tb.Text -cne $TH_sawatdi -and $sw.ElapsedMilliseconds -lt 4000) {
                [Windows.Forms.Application]::DoEvents()
                [Threading.Thread]::Sleep(1)
            }
            $visible = $sw.ElapsedMilliseconds
            Assert-Equal "$kind replacement" $tb.Text $TH_sawatdi
            if ($tb.Text -cne $TH_sawatdi) { Write-Host "  paste diagnostic: $($tb.PasteTrace)" }
            [void](Wait-Until { (Get-DoneCount) -gt $before } 5000)
            $ready = $sw.ElapsedMilliseconds
            $rows += [pscustomobject]@{ Trigger=$kind; Sample=$i+1; VisibleMs=$visible; ReadyMs=$ready }
            if (-not $Baseline) {
                Assert-True "$kind visible within 150 ms" ($visible -lt 150) "$visible ms"
                Assert-True "$kind ready within 200 ms" ($ready -lt 200) "$ready ms"
            }
            Assert-True 'original clipboard eventually restored' `
                (Wait-Until { [Windows.Forms.Clipboard]::GetText() -ceq 'latency-test-original' } 2000) ''
            Assert-Equal 'input language remains Thai after shell settles' ('{0:X4}' -f (Get-TestWindowLangId)) '041E'
        }
    }
    # A slow consumer must still paste the conversion, never the saved clipboard.
    $tb.IgnoreInsert = $false
    $tb.PasteDelay = 400
    Set-Target $EN_garbage $true
    [Windows.Forms.Clipboard]::SetText('must-not-be-pasted')
    Invoke-Fix
    Assert-Equal 'delayed paste uses converted text' $tb.Text $TH_sawatdi
    Assert-True 'delayed paste restores clipboard' `
        (Wait-Until { [Windows.Forms.Clipboard]::GetText() -ceq 'must-not-be-pasted' } 2000) ''
    $tb.PasteDelay = 0
    if (-not $Baseline) {
        $tb.IgnoreCopy = $true;
        Set-Target ('Please read ' + $EN_garbage) $false
        Invoke-Fix
        Assert-Equal 'Smart Selection reuses supported copy shortcut' $tb.Text ('Please read ' + $TH_sawatdi)
        $tb.IgnoreCopy = $false;

        Set-Target $EN_garbage $true
        [Windows.Forms.Clipboard]::SetText('rapid-fixes-original')
        Invoke-Fix
        $tb.SelectAll()
        Invoke-Fix
        Assert-Equal 'immediate second press converts back' $tb.Text $EN_garbage
        Assert-True 'rapid fixes preserve ORIGINAL clipboard' `
            (Wait-Until { [Windows.Forms.Clipboard]::GetText() -ceq 'rapid-fixes-original' } 2000) ''

        Set-Target $EN_garbage $true
        [Windows.Forms.Clipboard]::SetText('do-not-restore-over-user-copy')
        Invoke-Fix
        # Same text, a different clipboard write: compare ownership, not text.
        [Windows.Forms.Clipboard]::SetText($TH_sawatdi)
        Wait-Ms 850
        Assert-Equal 'later user copy survives stale restore timer' ([Windows.Forms.Clipboard]::GetText()) $TH_sawatdi
    }
} finally {
    if ($fixerProc -and -not $fixerProc.HasExited) { $fixerProc.Kill(); $fixerProc.WaitForExit() }
    if ($null -ne $capsAtStart -and (Get-CapsLock) -ne $capsAtStart) { Send-Key 0x14 }
    if ($form) { $form.Dispose() }
    if ($clipboardBefore) { [Windows.Forms.Clipboard]::SetDataObject($clipboardBefore, $true) }
    $rows | Format-Table -AutoSize
    $rows | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot '_latency-results.json')
}
exit $failures
