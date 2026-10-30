@echo off
setlocal
set "PYTHONHOME="
set "PYTHONPATH="
set "VIRTUAL_ENV="
pushd "%~dp0" || exit /b 1
if exist "%~dp0backend\.venv\Scripts\python.exe" (
    "%~dp0backend\.venv\Scripts\python.exe" "%~dp0Tools\run_backend.py" %*
) else (
    py -3.12 -V >nul 2>&1
    if not errorlevel 1 (
        py -3.12 "%~dp0Tools\run_backend.py" %*
    ) else (
        python "%~dp0Tools\run_backend.py" %*
    )
)
set "RESULT=%ERRORLEVEL%"
popd
exit /b %RESULT%
