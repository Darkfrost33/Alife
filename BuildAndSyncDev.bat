@echo off
setlocal EnableExtensions

set "REPO=%~dp0"
cd /d "%REPO%"

powershell -NoProfile -Command "if (Get-Process -Name 'Alife.Client' -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }"
if not errorlevel 1 (
  echo [ERROR] Alife.Client is still running.
  echo         Close it completely before building so old DLLs are not kept in use.
  exit /b 2
)

echo ============================================
echo  1/2 Sync dynamic plugins for Debug
echo ============================================
call "%REPO%SyncPluginsToDev.bat"
if errorlevel 1 exit /b %errorlevel%

echo.
echo ============================================
echo  2/2 Build the complete Debug client
echo ============================================
dotnet build "%REPO%sources\Alife\Alife.Client\Alife.Client.csproj" -c Debug
if errorlevel 1 exit /b %errorlevel%

echo.
echo ============================================
echo  Build and sync completed successfully.
echo  Output: %REPO%Outputs\Debug
echo ============================================

if /I "%~1"=="/run" (
  echo Starting Alife from the root shortcut...
  start "" "%REPO%Alife.Client.exe.lnk"
) else (
  echo Start Alife.Client.exe.lnk to run this build.
  echo Or use: BuildAndSyncDev.bat /run
)

exit /b 0
