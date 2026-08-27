@echo off
setlocal

rem  Assembles the folder that goes to a shop.
rem
rem  Builds the installer, then gathers it with the documents somebody needs before and during
rem  setup, the settings and catalogue templates, a START-HERE.txt, and a checksum file. Produces
rem  both a folder and a zip under artifacts\ship.
rem
rem    ship                     build and assemble
rem    ship --skip-build        use the installer already in artifacts\installer
rem    ship --include-loose     add the copy-and-run executables for a locked-down machine
rem    ship --no-zip            folder only
rem
rem  Needs Inno Setup 6 on this machine. Nothing is needed on the machine being shipped to.

set "SCRIPT=%~dp0ship.ps1"

if not exist "%SCRIPT%" (
    echo Could not find %SCRIPT%.
    echo Run this from the folder it shipped in.
    exit /b 2
)

set "ARGS="

:parse
if "%~1"=="" goto run
if /i "%~1"=="--skip-build"    set "ARGS=%ARGS% -SkipBuild"    & shift & goto parse
if /i "%~1"=="--include-loose" set "ARGS=%ARGS% -IncludeLoose" & shift & goto parse
if /i "%~1"=="--no-zip"        set "ARGS=%ARGS% -NoZip"        & shift & goto parse
if /i "%~1"=="--version"       set "ARGS=%ARGS% -Version %~2"  & shift & shift & goto parse
echo Unknown option: %~1
echo Usage: ship [--skip-build] [--include-loose] [--no-zip] [--version ^<v^>]
exit /b 2

:run
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"%ARGS%
set "CODE=%ERRORLEVEL%"

echo.
if "%CODE%"=="0" (
    echo Shipment ready in artifacts\ship.
) else (
    echo FAILED - see the message above.
)

exit /b %CODE%
