@echo off
REM Double-click this to stop Keyboard Language Fixer, remove it from startup,
REM and delete the installed copy.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -Uninstall
