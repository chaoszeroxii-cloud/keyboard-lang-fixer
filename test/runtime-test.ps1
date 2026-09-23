# Exercises the resident STA and real clipboard notification races. No focus or
# keyboard injection required; preserves the original clipboard on completion.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$exe = Join-Path $PSScriptRoot '_runtime-test.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter *.cs | ForEach-Object FullName)
try {
    & $csc /nologo /target:exe /optimize+ /main:KbFix.RuntimeTest "/out:$exe" `
        /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
        /reference:System.Windows.Forms.dll @sources (Join-Path $PSScriptRoot 'runtime-test.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Runtime test build failed' }
    & $exe
    $code = $LASTEXITCODE
} finally {
    Remove-Item -LiteralPath $exe -Force -ErrorAction SilentlyContinue
}
exit $code
