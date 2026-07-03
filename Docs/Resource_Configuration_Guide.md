# 資源配置規範(WebGL / DOTS)

> 適用專案:PinionCore.Project2.Game
> 技術棧:Unity 6000.5.1f1、Entities 6.5(DOTS)、URP 17.5、PinionCore.Remote
> 發佈目標:WebGL
> 目的:在資源量還小的時候定調資源載入框架,避免上線 WebGL 時因記憶體 / 下載體積問題被迫重構。

---

## 0. 核心原則(TL;DR)

1. **全面改用 Addressables,禁用 `Resources/`。** WebGL 記憶體是硬限制,`Resources/` 的內容會全部打包進初始下載且無法卸載。
2. **一切以「同時載入量」為設計基準,而非「資源總量」。** 目標是讓瀏覽器 heap 峰值可控。
3. **資源與網路解耦。** PinionCore.Remote 只傳邏輯資料與 Addressables 尋址 key,絕不傳資源本身。
4. **命名與 group/label 規範先定,再開始堆資源。** 框架定好後,新增資源只是往框架裡填。

---

## 1. WebGL 硬限制(設計約束)

| 限制 | 說明 | 對策 |
|---|---|---|
| 記憶體 heap | 瀏覽器單頁通常控制在 2GB 內,越低越穩;超過會直接 crash | 按需載入 / 主動卸載,控制同時載入量 |
| 無多執行緒(預設) | WebGL 不支援一般 thread,載入不能靠背景執行緒硬扛 | 用 Addressables 非同步 API,分批載入、避免單幀大量解壓 |
| 下載體積 = 首屏時間 | 初始包越大,玩家等待越久,跳出率越高 | 初始包最小化,其餘走遠端 / 按需 |
| 貼圖壓縮格式受限 | WebGL 走 DXT/ASTC,格式選錯體積 / 品質失衡 | 統一壓縮 profile,依平台覆寫 |
| 音訊 | 大檔不設 streaming 會佔記憶體 | 大音樂設 Streaming,音效設 Compressed In Memory |

---

## 2. Addressables Group 分類

以「載入生命週期」而非「資源類型」來分組。建議下列 group:

| Group | 內容 | 打包 | 載入時機 | 卸載 |
|---|---|---|---|---|
| `Core_Preload` | UI shell、Loading 畫面、共用 shader、字型、關鍵 SO 設定 | 隨主程式 / 首包 | 啟動即載 | 不卸載(常駐) |
| `Shared_Persistent` | 跨關卡共用素材(共用材質、通用特效、共用音效) | Pack Together | 首次用到 | 遊戲結束才卸 |
| `Level_<name>` | 各關卡專屬場景、地形、佈景 | 每關卡一包 | 進入該關卡 | 離開關卡即卸 |
| `Character_<id>` | 各角色模型 / 動畫 / 材質 | 每角色一包 | 選用該角色時 | 不再使用時卸 |
| `Audio_Streaming` | BGM、長音檔 | 獨立包 | 需要時 | 用完即卸 |
| `Remote_Optional` | 非必要素材(額外造型、彩蛋、後續 DLC) | 遠端 CDN | 玩家觸發 | 用完即卸 |

**分包粒度原則:** 常一起使用的放同包(減少請求數),生命週期不同的一定拆開(才能各自卸載)。WebGL 請求數過多也有成本,不要拆到極碎。

### 現況待辦
- `Assets/Resources/Terrain.prefab` → 移出 `Resources/`,改為 `Level_*` 或 `Shared_Persistent` 的 Addressable。
- `Assets/WorldConfigs/*.asset`(World 設定 SO)→ 放 `Core_Preload`,以 Addressable key 尋址載入。

---

## 3. 命名與 Label 規範

### 資料夾
```
Assets/
  AddressableAssets/
    Core/              # Core_Preload
    Shared/            # Shared_Persistent
    Levels/<LevelName>/
    Characters/<CharId>/
    Audio/
    Remote/
```

### Address 命名
- 全小寫、以 `/` 分層,對應語意路徑,不綁實體資料夾:
  - `level/forest/terrain`
  - `character/hero/model`
  - `audio/bgm/battle01`
  - `core/ui/mainmenu`
- **禁止**用 GUID 或流水號當 address,程式端一律用常數集中管理(見 §6)。

### Label(跨 group 的橫向標籤)
- 平台:`webgl` / `standalone`(配合 variant 覆寫)
- 品質:`hq` / `lq`
- 用途:`preload`、`optional`
- Label 用來做「批次載入 / 依平台過濾」,不要拿來當唯一尋址依據。

---

