# Exercises the resident STA, real clipboard notification races, reading a
# selection straight from Edit/RichEdit controls, and the Caps Lock state the
# hook captures. That last part presses Caps Lock for real (and puts it back),
# so a running fixer is stopped first -- it would act on the press in whatever
# window is in front -- and started again afterwards.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$wpf = Join-Path (Split-Path -Parent $csc) 'WPF'
$exe = Join-Path $PSScriptRoot '_runtime-test.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter *.cs | ForEach-Object FullName)
$running = @(Get-Process KeyboardLangFixer -ErrorAction SilentlyContinue | ForEach-Object { $_.Path } | Sort-Object -Unique)
Get-Process KeyboardLangFixer -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit() }
try {
    & $csc /nologo /target:exe /optimize+ /main:KbFix.RuntimeTest "/out:$exe" `
        /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
        /reference:System.Windows.Forms.dll "/reference:$wpf\UIAutomationClient.dll" `
        "/reference:$wpf\UIAutomationTypes.dll" "/reference:$wpf\WindowsBase.dll" @sources (Join-Path $PSScriptRoot 'runtime-test.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Runtime test build failed' }
    & $exe
    $code = $LASTEXITCODE
} finally {
    Remove-Item -LiteralPath $exe -Force -ErrorAction SilentlyContinue
    foreach ($p in $running) { if ($p) { Start-Process -FilePath $p } }
}
exit $code
