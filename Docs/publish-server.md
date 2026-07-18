# 伺服器發布流程(本地 Docker)

把線上版統一伺服器(Server/World/User/Bot 四場景,Linux Dedicated Server build)發布到本地 docker。
前端(WebGL client)另有獨立服務,不發前端時不影響。

## 前置條件

- Unity `6000.5.1f1`(`D:\unity\editors\6000.5.1f1`),已安裝 Linux Dedicated Server 模組(Hub CLI id=`linux-server`)。
- Docker Desktop 運行中。
- **Unity 編輯器必須關閉**:build 走 CLI batchmode,編輯器開著會佔住 `Temp/UnityLockfile`,
  batchmode 會直接以 exit code 1 退出(log 只有一行 `Exiting without the bug reporter`,沒有錯誤訊息)。
  - 檢查:`Test-Path PinionCore.Project2.Game\Temp\UnityLockfile` 為 `False` 且無 `Unity` 程序。
  - 關閉前記得存檔(場景 + assets)。
- 不能用 Unity MCP 在編輯器內觸發 build:target 切換會整包 reimport,MCP 必逾時。

## 發布步驟

### 1. 建置 Linux server(CLI batchmode)

```powershell
& "D:\unity\editors\6000.5.1f1\Editor\Unity.exe" `
  -batchmode -quit `
  -projectPath "D:\develop\ProjectGame2\PinionCore.Project2.Game" `
  -executeMethod PinionCore.Project2.Build.PublishBuilder.BuildLinuxServer `
  -logFile "$env:TEMP\build-linux-server.log"
```

- 入口在 [PublishBuilder.cs](../PinionCore.Project2.Game/Assets/Project/Editor/Build/PublishBuilder.cs)
  (編輯器內對應選單 `Project/Publish/Build Linux Server`,但發布一律走 CLI)。
- 流程:切 build target(StandaloneLinux64 + Server subtarget)→ Addressables content build → BuildPlayer。
  target 切換會觸發整包 reimport,首次或跨 target 切換要跑一段時間(數分鐘到十幾分鐘)。
- 成功判定:exit code 0,log 尾端有
  `[PublishBuilder] StandaloneLinux64(Server)完成 → ...\publish\linux-server\ProjectGame2Server.x86_64`。
- 產物在 repo 根的 `publish/linux-server/`。驗證新舊看 `ProjectGame2Server_Data\Managed\*.dll` 的時間戳
  (`.x86_64` 主檔內容沒變時 Unity 不會重寫,時間戳可能是舊的,別只看它)。
- 失敗排查:搜 log 關鍵字 `BuildFailedException`、`Aborting batchmode`、`error CS`。

### 2. 部署到 docker(只起 game-server)

```powershell
docker compose -f docker\docker-compose.yml up --build -d game-server
```

- compose 檔:[docker-compose.yml](../docker/docker-compose.yml);image 由
  [Dockerfile.gameserver](../docker/Dockerfile.gameserver) 以 `publish/linux-server` 為 build context 建出。
- 指名 `game-server` 就只會重建+重啟伺服器容器;`webgl-client`(8081)/`webgl-client-dev`(8082)不會被動到。
- 要連前端一起發:先跑 `PublishBuilder.BuildAll`(server+WebGL),再 `up --build -d` 不帶服務名。

### 3. 驗證

```powershell
docker logs docker-game-server-1 --tail 50
```

三個必要標記(啟動後幾秒內就會全出現):

1. `[ServersEntry] 伺服器場景載入完成:World, User, Bot`
2. `Bot verify result: True`
3. `AutoConnector: Standalone connect success`,且**無** Gateway 重試訊息
   (ServersEntry 已停用指向 Gateway 場景的 AutoConnector,拓撲為直連)。

埠檢查:

```powershell
(Test-NetConnection 127.0.0.1 -Port 27232).TcpTestSucceeded   # TCP client(LocalTcpUser)
(Test-NetConnection 127.0.0.1 -Port 27233).TcpTestSucceeded   # WebSocket client(LocalWebUser)
```

## 環境陷阱(實測踩過)

| 症狀 | 原因 / 處置 |
| --- | --- |
| batchmode 秒退 exit 1,log 無錯誤 | 編輯器開著佔住專案鎖,關閉編輯器重跑 |
| host 8080 不可用 | WSL 舊服務經 wslrelay 佔住 `::1:8080`,nginx 對外一律用 8081 |
| 本機 Chrome 連 `127.0.0.1` 高埠被拒 | 疑 Outline VPN 造成;curl 可通。驗證/遊玩改用 LAN IP(如 `http://192.168.50.4:8081`) |
| 換部署環境要改連線端點 | 不用重建:改 `publish/webgl-client/StreamingAssets/connection.json`(`"web":"auto"` 由頁面 URL 自動推導 ws 端點) |

## 連線資訊速查

| 用途 | 端點 |
| --- | --- |
| TCP client | `<host>:27232` |
| WebSocket client | `ws://<host>:27233` |
| WebGL 前端(有發時) | `http://<LAN IP>:8081` |
