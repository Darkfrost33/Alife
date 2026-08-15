@echo off
setlocal EnableExtensions EnableDelayedExpansion

rem =============================================================================
rem SyncPluginsToDev.bat
rem 把软件里升级/安装到 Storage\Plugins 的插件，同步到 Debug 用的 PluginsDebug。
rem
rem 用法:
rem   SyncPluginsToDev.bat
rem       同步所有「非官方内置」插件（推荐）
rem   SyncPluginsToDev.bat Alife.Function.DeskPet.Layout Alife.Plugin.SmartWebSearch
rem       只同步指定插件
rem   SyncPluginsToDev.bat /all
rem       同步 Plugins 下全部插件（含官方 Function，Debug 一般不需要）
rem =============================================================================

set "REPO=%~dp0"
set "OFFICIAL_SRC=%REPO%Sources\Alife.Function"

rem Prefer the repository-local Storage used by the root development shortcut.
rem Fall back to the legacy Documents location for installed/release clients.
if exist "%REPO%Storage\Plugins\" (
  set "SRC=%REPO%Storage\Plugins"
  set "DST=%REPO%Storage\PluginsDebug"
) else (
  set "SRC=%USERPROFILE%\Documents\Alife\Storage\Plugins"
  set "DST=%USERPROFILE%\Documents\Alife\Storage\PluginsDebug"
)

if not exist "%SRC%\" (
  echo [ERROR] Source not found: "%SRC%"
  echo         Check Alife storage path / that plugins were installed.
  exit /b 1
)

if not exist "%DST%\" mkdir "%DST%"

set "MODE=auto"
if /I "%~1"=="/all" (
  set "MODE=all"
  shift /1
)

echo ============================================
echo  Sync Plugins -^> PluginsDebug
echo  From: %SRC%
echo  To:   %DST%
echo ============================================
echo.

set "COPIED=0"
set "SKIPPED=0"

if not "%~1"=="" goto :sync_named

rem ----- auto / all: iterate Storage\Plugins, then repo Plugins\ overlays -----
for /d %%D in ("%SRC%\*") do (
  set "NAME=%%~nxD"
  call :should_sync "!NAME!"
  if errorlevel 1 (
    echo [skip] !NAME!
    if exist "%OFFICIAL_SRC%\!NAME!\!NAME!.csproj" (
      if not exist "%DST%\!NAME!\" mkdir "%DST%\!NAME!"
      if exist "%SRC%\!NAME!\VERSION.txt" copy /Y "%SRC%\!NAME!\VERSION.txt" "%DST%\!NAME!\VERSION.txt" >nul
    )
    set /a SKIPPED+=1
  ) else (
    call :copy_one "!NAME!"
  )
)

rem Repo-maintained overlays (e.g. AutoSpeak, DeskPet.Layout) may only live under Plugins\.
if exist "%REPO%Plugins\" (
  for /d %%D in ("%REPO%Plugins\*") do (
    set "NAME=%%~nxD"
    if not exist "%DST%\!NAME!\" (
      call :should_sync "!NAME!"
      if not errorlevel 1 call :copy_one "!NAME!"
    ) else if exist "%REPO%Plugins\!NAME!\" (
      rem Always refresh overlays from repo source when present.
      call :should_sync "!NAME!"
      if not errorlevel 1 call :copy_one "!NAME!"
    )
  )
)
goto :done

:sync_named
:named_loop
if "%~1"=="" goto :done
set "NAME=%~1"
if exist "%SRC%\%NAME%\" goto :copy_named
if exist "%REPO%Plugins\%NAME%\" goto :copy_named
echo [miss] %NAME%  ^(not in Plugins^)
goto :next_named

:copy_named
call :copy_one "%NAME%"

:next_named
shift /1
goto :named_loop

:done
echo.
echo ============================================
echo  Done. copied=%COPIED%  skipped=%SKIPPED%
echo  Restart / reload modules in Debug Client to pick up changes.
echo ============================================
exit /b 0

rem -----------------------------------------------------------------------------
rem should_sync NAME
rem exit /b 0 = sync, 1 = skip
rem -----------------------------------------------------------------------------
:should_sync
set "PNAME=%~1"
if /I "%PNAME%"=="BaseDirectory" exit /b 1
if /I "%MODE%"=="all" exit /b 0

rem Official Function plugins are ProjectReference'd into Debug Client;
rem keeping .cs copies in PluginsDebug causes duplicate hot-compile.
if exist "%OFFICIAL_SRC%\%PNAME%\%PNAME%.csproj" exit /b 1

exit /b 0

rem -----------------------------------------------------------------------------
rem copy_one NAME
rem -----------------------------------------------------------------------------
:copy_one
set "PNAME=%~1"
set "FROM=%SRC%\%PNAME%"
set "TO=%DST%\%PNAME%"

rem Repository-maintained plugins contain source compatible with this branch.
if exist "%REPO%Plugins\%PNAME%\" set "FROM=%REPO%Plugins\%PNAME%"

if not exist "%TO%\" mkdir "%TO%"

echo [sync] %PNAME%
rem Exclude DeskPet shared DLLs already provided by Debug Client.
robocopy "%FROM%" "%TO%" /E /IS /IT /R:1 /W:1 /NFL /NDL /NJH /NJS ^
  /XD bin obj ^
  /XF Alife.Function.DeskPet.dll Alife.DeskPet.Protocol.dll >nul
set "RC=!errorlevel!"

rem Razor plugins need their generated component source for runtime hot-compile.
if /I "%PNAME%"=="Alife.Function.DeskPet.Layout" (
  set "RAZOR_GEN=%FROM%\obj\Debug\generated\Microsoft.CodeAnalysis.Razor.Compiler\Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator\DeskPetLayoutServiceUI_razor.g.cs"
  if exist "!RAZOR_GEN!" copy /Y "!RAZOR_GEN!" "%TO%\DeskPetLayoutServiceUI_razor.g.cs" >nul
)
rem robocopy: 0-7 = success-ish, >=8 = failure
if !RC! GEQ 8 (
  echo        FAILED  robocopy exit=!RC!
) else (
  echo        OK
  set /a COPIED+=1
)
exit /b 0
