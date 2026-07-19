# ============================================================================
#  deploy-webgl.ps1
#
#  一鍵發布 WebGL client 到 Docker:
#    1. Unity batch mode 建置 WebGL client → publish/webgl-client
#    2. docker compose 啟動 webgl-client(nginx bind mount,建置產物即時生效)
#
#  Usage:   .\deploy-webgl.ps1 [-SkipBuild] [-Maintenance on|off]
#           -SkipBuild        跳過 Unity 建置,只確保 Docker 服務在跑
#           -Maintenance on   只掛維護頁後結束(不建置、不動容器)
#           -Maintenance off  只卸維護頁後結束
# ============================================================================

param(
    [switch]$SkipBuild,
    [ValidateSet('on', 'off')]
    [string]$Maintenance
)

$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
$Project = Join-Path $Root 'PinionCore.Project2.Game'
$Compose = Join-Path $Root 'docker\docker-compose.yml'
# nginx 逐 request 檢查此旗標檔,touch/刪除即時生效(見 docker/nginx-webgl.conf)
$MaintenanceFlag = Join-Path $Root 'docker\maintenance\on'

if ($Maintenance) {
    if ($Maintenance -eq 'on') {
        New-Item -ItemType File -Force $MaintenanceFlag | Out-Null
        Write-Host "維護頁已掛上(卸下:.\deploy-webgl.ps1 -Maintenance off)"
    }
    else {
        if (Test-Path $MaintenanceFlag) { Remove-Item $MaintenanceFlag -Force }
        Write-Host "維護頁已卸下"
    }
    exit 0
}

if (-not $SkipBuild) {
    # 從 ProjectVersion.txt 取版本;安裝根目錄依機器而異,逐一探測
    $version = (Select-String -Path (Join-Path $Project 'ProjectSettings\ProjectVersion.txt') `
        -Pattern 'm_EditorVersion: (.+)').Matches[0].Groups[1].Value.Trim()
    $Unity = @("D:\Unity\IDEs\$version\Editor\Unity.exe", "D:\unity\editors\$version\Editor\Unity.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Unity) { throw "找不到 Unity $version(已探測 D:\Unity\IDEs 與 D:\unity\editors)" }

    # 專案已被 Editor 開啟時 batch mode 會直接失敗,先擋下來給明確訊息
    $open = Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'" |
        Where-Object { $_.CommandLine -match [regex]::Escape($Project) }
    if ($open) { throw "Unity Editor 已開啟此專案(PID $($open.ProcessId)),請先關閉再執行。" }

    # 建置期間 publish\webgl-client 會被改寫,先掛維護頁;建置失敗時刻意保留
    New-Item -ItemType File -Force $MaintenanceFlag | Out-Null

    $log = Join-Path $Root 'publish\webgl-client-build.log'
    New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
    Write-Host "[1/2] Unity WebGL 建置中(log: $log)..."
    # -buildTarget 讓 Editor 啟動即在 WebGL:deploy-server 會把 Library 的
    # active target 留在 Linux64,executeMethod 中切換平台不等重編譯完成,
    # Addressables build 會以 SBP ErrorException 失敗(同 deploy-server.ps1 註記)
    $p = Start-Process -FilePath $Unity -ArgumentList @(
        '-batchmode', '-quit',
        '-projectPath', "`"$Project`"",
        '-buildTarget', 'WebGL',
        '-executeMethod', 'PinionCore.Project2.Build.PublishBuilder.BuildWebGLClient',
        '-logFile', "`"$log`""
    ) -Wait -PassThru -NoNewWindow
    if ($p.ExitCode -ne 0) {
        Get-Content $log -Tail 40 -Encoding UTF8
        throw "Unity 建置失敗(exit $($p.ExitCode)),完整 log:$log;維護頁保持掛上,修復後以 -Maintenance off 卸下"
    }
    Write-Host "[1/2] 建置完成 → publish\webgl-client"
}

Write-Host "[2/2] 啟動 Docker webgl-client..."
docker compose -f $Compose up -d webgl-client
if ($LASTEXITCODE -ne 0) { throw "docker compose 失敗(exit $LASTEXITCODE);維護頁保持掛上,修復後以 -Maintenance off 卸下" }

# 部署完成,卸下建置前掛上的維護頁(-SkipBuild 沒掛過,不動旗標)
if (-not $SkipBuild -and (Test-Path $MaintenanceFlag)) {
    Remove-Item $MaintenanceFlag -Force
}

Write-Host ""
Write-Host "完成:http://localhost:27234"
