@echo off
rem The one file to run. Double-click it: it installs what is missing, downloads the data, opens the
rem project in the right Unity version and serves the backend. It asks for nothing.
rem With arguments it only runs the launcher, for example: run.bat --test
setlocal
set "PYTHONHOME="
set "PYTHONPATH="
set "VIRTUAL_ENV="
pushd "%~dp0" || exit /b 1
set "ARGS=%*"
if not "%~1"=="" goto launcher
set "ARGS=--start-everything"

call :find_python
if defined PYTHON goto have_python
echo Installing Python 3.12...
winget install --id Python.Python.3.12 -e --silent --accept-package-agreements --accept-source-agreements
call :find_python
:have_python
git lfs version >nul 2>&1
if not errorlevel 1 goto launcher
echo Installing Git and Git LFS...
winget install --id Git.Git -e --silent --accept-package-agreements --accept-source-agreements
set "PATH=%ProgramFiles%\Git\cmd;%PATH%"

:launcher
call :find_python
if not defined PYTHON (
    echo Python 3.12 could not be found or installed. Install it from https://www.python.org and run this again.
    set "RESULT=1"
    goto done
)
%PYTHON% "%~dp0Tools\run_backend.py" %ARGS%
set "RESULT=%ERRORLEVEL%"

:done
popd
rem Keep the window open on a double-click so any error can be read.
if "%~1"=="" if not "%RESULT%"=="0" pause
exit /b %RESULT%

:find_python
set "PYTHON="
if exist "%~dp0backend\.venv\Scripts\python.exe" (
    set PYTHON="%~dp0backend\.venv\Scripts\python.exe"
    exit /b 0
)
py -3.12 -V >nul 2>&1
if not errorlevel 1 (
    set PYTHON=py -3.12
    exit /b 0
)
if exist "%LocalAppData%\Programs\Python\Python312\python.exe" (
    set PYTHON="%LocalAppData%\Programs\Python\Python312\python.exe"
    exit /b 0
)
python -V 2>nul | findstr /b "Python 3.1" >nul
if not errorlevel 1 set PYTHON=python
exit /b 0
