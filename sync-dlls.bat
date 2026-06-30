@echo off
REM ============================================================================
REM  sync-dlls.bat
REM
REM  1) Builds the PinionCore.Remote projects and copies the resulting DLLs
REM     into the PinionCore.NetSync.Package (Unity UPM package).
REM  2) Builds PinionCore.Project2.Protocols and copies its DLL into
REM     PinionCore.Project2.Game (Assets\Plugins).
REM
REM  Notes:
REM   - For the NetSync package, only files that ALREADY exist in the package
REM     are overwritten. This guarantees we update the correct locations and
REM     never introduce new untracked files (so Unity .meta GUIDs stay stable).
REM   - .meta files are never touched.
REM
REM  Usage:   sync-dlls.bat [Release|Debug]   (default: Release)
REM ============================================================================

setlocal enabledelayedexpansion

set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Release"

set "ROOT=%~dp0"
set "REMOTE=%ROOT%PinionCore.Remote"
set "PKG=%ROOT%PinionCore.NetSync.Package"
set "PROTO=%ROOT%PinionCore.Project2.Protocols"
set "GAME=%ROOT%PinionCore.Project2.Game"

set "TFM=netstandard2.1"
set "TFM_ANALYZER=netstandard2.0"

set "PLUGINS=%PKG%\Runtime\Plugins"
set "ANALYZERS=%PKG%\Analyzers"
set "GAME_PLUGINS=%GAME%\Assets\Plugins"

echo ============================================================
echo  Configuration : %CONFIG%
echo ============================================================

REM ---------------------------------------------------------------------------
REM  1. Build the PinionCore.Remote projects
REM ---------------------------------------------------------------------------
echo.
echo === [1/4] Building PinionCore.Remote projects ===
for %%P in (
    "PinionCore.Network\PinionCore.Network.csproj"
    "PinionCore.Serialization\PinionCore.Serialization.csproj"
    "PinionCore.Utility\PinionCore.Utility\PinionCore.Utility.csproj"
    "PinionCore.Remote\PinionCore.Remote.csproj"
    "PinionCore.Remote.Client\PinionCore.Remote.Client.csproj"
    "PinionCore.Remote.Server\PinionCore.Remote.Server.csproj"
    "PinionCore.Remote.Ghost\PinionCore.Remote.Ghost.csproj"
    "PinionCore.Remote.Soul\PinionCore.Remote.Soul.csproj"
    "PinionCore.Remote.Standalone\PinionCore.Remote.Standalone.csproj"
    "PinionCore.Remote.Gateway\PinionCore.Remote.Gateway.csproj"
    "PinionCore.Remote.Gateway.Protocols\PinionCore.Remote.Gateway.Protocols.csproj"
    "PinionCore.Remote.Protocol.Identify\PinionCore.Remote.Protocol.Identify.csproj"
    "PinionCore.Remote.Tools.Protocol.Sources\PinionCore.Remote.Tools.Protocol.Sources.csproj"
) do (
    echo   building %%~P
    dotnet build "%REMOTE%\%%~P" -c %CONFIG% --nologo -v quiet
    if errorlevel 1 goto :build_error
)

REM ---------------------------------------------------------------------------
REM  2. Copy Remote DLLs -> NetSync package Runtime\Plugins
REM ---------------------------------------------------------------------------
echo.
echo === [2/4] Copying Remote DLLs -^> Runtime\Plugins ===

