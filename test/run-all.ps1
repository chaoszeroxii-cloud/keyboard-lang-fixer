<#
  Runs every suite, optionally more than once to shake out flakiness.

      powershell -NoProfile -ExecutionPolicy Bypass -File test\run-all.ps1
      powershell -NoProfile -ExecutionPolicy Bypass -File test\run-all.ps1 -Repeat 3

  Note on the self-test: the program is a windowed executable, so PowerShell's
  call operator does not wait for it and $LASTEXITCODE would be whatever the
  previous command left behind. Start-Process -Wait is the only reliable way to
  get its exit code.
#>
[CmdletBinding()]
param([int]$Repeat = 1)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'KeyboardLangFixer.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "$exe is missing - run build.cmd first." }

function Invoke-Exe([string[]]$exeArgs, [string]$outFile) {
    $p = Start-Process $exe -PassThru -RedirectStandardOutput $outFile -ArgumentList $exeArgs
    $null = $p.Handle
    [void]$p.WaitForExit(300000)
    return $p.ExitCode
}

function Invoke-Script([string[]]$psArgs) {
    $output = & powershell.exe @psArgs 2>&1
    return [pscustomobject]@{ Code = $LASTEXITCODE; Output = $output }
}

$totalFailures = 0
$selfTestOut = Join-Path $PSScriptRoot '_selftest_out.txt'
$wholeRun = [Diagnostics.Stopwatch]::StartNew()

for ($run = 1; $run -le $Repeat; $run++) {
    if ($Repeat -gt 1) { Write-Host "===== run $run of $Repeat =====" -ForegroundColor Cyan }

    # Timed per suite, and printed whether it passes or fails. A suite that
    # quietly gets slower is the thing this whole file was rewritten to stop
    # hiding, so the number has to be on screen every run.
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $code = Invoke-Exe @('--self-test') $selfTestOut
    $took = $sw.Elapsed.TotalSeconds
    if ($code -eq 0) {
        $line = @(Get-Content $selfTestOut | Select-String 'checks,')
        Write-Host ("  {0,-11} PASS  {1,6:N1}s  {2}" -f 'self-test', $took, ($line -join '')) -ForegroundColor Green
    } else {
        $totalFailures++
        Write-Host ("  {0,-11} FAIL  {1,6:N1}s  exit {2}" -f 'self-test', $took, $code) -ForegroundColor Red
        Get-Content $selfTestOut | Select-String 'FAIL' | ForEach-Object { Write-Host "              $_" -ForegroundColor Red }
    }
    Remove-Item $selfTestOut -ErrorAction SilentlyContinue

    $suites = @(
        @{ Name = 'runtime'; Sta = $true;  Script = 'runtime-test.ps1' }
        @{ Name = 'e2e';     Sta = $true;  Script = 'e2e-test.ps1' }
        # winspace-probe.ps1 is deliberately NOT here. It answers "does watching
        # Win+Space stop Windows switching the language", and it answers it by
        # measuring the same press with the program stopped as a control -- which
        # needs an idle desktop and a language state nothing else is touching.
        # Run mid-suite it measured noise and then hung outright. Run it on its
        # own, after a change that touches the hook or the input language.
        @{ Name = 'tray';    Sta = $false; Script = 'tray-quit-test.ps1' }
        @{ Name = 'dialog';  Sta = $true;  Script = 'dialog-shot.ps1' }
        @{ Name = 'install'; Sta = $false; Script = 'install-test.ps1' }
    )
    foreach ($s in $suites) {
        $psArgs = @('-NoProfile')
        if ($s.Sta) { $psArgs += '-STA' }
        $psArgs += @('-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot $s.Script))
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $r = Invoke-Script $psArgs
        $took = $sw.Elapsed.TotalSeconds
        if ($r.Code -eq 0) {
            Write-Host ("  {0,-11} PASS  {1,6:N1}s" -f $s.Name, $took) -ForegroundColor Green
        } else {
            $totalFailures++
            Write-Host ("  {0,-11} FAIL  {1,6:N1}s  exit {2}" -f $s.Name, $took, $r.Code) -ForegroundColor Red
            $r.Output | Select-String 'FAIL|Exception|error|REGRESSION|switched' | Select-Object -First 8 |
                ForEach-Object { Write-Host "              $_" -ForegroundColor Red }
        }
    }
}

Write-Host ''
Write-Host ("total failing suites across $Repeat run(s): $totalFailures  ({0:N1}s)" -f $wholeRun.Elapsed.TotalSeconds) `
    -ForegroundColor $(if ($totalFailures) { 'Red' } else { 'Green' })
exit $totalFailures
