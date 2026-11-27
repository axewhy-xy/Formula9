@echo off
rem Builds Builds\Installer\Formula9-Setup-<version>.exe: runs build.bat's build, then packs that folder
rem into one installer with Inno Setup 6 (installed with winget if missing). Close the project in
rem Unity first. Without backend\.venv it sets that up first, the way run.bat does.
rem Arguments go to Tools\build_installer.py, for example: installer.bat --skip-build
setlocal
set "PYTHONHOME="
set "PYTHONPATH="
set "VIRTUAL_ENV="
pushd "%~dp0" || exit /b 1
set "PYTHON=%~dp0backend\.venv\Scripts\python.exe"
if exist "%PYTHON%" goto package
echo Setting up the backend environment first. This takes a few minutes.
call "%~dp0run.bat" --setup
if not exist "%PYTHON%" (
    echo The backend environment could not be set up. Run run.bat once, then try again.
    set "RESULT=1"
    goto done
)
:package
"%PYTHON%" "%~dp0Tools\build_installer.py" %*
set "RESULT=%ERRORLEVEL%"

:done
popd
rem Keep the window open on a double-click so the result can be read.
if "%~1"=="" pause
exit /b %RESULT%
