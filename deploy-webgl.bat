@echo off
REM Thin wrapper - the real logic lives in deploy-webgl.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy-webgl.ps1" %*
exit /b %ERRORLEVEL%
