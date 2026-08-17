<#
  Opens the hotkey picker, screenshots it, and closes it. Used to eyeball the
  dialog layout without clicking through it by hand.

  Run with:  powershell -NoProfile -STA -ExecutionPolicy Bypass -File test\dialog-shot.ps1
#>
[CmdletBinding()]
param([string]$OutFile)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
namespace Shot {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    public static class W {
        public delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        /// Captures the window's own pixels, so another window sitting on top
        /// of it cannot end up in the screenshot instead.
        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        /// FindWindow is avoided here: passing $null for lpClassName from
        /// PowerShell marshals as an empty string, not NULL, and then nothing
        /// ever matches.
        public static IntPtr ByTitle(string title, bool mustBeVisible) {
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, l) => {
                var sb = new StringBuilder(300);
                GetWindowText(h, sb, 300);
                if (sb.ToString() == title && (!mustBeVisible || IsWindowVisible(h))) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }
    }
}
'@

$root = Split-Path -Parent $PSScriptRoot
$fixer = Join-Path $root 'KeyboardLangFixer.exe'
if (-not $OutFile) { $OutFile = Join-Path $PSScriptRoot '_dialog.png' }

$proc = Start-Process $fixer -PassThru -ArgumentList '--configure-hotkey'

$title = 'Keyboard Language Fixer - hotkey'
$hwnd = [IntPtr]::Zero
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt 25 -and $hwnd -eq [IntPtr]::Zero) {
    Start-Sleep -Milliseconds 50
    $hwnd = [Shot.W]::ByTitle($title, $true)      # must be VISIBLE, not merely created
}

if ($hwnd -eq [IntPtr]::Zero) {
    $exists = [Shot.W]::ByTitle($title, $false)
    if ($exists -ne [IntPtr]::Zero) {
        Write-Host '  FAIL  the dialog was created but never became visible' -ForegroundColor Red
    } else {
        Write-Host '  FAIL  the hotkey dialog never appeared' -ForegroundColor Red
    }
    if (-not $proc.HasExited) { $proc.Kill() }
    exit 1
}

# Long enough for the dialog to have painted itself; PrintWindow on a window
# that has not yet drawn comes back blank.
Start-Sleep -Milliseconds 250
$r = New-Object Shot.RECT
[void][Shot.W]::GetWindowRect($hwnd, [ref]$r)
$w = $r.Right - $r.Left
$h = $r.Bottom - $r.Top

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$printed = [Shot.W]::PrintWindow($hwnd, $hdc, 2)      # PW_RENDERFULLCONTENT
$g.ReleaseHdc($hdc)
if (-not $printed) { $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $h)) }
$g.Dispose()
$bmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Host ("  PASS  dialog appeared ({0}x{1}), saved to {2}" -f $w, $h, $OutFile) -ForegroundColor Green

[void][Shot.W]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)   # WM_CLOSE
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt 5 -and -not $proc.HasExited) { Start-Sleep -Milliseconds 50 }
if (-not $proc.HasExited) { $proc.Kill() }
exit 0
