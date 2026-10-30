@echo off
setlocal
python "%~dp0Tools\run_backend.py" %*
exit /b %ERRORLEVEL%
