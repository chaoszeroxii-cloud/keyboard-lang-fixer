@echo off
REM Builds KeyboardLangFixer.exe using the C# compiler that ships with Windows.
REM Nothing needs to be installed first.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Build.ps1"
if errorlevel 1 (
  echo.
  echo Build failed.
  pause
  exit /b 1
)
