@echo off
REM Thin wrapper - the real logic lives in deploy-server.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy-server.ps1" %*
exit /b %ERRORLEVEL%
