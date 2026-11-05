@echo off
setlocal
cd /d "%~dp0"
echo ============================================
echo  DolatMan - Clean, Restore and Build
echo ============================================
dotnet --version
if errorlevel 1 goto :error

if not exist "App.xaml" (
  echo ERROR: App.xaml is missing from the project folder.
  goto :error
)
if not exist "App.xaml.cs" (
  echo ERROR: App.xaml.cs is missing from the project folder.
  goto :error
)
if not exist "MainWindow.xaml" (
  echo ERROR: MainWindow.xaml is missing from the project folder.
  goto :error
)
if not exist "MainWindow.xaml.cs" (
  echo ERROR: MainWindow.xaml.cs is missing from the project folder.
  goto :error
)

echo.
echo [1/3] Removing old generated files...
if exist "bin" rmdir /s /q "bin"
if exist "obj" rmdir /s /q "obj"

echo.
echo [2/3] Restoring NuGet packages...
dotnet restore DolatMan.csproj
if errorlevel 1 goto :error

echo.
echo [3/3] Building WPF application...
dotnet build DolatMan.csproj -c Release --no-restore
if errorlevel 1 goto :error

echo.
echo BUILD COMPLETED SUCCESSFULLY.
echo Output: bin\Release\net10.0-windows\DolatMan.exe
pause
exit /b 0

:error
echo.
echo BUILD FAILED. Copy the complete error text and send it for correction.
pause
exit /b 1
