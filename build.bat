@echo off
rem NameDayMascot (C#) - Windows build script
cd /d "%~dp0"

dotnet --version >nul 2>&1
if errorlevel 1 (
  echo [ERROR] dotnet not found. Please install .NET 10 SDK.
  pause
  exit /b 1
)

dotnet build -c Release
if errorlevel 1 (
  echo [ERROR] Build failed. See messages above.
  pause
  exit /b 1
)

echo.
echo [OK] Build succeeded. Run: dotnet run -c Release
pause
