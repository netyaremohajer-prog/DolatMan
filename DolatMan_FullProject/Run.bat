@echo off
setlocal
cd /d "%~dp0"
echo ============================================
echo  DolatMan - Run (Release)
echo ============================================
dotnet --version
if errorlevel 1 (
  echo .NET SDK was not found. Install the .NET 10 SDK first.
  pause
  exit /b 1
)
if not exist "bin\Release\net10.0-windows\DolatMan.exe" (
  echo Release build not found. Run Build.bat first.
  pause
  exit /b 1
)
dotnet run --project DolatMan.csproj -c Release --no-restore
if errorlevel 1 (
  echo.
  echo The app did not start. Copy the complete error text and send it for correction.
  pause
  exit /b 1
)
endlocal
