@echo off
rem Serves the backend for a showcase (docs/showcase.md): a ready account and a session that shows every
rem feature within minutes. Run run.bat once first, so backend\.venv exists, and close its backend window:
rem both use port 8000. Then press Play in Unity, or open Formula9.exe.
rem Arguments are passed on, for example: showcase.bat --port 8001
setlocal
set "PYTHONHOME="
set "PYTHONPATH="
set "VIRTUAL_ENV="
pushd "%~dp0" || exit /b 1
if not exist "%~dp0backend\.venv\Scripts\python.exe" (
    echo The backend is not set up yet. Double-click run.bat once, then run this again.
    set "RESULT=1"
    goto done
)
"%~dp0backend\.venv\Scripts\python.exe" "%~dp0Tools\showcase_backend.py" %*
set "RESULT=%ERRORLEVEL%"

:done
popd
rem Keep the window open on a double-click so any error can be read.
if "%~1"=="" if not "%RESULT%"=="0" pause
exit /b %RESULT%
