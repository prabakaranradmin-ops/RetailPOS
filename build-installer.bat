@echo off
setlocal

rem  Builds the shippable installer.
rem
rem  Runs the tests, publishes both executables, and wraps everything a lane needs into one
rem  setup.exe under artifacts\installer. The target machine needs nothing installed - not even
rem  the .NET runtime, which is bundled inside each executable.
rem
rem  The version comes from the git tag, not from anything written down, and the build refuses to
rem  run if this commit is not tagged or the working tree is dirty. An installer that says 1.0.0
rem  while carrying 1.1.0 code is worse than no installer, because somebody will trust it.
rem
rem    build-installer                    build the tagged release
rem    build-installer --version 1.2.0    build something that is not a tagged release
rem    build-installer --allow-dirty      build with uncommitted changes (not the release)
rem    build-installer --skip-publish     reuse artifacts\lane instead of rebuilding it
rem
rem  Needs Inno Setup 6 on this machine. Nothing is needed on the machine being installed to.

set "SCRIPT=%~dp0build-installer.ps1"

if not exist "%SCRIPT%" (
    echo Could not find %SCRIPT%.
    echo Run this from the folder it shipped in.
    exit /b 2
)

set "ARGS="

:parse
if "%~1"=="" goto run
if /i "%~1"=="--version"      set "ARGS=%ARGS% -Version %~2" & shift & shift & goto parse
if /i "%~1"=="--allow-dirty"  set "ARGS=%ARGS% -AllowDirty"  & shift & goto parse
if /i "%~1"=="--skip-publish" set "ARGS=%ARGS% -SkipPublish" & shift & goto parse
if /i "%~1"=="--compiler"     set "ARGS=%ARGS% -Compiler ""%~2""" & shift & shift & goto parse
echo Unknown option: %~1
echo Usage: build-installer [--version ^<v^>] [--allow-dirty] [--skip-publish] [--compiler ^<path^>]
exit /b 2

:run
rem  -ExecutionPolicy Bypass so this works on a machine that has never had scripts enabled. It
rem  applies to this process only and changes nothing on the machine.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"%ARGS%
set "CODE=%ERRORLEVEL%"

echo.
if "%CODE%"=="0" (
    echo Installer built. It is in artifacts\installer.
) else (
    echo Build FAILED - see the message above.
)

exit /b %CODE%
