@echo off
REM Double-click this to pick a different hotkey. The running copy is restarted
REM automatically so the new one takes effect straight away.
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0KeyboardLangFixer.ps1" -ConfigureHotkey
