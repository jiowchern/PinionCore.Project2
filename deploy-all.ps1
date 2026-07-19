# ============================================================================
#  deploy-all.ps1
#
#  一鍵發布 client(WebGL)+ server(Server/User/World/Bot)到 Docker:
#    1. Unity batch mode 建置 Linux Dedicated Server → publish/linux-server
#       (舊服務照常運作,不掛維護頁)
#    2. 掛維護頁 → Unity batch mode 建置 WebGL client → publish/webgl-client
#       (webgl-client 是 nginx bind mount,建置會直接改寫線上目錄)
#    3. docker compose 重建 game-server image 並重啟兩個容器 → 卸維護頁
#       (兩端同一窗口更新,避免新舊 client/server 協議不一致的空窗)
#
#  失敗時維護頁保持掛上,修復後以 .\deploy-webgl.ps1 -Maintenance off 卸下。
#
#  Usage:   .\deploy-all.ps1 [-SkipBuild]
#           -SkipBuild   跳過兩個 Unity 建置,用現有 publish/ 重建容器
# ============================================================================

param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
$Project = Join-Path $Root 'PinionCore.Project2.Game'
$Compose = Join-Path $Root 'docker\docker-compose.yml'
$DeployWebGL = Join-Path $Root 'deploy-webgl.ps1'

function Invoke-UnityBuild([string[]]$TargetArgs, [string]$Method, [string]$Log) {
    $p = Start-Process -FilePath $Unity -ArgumentList (@(
        '-batchmode', '-quit',
        '-projectPath', "`"$Project`"") + $TargetArgs + @(
        '-executeMethod', $Method,
        '-logFile', "`"$Log`"")) -Wait -PassThru -NoNewWindow
    if ($p.ExitCode -ne 0) {
        Get-Content $Log -Tail 40 -Encoding UTF8
        throw "Unity 建置失敗(exit $($p.ExitCode)),完整 log:$Log"
    }
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

    New-Item -ItemType Directory -Force (Join-Path $Root 'publish') | Out-Null

    # [1/3] Linux Server:舊容器照常服務,不需維護頁
    # -buildTarget/-standaloneBuildSubtarget 讓 Editor 啟動即在目標平台:
    # executeMethod 中切換平台不等重編譯完成,Addressables build 會以
    # SBP ErrorException 失敗(詳見 deploy-server.ps1 註記)
    $log = Join-Path $Root 'publish\linux-server-build.log'
    Write-Host "[1/3] Unity Linux Server 建置中(log: $log)..."
    Invoke-UnityBuild @('-buildTarget', 'Linux64', '-standaloneBuildSubtarget', 'Server') `
        'PinionCore.Project2.Build.PublishBuilder.BuildLinuxServer' $log
    Write-Host "[1/3] 建置完成 → publish\linux-server"

    # WebGL 建置會改寫 bind mount 的 publish\webgl-client,先掛維護頁
    & $DeployWebGL -Maintenance on

    $log = Join-Path $Root 'publish\webgl-client-build.log'
    Write-Host "[2/3] Unity WebGL 建置中(log: $log)..."
    Invoke-UnityBuild @('-buildTarget', 'WebGL') `
        'PinionCore.Project2.Build.PublishBuilder.BuildWebGLClient' $log
    Write-Host "[2/3] 建置完成 → publish\webgl-client"
}
else {
    # 容器重啟期間前端連不上 server,一樣掛維護頁
    & $DeployWebGL -Maintenance on
}

Write-Host "[3/3] 重建並重啟 Docker game-server + webgl-client..."
docker compose -f $Compose up --build -d game-server webgl-client
if ($LASTEXITCODE -ne 0) { throw "docker compose 失敗(exit $LASTEXITCODE);維護頁保持掛上,修復後以 .\deploy-webgl.ps1 -Maintenance off 卸下" }

& $DeployWebGL -Maintenance off

Write-Host ""
Write-Host "完成:server TCP 27232 / WS 27233,client http://localhost:27234"
Write-Host "檢查 log:docker logs -f pinioncore-project2-server"
