@echo off
REM Thin wrapper - the real logic lives in sync-dlls.ps1.
REM (The old pure-batch version hit the cmd "cannot find the batch label"
REM quirk, silently skipping DLL copies.)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0sync-dlls.ps1" %*
exit /b %ERRORLEVEL%