call :sync "%REMOTE%\PinionCore.Network\bin\%CONFIG%\%TFM%"                       "%PLUGINS%" PinionCore.Network
call :sync "%REMOTE%\PinionCore.Serialization\bin\%CONFIG%\%TFM%"                 "%PLUGINS%" PinionCore.Serialization
call :sync "%REMOTE%\PinionCore.Utility\PinionCore.Utility\bin\%CONFIG%\%TFM%"    "%PLUGINS%" PinionCore.Utility
call :sync "%REMOTE%\PinionCore.Remote\bin\%CONFIG%\%TFM%"                        "%PLUGINS%" PinionCore.Remote
call :sync "%REMOTE%\PinionCore.Remote.Client\bin\%CONFIG%\%TFM%"                 "%PLUGINS%" PinionCore.Remote.Client
call :sync "%REMOTE%\PinionCore.Remote.Server\bin\%CONFIG%\%TFM%"                 "%PLUGINS%" PinionCore.Remote.Server
call :sync "%REMOTE%\PinionCore.Remote.Ghost\bin\%CONFIG%\%TFM%"                  "%PLUGINS%" PinionCore.Remote.Ghost
call :sync "%REMOTE%\PinionCore.Remote.Soul\bin\%CONFIG%\%TFM%"                   "%PLUGINS%" PinionCore.Remote.Soul
call :sync "%REMOTE%\PinionCore.Remote.Standalone\bin\%CONFIG%\%TFM%"             "%PLUGINS%" PinionCore.Remote.Standalone
call :sync "%REMOTE%\PinionCore.Remote.Gateway\bin\%CONFIG%\%TFM%"                "%PLUGINS%" PinionCore.Remote.Gateway
call :sync "%REMOTE%\PinionCore.Remote.Gateway.Protocols\bin\%CONFIG%\%TFM%"      "%PLUGINS%" PinionCore.Remote.Gateway.Protocols
call :sync "%REMOTE%\PinionCore.Remote.Protocol.Identify\bin\%CONFIG%\%TFM%"      "%PLUGINS%" PinionCore.Remote.Protocol.Identify

REM ---------------------------------------------------------------------------
REM  3. Copy the source-generator analyzer -> NetSync package Analyzers
REM ---------------------------------------------------------------------------
echo.
echo === [3/4] Copying analyzer -^> Analyzers ===
call :sync "%REMOTE%\PinionCore.Remote.Tools.Protocol.Sources\bin\%CONFIG%\%TFM_ANALYZER%" "%ANALYZERS%" PinionCore.Remote.Tools.Protocol.Sources

REM ---------------------------------------------------------------------------
REM  4. Build + copy Project2.Protocols -> Game Assets\Plugins
REM ---------------------------------------------------------------------------
echo.
echo === [4/4] Building + copying Project2.Protocols -^> Game ===
dotnet build "%PROTO%\PinionCore.Project2.Protocols.csproj" -c %CONFIG% --nologo -v quiet
if errorlevel 1 goto :build_error

if not exist "%GAME_PLUGINS%" mkdir "%GAME_PLUGINS%"
set "PROTO_OUT=%PROTO%\bin\%CONFIG%\%TFM%"
REM Only the protocol assembly itself - PinionCore.Remote etc. are already
REM provided by the NetSync package, copying them again would duplicate them.
copy /Y "%PROTO_OUT%\PinionCore.Project2.Protocols.dll" "%GAME_PLUGINS%\" >nul && echo   [OK] PinionCore.Project2.Protocols.dll
if exist "%PROTO_OUT%\PinionCore.Project2.Protocols.pdb" copy /Y "%PROTO_OUT%\PinionCore.Project2.Protocols.pdb" "%GAME_PLUGINS%\" >nul && echo   [OK] PinionCore.Project2.Protocols.pdb

echo.
echo ============================================================
echo  Done.
echo ============================================================
endlocal
exit /b 0

REM ===========================================================================
REM  :sync  <srcDir> <dstDir> <baseName>
REM  Overwrites <baseName>.dll/.pdb/.deps.json in dstDir, but ONLY for the
REM  extensions that already exist there (keeps the package layout intact).
REM ===========================================================================
:sync
set "SRC=%~1"
set "DST=%~2"
set "NAME=%~3"
for %%E in (dll pdb deps.json) do (
    if exist "%DST%\%NAME%.%%E" (
        if exist "%SRC%\%NAME%.%%E" (
            copy /Y "%SRC%\%NAME%.%%E" "%DST%\" >nul && echo   [OK]   %NAME%.%%E
        ) else (
            echo   [WARN] source missing: %SRC%\%NAME%.%%E
        )
    )
)
exit /b 0

:build_error
echo.
echo  *** BUILD FAILED - aborting copy. ***
endlocal
exit /b 1
