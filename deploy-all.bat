@echo off
REM Thin wrapper - the real logic lives in deploy-all.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy-all.ps1" %*
exit /b %ERRORLEVEL%
