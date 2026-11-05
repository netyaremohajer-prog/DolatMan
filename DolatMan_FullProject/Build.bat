@echo off
setlocal
cd /d "%~dp0"
echo ============================================
echo  DolatMan - Restore and Build
echo ============================================
dotnet --version
if errorlevel 1 goto :error
dotnet restore DolatMan.csproj
if errorlevel 1 goto :error
dotnet build DolatMan.csproj -c Release --no-restore
if errorlevel 1 goto :error
echo.
echo Build completed.
echo Output: bin\Release\net10.0-windows\DolatMan.exe
pause
exit /b 0
:error
echo.
echo Build failed. Copy the complete error text and send it for correction.
pause
exit /b 1
