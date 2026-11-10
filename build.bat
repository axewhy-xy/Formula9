@echo off
rem Builds Builds\Formula9\Formula9.exe with the backend bundled beside it, so the build runs the
rem client and the backend together. Close the project in Unity first. Without backend\.venv it sets
rem that up first, the way run.bat does.
rem Arguments go to Tools\build_executable.py, for example: build.bat --zip --include-env
setlocal
set "PYTHONHOME="
set "PYTHONPATH="
set "VIRTUAL_ENV="
pushd "%~dp0" || exit /b 1
set "PYTHON=%~dp0backend\.venv\Scripts\python.exe"
if exist "%PYTHON%" goto build
echo Setting up the backend environment first. This takes a few minutes.
call "%~dp0run.bat" --setup
if not exist "%PYTHON%" (
    echo The backend environment could not be set up. Run run.bat once, then build again.
    set "RESULT=1"
    goto done
)
:build
"%PYTHON%" "%~dp0Tools\build_executable.py" %*
set "RESULT=%ERRORLEVEL%"

:done
popd
rem Keep the window open on a double-click so the result can be read.
if "%~1"=="" pause
exit /b %RESULT%
