@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "NO_OPEN=0"
set "NO_PAUSE=0"
:parse
if "%~1"=="" goto build
if /i "%~1"=="--no-open" (
    set "NO_OPEN=1"
    shift
    goto parse
)
if /i "%~1"=="--no-pause" (
    set "NO_PAUSE=1"
    shift
    goto parse
)
echo Usage: Build-Plugin.cmd [--no-open] [--no-pause]
exit /b 2

:build
pushd "%~dp0"
if errorlevel 1 (
    echo Could not open the plugin source directory.
    if "%NO_PAUSE%"=="0" pause
    exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build.ps1"
set "BUILD_EXIT=%ERRORLEVEL%"
if not "%BUILD_EXIT%"=="0" goto failed
echo.
echo Plugin package built successfully.
if "%NO_OPEN%"=="0" start "" "%~dp0dist"
goto finish

:failed
echo.
echo Plugin build failed with exit code %BUILD_EXIT%.

:finish
popd
if "%NO_PAUSE%"=="0" pause
exit /b %BUILD_EXIT%
