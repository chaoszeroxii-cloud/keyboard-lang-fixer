<#
  Adds (or removes) a shortcut in the current user's Startup folder so the
  fixer runs automatically after login. No admin rights needed, nothing is
  written to the registry, and no files outside the Startup folder are touched.

      powershell -ExecutionPolicy Bypass -File Install-Startup.ps1
      powershell -ExecutionPolicy Bypass -File Install-Startup.ps1 -Uninstall
#>
[CmdletBinding()]
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'

$root      = $PSScriptRoot
$launcher  = Join-Path $root 'Start-Hidden.vbs'
$startup   = [Environment]::GetFolderPath('Startup')
$shortcut  = Join-Path $startup 'Keyboard Language Fixer.lnk'

if ($Uninstall) {
    if (Test-Path $shortcut) {
        Remove-Item $shortcut
        Write-Host "Removed: $shortcut" -ForegroundColor Green
    } else {
        Write-Host "Nothing to remove; no shortcut at $shortcut" -ForegroundColor Yellow
    }
    return
}

if (-not (Test-Path $launcher)) { throw "Launcher not found: $launcher" }

$ws = New-Object -ComObject WScript.Shell
$sc = $ws.CreateShortcut($shortcut)
$sc.TargetPath        = Join-Path $env:SystemRoot 'System32\wscript.exe'
$sc.Arguments         = "`"$launcher`""
$sc.WorkingDirectory  = $root
$sc.Description       = 'Fixes text typed with the wrong keyboard layout (Thai <-> English)'
$sc.WindowStyle       = 7          # minimised; the launcher itself is windowless

# Without this the shortcut would show the generic wscript.exe icon.
$icon = Join-Path $root 'icon.ico'
if (Test-Path $icon) { $sc.IconLocation = "$icon,0" }

$sc.Save()

Write-Host "Installed: $shortcut" -ForegroundColor Green
Write-Host "It will start automatically at your next login." -ForegroundColor DarkGray
Write-Host "To start it right now without logging out, double-click Start-Hidden.vbs." -ForegroundColor DarkGray
