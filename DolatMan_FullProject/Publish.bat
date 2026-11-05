@echo off
setlocal
cd /d "%~dp0"
echo ============================================
echo  DolatMan - Publish for Windows 10/11 x64
echo ============================================
dotnet publish DolatMan.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
if errorlevel 1 (
  echo Publish failed. Check the error above.
  pause
  exit /b 1
)
echo.
echo Publish completed.
echo EXE folder: bin\Release\net10.0-windows\win-x64\publish\
echo Keep all files in the publish folder together. Do not copy only the EXE.
pause
endlocal
