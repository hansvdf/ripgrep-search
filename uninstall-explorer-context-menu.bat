@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall-explorer-context-menu.ps1"
if errorlevel 1 (
  echo.
  echo Removal failed.
  pause
  exit /b 1
)
echo.
pause
