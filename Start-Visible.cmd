@echo off
REM Runs KeyboardLangFixer with its console window visible, so the log lines and
REM any errors are readable. Close this window to stop the tool.
cd /d "%~dp0"
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0KeyboardLangFixer.ps1" -Relaunched %*
pause