## 4. 貼圖 / 材質壓縮 Profile

WebGL 建議統一走 **DXT(BC 系列)**,相容性最好;ASTC 檔案較小但需確認目標瀏覽器支援。

| 用途 | 格式(WebGL) | Max Size | 備註 |
|---|---|---|---|
| 角色 / 場景 Albedo | DXT5 (BC3) / DXT1 (BC1, 無 alpha) | 1024–2048 | 依重要性分級 |
| Normal map | BC5 | 1024 | |
| UI | DXT5,關 mipmap | 依實際尺寸 | Sprite Atlas 合圖 |
| 特效 / 遠景 | DXT,降 Max Size | 256–512 | |

原則:
- 一律開 **Platform Override(WebGL)**,不要沿用預設。
- 大量重複小圖用 **Sprite Atlas** 合併,減少請求與 draw call。
- 需要高低配時,用 Addressables **variant + label(`hq`/`lq`)** 產兩份,依裝置選載。

---

## 5. 音訊 Profile

| 用途 | Load Type | Compression | 備註 |
|---|---|---|---|
| BGM / 長音檔 | Streaming | Vorbis | 不佔常駐記憶體 |
| 一般音效 | Compressed In Memory | Vorbis | |
| 高頻短音效 | Decompress On Load | ADPCM/PCM | 量少才用 |

- 全部開 WebGL Platform Override。
- 長音檔務必 Streaming,否則整段解壓進記憶體。

---

## 6. 與 DOTS / SubScene 的整合

- Entities 的 **SubScene baking 產物(Entity Scenes)也要納入 Addressables 管理**,視為 `Level_*` 的一部分。
- 規劃哪些 SubScene 是**啟動常駐**、哪些是**按關卡串流載入 / 卸載**;用 `SceneSystem` 的非同步串流,避免單幀大量 instantiate。
- Baking 階段就決定資源引用走 Addressable,避免把大 mesh/texture 直接 hard reference 進 Entity Scene 而膨脹首包。
- URP 已有 `PC_RPAsset` / `Mobile_RPAsset` 兩檔品質層級 → **新增一個 WebGL 專用 RP tier**(降陰影解析度、關閉昂貴後處理、限制即時光源),納入 `Core_Preload`。

---

## 7. 資源與網路解耦(PinionCore.Remote)

- 伺服器 / 邏輯層**只傳資料**:實體 id、狀態、Addressable **key 字串**,不傳 prefab / texture / mesh 本身。
- 客戶端收到 key 後自行透過 Addressables 載入對應資源,載入狀態(loading / ready / failed)是**純客戶端關注點**,不進入網路同步。
- 建議建一個集中的 **`AddressKeys` 常數表**(或由 SO 產生),伺服器與客戶端共用同一份 key 定義,避免字串散落與不同步。
- 資源載入失敗要有 fallback(佔位資源 / 重試),不可阻塞邏輯或網路狀態機。

---

## 8. 初始下載包(首屏)應包含什麼

**只放「不載就進不了遊戲」的東西:**
- Loading / 開場 UI、進度條
- 主選單所需最小素材
- 共用 shader、字型、核心 SO 設定(含 WorldConfigs)
- WebGL RP 設定

**不應放進首包:** 任何關卡地形、角色模型、BGM、可選造型 → 全部按需 / 遠端。

---

## 9. 導入步驟(建議順序)

1. 安裝 `com.unity.addressables`(Entities 6.5 已含 ScriptableBuildPipeline 相依,相容)。
2. 建立 §3 的 `AddressableAssets/` 資料夾結構與 §2 的 groups。
3. 把 `Resources/Terrain.prefab`、`WorldConfigs` 遷入 Addressables,移除 `Resources/` 用法。
4. 設定貼圖 / 音訊的 WebGL Platform Override(§4、§5)。
5. 建立 `AddressKeys` 常數表,改寫載入程式走 Addressables 非同步 API。
6. 新增 WebGL RP tier。
7. 設定 Addressables Profile:本地 vs 遠端(CDN)路徑,`Remote_Optional` 走遠端。
8. 出一版 WebGL build,量測首包體積與執行期記憶體峰值,回頭調 group 粒度。

---

## 10. 驗收檢查清單

- [ ] 專案內已無 `Resources/` 資源引用
- [ ] 每個 group 都有明確的「載入時機 + 卸載時機」
- [ ] 貼圖 / 音訊皆有 WebGL Platform Override
- [ ] SubScene 已分類為常駐 / 串流
- [ ] 網路封包中不含任何資源二進位,只有 key 與資料
- [ ] 首包體積與記憶體峰值有實測數據並在可接受範圍
