# ============================================================================
#  deploy-webgl.ps1
#
#  一鍵發布 WebGL client 到 Docker:
#    1. Unity batch mode 建置 WebGL client → publish/webgl-client
#    2. docker compose 啟動 webgl-client(nginx bind mount,建置產物即時生效)
#
#  Usage:   .\deploy-webgl.ps1 [-SkipBuild]
#           -SkipBuild  跳過 Unity 建置,只確保 Docker 服務在跑
# ============================================================================

param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
$Project = Join-Path $Root 'PinionCore.Project2.Game'
$Compose = Join-Path $Root 'docker\docker-compose.yml'

if (-not $SkipBuild) {
    # 從 ProjectVersion.txt 取版本,對應 Unity Hub 安裝目錄(D:\Unity\IDEs)
    $version = (Select-String -Path (Join-Path $Project 'ProjectSettings\ProjectVersion.txt') `
        -Pattern 'm_EditorVersion: (.+)').Matches[0].Groups[1].Value.Trim()
    $Unity = "D:\Unity\IDEs\$version\Editor\Unity.exe"
    if (-not (Test-Path $Unity)) { throw "找不到 Unity $version : $Unity" }

    # 專案已被 Editor 開啟時 batch mode 會直接失敗,先擋下來給明確訊息
    $open = Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'" |
        Where-Object { $_.CommandLine -match [regex]::Escape($Project) }
    if ($open) { throw "Unity Editor 已開啟此專案(PID $($open.ProcessId)),請先關閉再執行。" }

    $log = Join-Path $Root 'publish\webgl-client-build.log'
    New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
    Write-Host "[1/2] Unity WebGL 建置中(log: $log)..."
    $p = Start-Process -FilePath $Unity -ArgumentList @(
        '-batchmode', '-quit',
        '-projectPath', "`"$Project`"",
        '-executeMethod', 'PinionCore.Project2.Build.PublishBuilder.BuildWebGLClient',
        '-logFile', "`"$log`""
    ) -Wait -PassThru -NoNewWindow
    if ($p.ExitCode -ne 0) {
        Get-Content $log -Tail 40
        throw "Unity 建置失敗(exit $($p.ExitCode)),完整 log:$log"
    }
    Write-Host "[1/2] 建置完成 → publish\webgl-client"
}

Write-Host "[2/2] 啟動 Docker webgl-client..."
docker compose -f $Compose up -d webgl-client
if ($LASTEXITCODE -ne 0) { throw "docker compose 失敗(exit $LASTEXITCODE)" }

Write-Host ""
Write-Host "完成:http://localhost:27234"
