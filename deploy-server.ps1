# ============================================================================
#  deploy-server.ps1
#
#  一鍵發布統一遊戲伺服器到 Docker:
#    1. Unity batch mode 建置 Linux Dedicated Server → publish/linux-server
#    2. 掛維護頁 → docker compose 重建 game-server image 並重啟容器 → 卸維護頁
#       (server 烘進 image,非 bind mount,必須 --build 才會吃到新產物)
#
#  注意:容器重啟會踢掉線上玩家連線;重啟期間前端連不上 server,
#  故 [2/2] 前後以 deploy-webgl.ps1 -Maintenance on/off 掛/卸維護頁
#  (建置期間舊 server 照常服務,維護窗口只覆蓋容器重建段)。失敗時維護頁保持掛上。
#
#  Usage:   .\deploy-server.ps1 [-SkipBuild]
#           -SkipBuild   跳過 Unity 建置,用現有 publish/linux-server 重建容器
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

    $log = Join-Path $Root 'publish\linux-server-build.log'
    New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
    Write-Host "[1/2] Unity Linux Server 建置中(log: $log)..."
    # -buildTarget/-standaloneBuildSubtarget 讓 Editor 啟動即在目標平台:
    # executeMethod 中呼叫 SwitchActiveBuildTarget 只會「請求」重編譯、不等完成,
    # 緊接的 Addressables build 會在半切換狀態下以 SBP ErrorException 失敗
    $p = Start-Process -FilePath $Unity -ArgumentList @(
        '-batchmode', '-quit',
        '-projectPath', "`"$Project`"",
        '-buildTarget', 'Linux64',
        '-standaloneBuildSubtarget', 'Server',
        '-executeMethod', 'PinionCore.Project2.Build.PublishBuilder.BuildLinuxServer',
        '-logFile', "`"$log`""
    ) -Wait -PassThru -NoNewWindow
    if ($p.ExitCode -ne 0) {
        Get-Content $log -Tail 40 -Encoding UTF8
        throw "Unity 建置失敗(exit $($p.ExitCode)),完整 log:$log"
    }
    Write-Host "[1/2] 建置完成 → publish\linux-server"
}

# 重啟期間前端連不上 server,掛維護頁;成功後卸下,失敗保持掛上
$DeployWebGL = Join-Path $Root 'deploy-webgl.ps1'
& $DeployWebGL -Maintenance on

Write-Host "[2/2] 重建並重啟 Docker game-server..."
docker compose -f $Compose up --build -d game-server
if ($LASTEXITCODE -ne 0) { throw "docker compose 失敗(exit $LASTEXITCODE);維護頁保持掛上,修復後以 .\deploy-webgl.ps1 -Maintenance off 卸下" }

& $DeployWebGL -Maintenance off

Write-Host ""
Write-Host "完成:pinioncore-project2-server 已更新(TCP 27232 / WS 27233)"
Write-Host "檢查 log:docker logs -f pinioncore-project2-server"
