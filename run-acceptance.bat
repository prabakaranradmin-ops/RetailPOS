@echo off
setlocal

rem  RetailPOS acceptance run.
rem
rem  Drives the shipped executables end to end - catalogue, billing, payment, printing, day close -
rem  photographs the till at each step, and writes a single HTML report with the checks that must
rem  work and the checks that must be refused reported separately.
rem
rem  It bills against a throwaway lane of its own under artifacts\acceptance\workspace and never
rem  touches %LOCALAPPDATA%\RetailPOS, so it is safe to run on a terminal that has real data on it.
rem
rem    run-acceptance            run everything, using artifacts\lane if it is there
rem    run-acceptance --no-ui    skip the parts that drive the window (no desktop needed)
rem    run-acceptance --keep     leave the throwaway lane behind for inspection
rem
rem  Exit code is 0 when every check passed, 1 otherwise, so it can gate a rollout.

set "SCRIPT=%~dp0tools\acceptance\Run-Acceptance.ps1"

if not exist "%SCRIPT%" (
    echo Could not find %SCRIPT%.
    echo Run this from the folder it shipped in.
    exit /b 2
)

set "ARGS="

:parse
if "%~1"=="" goto run
if /i "%~1"=="--no-ui"  set "ARGS=%ARGS% -NoUi"        & shift & goto parse
if /i "%~1"=="--keep"   set "ARGS=%ARGS% -KeepWorkspace" & shift & goto parse
if /i "%~1"=="--bin"    set "ARGS=%ARGS% -BinDir ""%~2""" & shift & shift & goto parse
echo Unknown option: %~1
echo Usage: run-acceptance [--no-ui] [--keep] [--bin ^<folder^>]
exit /b 2

:run
rem  -ExecutionPolicy Bypass so the run works on a lane that has never had scripts enabled, which
rem  is every lane. It applies to this process only and changes nothing on the machine.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"%ARGS%
set "CODE=%ERRORLEVEL%"

echo.
if "%CODE%"=="0" (
    echo All checks passed.
) else (
    echo One or more checks FAILED - see the report.
)

set "REPORT=%~dp0artifacts\acceptance\acceptance-report.html"
if exist "%REPORT%" start "" "%REPORT%"

exit /b %CODE%
