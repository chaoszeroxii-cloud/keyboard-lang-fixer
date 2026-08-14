<#
  One-shot installer. Builds the executable if it is not there yet, copies the
  tool into the user's own app-data folder, sets it to start at login, and
  launches it -- no admin rights, no registry, nothing outside the user profile.

  Normally launched by double-clicking Install.cmd. Run directly with:
      powershell -ExecutionPolicy Bypass -File Install.ps1
      powershell -ExecutionPolicy Bypass -File Install.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [switch]$Uninstall,
    # Skip the finishing message box (used by the tests).
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

$AppName     = 'Keyboard Language Fixer'
$ExeName     = 'KeyboardLangFixer.exe'
$source      = $PSScriptRoot
$target      = Join-Path $env:LOCALAPPDATA 'KeyboardLangFixer'
$startupLink = Join-Path ([Environment]::GetFolderPath('Startup')) "$AppName.lnk"

# What the installed copy needs to run, reconfigure and uninstall itself.
# settings.json is deliberately absent: it belongs to the installed copy and a
# reinstall must not wipe the user's chosen hotkey.
$payload = @(
    $ExeName
    'icon.ico'
    'README.md'
    'LICENSE'
    'Install.cmd'
    'Install.ps1'
    'Uninstall.cmd'
    'Settings.cmd'
)

function Say([string]$text, [string]$colour = 'Gray') { Write-Host $text -ForegroundColor $colour }

function Stop-RunningCopy {
    $running = @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($ExeName)) -ErrorAction SilentlyContinue)
    foreach ($p in $running) {
        Say "  stopping running copy (PID $($p.Id))"
        try { $p.Kill(); $p.WaitForExit(5000) | Out-Null } catch { }
    }
    if ($running.Count) { Start-Sleep -Milliseconds 600 }
    return $running.Count
}

# ------------------------------------------------------------------------------
if ($Uninstall) {
    Say "Removing $AppName ..." 'Cyan'
    [void](Stop-RunningCopy)

    if (Test-Path -LiteralPath $startupLink) {
        Remove-Item -LiteralPath $startupLink -Force
        Say '  removed the startup shortcut'
    }

    $message = "$AppName has been stopped and will no longer start with Windows."
    # Only delete the installed copy, never the folder the installer was run from.
    if ((Test-Path -LiteralPath $target) -and ($source -ne $target)) {
        Remove-Item -LiteralPath $target -Recurse -Force
        Say "  deleted $target"
        $message += "`n`nDeleted:`n$target"
    } elseif ($source -eq $target) {
        $message += "`n`nThe program folder was left in place because the uninstaller is running from it:`n$target"
    }

    Say 'Done.' 'Green'
    if (-not $Quiet) {
        [System.Windows.Forms.MessageBox]::Show($message, $AppName, 'OK', 'Information') | Out-Null
    }
    exit 0
}

# ------------------------------------------------------------------------------
Say "Installing $AppName ..." 'Cyan'

# Build on demand, so a fresh clone of the repository installs without any
# separate step. The compiler ships with Windows.
$sourceExe = Join-Path $source $ExeName
if (-not (Test-Path -LiteralPath $sourceExe)) {
    $builder = Join-Path $source 'tools\Build.ps1'
    if (-not (Test-Path -LiteralPath $builder)) {
        throw "Neither $ExeName nor tools\Build.ps1 is in $source - copy the whole folder."
    }
    Say '  building the executable ...'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $builder -Quiet | Out-Null
    if (-not (Test-Path -LiteralPath $sourceExe)) { throw "The build did not produce $ExeName." }
    Say '  built'
}

[void](Stop-RunningCopy)

if ($source -eq $target) {
    Say "  already installed in $target, refreshing the startup entry only"
} else {
    if (-not (Test-Path -LiteralPath $target)) {
        New-Item -ItemType Directory -Path $target -Force | Out-Null
    }
    foreach ($file in $payload) {
        $from = Join-Path $source $file
        if (Test-Path -LiteralPath $from) { Copy-Item -LiteralPath $from -Destination $target -Force }
    }
    # tools\ travels along so the installed copy can rebuild itself, which also
    # means Install.cmd works when run from the installed folder.
    $toolsFrom = Join-Path $source 'tools'
    if (Test-Path -LiteralPath $toolsFrom) {
        Copy-Item -LiteralPath $toolsFrom -Destination $target -Recurse -Force
    }
    Say "  copied to $target"
}

# The shortcut has to point at the installed copy, so let that copy make it.
$targetExe = Join-Path $target $ExeName
& $targetExe --install-startup | Out-Null
if (Test-Path -LiteralPath $startupLink) { Say '  set to start with Windows' }
else { Say '  could not create the startup shortcut' 'Yellow' }

Start-Process $targetExe
Start-Sleep -Seconds 3
Say '  started'

Say 'Done.' 'Green'
if (-not $Quiet) {
    [System.Windows.Forms.MessageBox]::Show(
        "$AppName is installed and running." +
        "`n`nType something in the wrong language and press Win+Space - the last word is fixed." +
        " Select text first to fix exactly that." +
        "`n`nIt starts automatically when you log in. The tray icon has a" +
        " 'Change hotkey...' option, and Uninstall.cmd removes it completely." +
        "`n`nInstalled in:`n$target",
        $AppName, 'OK', 'Information') | Out-Null
}
exit 0
