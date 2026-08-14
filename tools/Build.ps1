<#
  Compiles KeyboardLangFixer.exe with the C# compiler that ships with the .NET
  Framework, so building needs nothing installed beyond Windows itself. That is
  the whole reason this project targets that compiler: anyone who clones the
  repo can build it, and the result runs on any Windows 10/11 without a runtime.

      powershell -ExecutionPolicy Bypass -File tools\Build.ps1
      powershell -ExecutionPolicy Bypass -File tools\Build.ps1 -OutDir dist
#>
[CmdletBinding()]
param(
    # Where to put the executable. Defaults to the project root, which is where
    # the launchers and icon.ico live.
    [string]$OutDir,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$srcDir = Join-Path $root 'src'
if (-not $OutDir) { $OutDir = $root }
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $csc)) {
    throw "The .NET Framework C# compiler was not found. Windows 10/11 ships it at " +
          "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
}

$sources = @(Get-ChildItem -LiteralPath $srcDir -Filter *.cs -File | Sort-Object Name)
if ($sources.Count -eq 0) { throw "No .cs files in $srcDir" }

$exe = Join-Path $OutDir 'KeyboardLangFixer.exe'
$icon = Join-Path $root 'icon.ico'

# /target:winexe -> no console window ever appears, which is why the old
# PowerShell version needed a .vbs launcher and this one does not.
$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/platform:anycpu'
    '/optimize+'
    '/warn:4'
    "/out:$exe"
    '/reference:System.dll'
    '/reference:System.Core.dll'
    '/reference:System.Drawing.dll'
    '/reference:System.Windows.Forms.dll'
)
if (Test-Path -LiteralPath $icon) { $cscArgs += "/win32icon:$icon" }
$cscArgs += ($sources | ForEach-Object { $_.FullName })

if (-not $Quiet) {
    Write-Host "compiling $($sources.Count) source file(s) -> $exe" -ForegroundColor Cyan
}
$output = & $csc @cscArgs 2>&1
$code = $LASTEXITCODE

$warnings = @($output | Where-Object { $_ -match 'warning CS' })
$errors = @($output | Where-Object { $_ -match 'error CS' })

foreach ($e in $errors) { Write-Host $e -ForegroundColor Red }
if (-not $Quiet) { foreach ($w in $warnings) { Write-Host $w -ForegroundColor Yellow } }

if ($code -ne 0 -or $errors.Count -gt 0) {
    throw "Build failed with $($errors.Count) error(s)."
}

$info = Get-Item -LiteralPath $exe
if (-not $Quiet) {
    Write-Host ("built {0} ({1:N0} KB, {2} warning(s))" -f $info.Name, ($info.Length / 1KB), $warnings.Count) `
        -ForegroundColor Green
}
$exe
