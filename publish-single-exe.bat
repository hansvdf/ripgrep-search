@echo off
setlocal
cd /d "%~dp0"

dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true

if errorlevel 1 (
  echo.
  echo Publishing failed.
  pause
  exit /b 1
)

echo.
echo Executable created in:
echo bin\Release\net8.0-windows\win-x64\publish\RipgrepSearch.exe
pause
