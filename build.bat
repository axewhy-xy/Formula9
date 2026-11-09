@echo off
rem Builds Builds\Formula9\Formula9.exe with the backend bundled beside it, so the build runs the
rem client and the backend together. Run run.bat once first, and close the project in Unity.
rem Arguments go to Tools\build_executable.py, for example: build.bat --zip
setlocal
set "PYTHONHOME="
set "PYTHONPATH="
set "VIRTUAL_ENV="
pushd "%~dp0" || exit /b 1
set "PYTHON=%~dp0backend\.venv\Scripts\python.exe"
if not exist "%PYTHON%" (
    echo The backend environment is missing. Run run.bat once, then build again.
    set "RESULT=1"
    goto done
)
"%PYTHON%" "%~dp0Tools\build_executable.py" %*
set "RESULT=%ERRORLEVEL%"

:done
popd
rem Keep the window open on a double-click so the result can be read.
if "%~1"=="" pause
exit /b %RESULT%
