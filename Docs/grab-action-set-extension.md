# 擴充新一套抓取動作組 使用說明

適用範圍:你手上有一系列新的成對抓取動畫(A=抓取者、B=被抓者),想在現有抓取系統
(commit `e7497c9` GrabResolver 配對系統、`2f11dbd` 動作選單圖示)之外加出「第二套抓取動作」。
以下以 `Grab2` 為佔位名,實際命名請換成你的招式名。

抓取系統零協議改動,擴充也一樣:**不需要動任何 Protocolable 介面**,
但 Shared 程式碼(ActionType / 轉移圖)是 server/client 共用,改完兩端同 commit 重編即可。

---

## 系統概觀(擴充前先懂資料流)

```
玩家出 UnarmedGrabStart(ActionConfig.HitEffect=Grab 帶 HitSegments)
  → HitResolver 掃描命中 → GrabResolver.EnqueueGrab(掃描中不動位置)
  → GrabResolver.Tick(同幀、掃描後)驗證並建立 pair:
      grabber ForceTransition → UnarmedGrabIdleA;victim → UnarmedGrabIdleB(面向 grabber)
  → 配對存續期間:
      - grabber 的 MoveInfo 加錨點偏移轉發給 victim(負 Speed = 面向 grabber 倒退走)
      - grabber 節點轉移鏡射到 victim(IdleA→IdleB、WalkA→WalkB)
      - Atk1A 不鏡射:HitSegments 命中 victim → Damage 路由 → 轉移圖 Damage=UnarmedGrabAtk1B
      - ThrowA:起手即解體,victim 自驅 ThrowB 的烘焙飛行
      - victim 出 UnarmedGrabBreakB(白名單唯一入口)→ 解體,grabber 進 UnarmedGrabBreakA 後搖
      - grabber 離開 grab 家族(如被第三方打進 UnarmedDamage)→ 自動解體
```

一套抓取動作組 = **11 個節點**:
`Start`(共用起手)、`IdleA/B`、`WalkA/B`、`Atk1A/B`、`ThrowA/B`、`BreakA/B`。
若你的新套路沒有拖行或補打,對應節點不建資產、不進轉移圖白名單即可;
家族描述(步驟 7)的該欄位留白(None)不會誤觸發——handler 比對的是實際轉移節點,
永遠不會是 None。

---

## 需要動到的東西總覽

| # | 位置 | 內容 |
|---|------|------|
| 1 | 動畫 FBX/clip | 匯入新動畫,A/B 成對 |
| 2 | `Shared/ActionType.cs` | 新 enum 成員(顯式數值,接在既有值之後) |
| 3 | `Configs/ActionConfigs/` | 每節點一顆 ActionConfig 資產 |
| 4 | 選單 `PinionCore/Bake Action Motions`、`PinionCore/Generate Action Animator States` | 烘焙位移段 + 重生 Animator states |
| 5 | `Configs/ActorConfigs/*.asset` | 把新資產註冊進角色的 config 清單 |
| 6 | `Shared/StandardTransitionProvider.cs` | 新節點的轉移表 + 出招入口 |
| 7 | `Worlds/GrabResolver.cs` | `_Families` 家族描述表加一筆 |
| 8 | `AddressableAssets/Icons/` + `Configs/ActionIconConfigs/` | client 選單圖示 |
| 9 | `Tests/Scripts/GrabTests.cs` | 配對/拖行/掙脫/丟投測試 |

路徑皆在 `PinionCore.Project2.Game/Assets/Project/` 下。

---

## 步驟 1:匯入動畫

- A/B clip 必須**成對等長**(現有資產 Atk1A/B 同為 0.833s、ThrowA/B 同為 1.633s、
  BreakA/B 同為 1.1s)——B 側表現是 A 側轉移的鏡射,長度不齊會出現一方先回 idle 的穿幫。
- FBX 若是外部素材,Rig 預設常是 Generic,要轉 **Humanoid**。
- B 側動畫的**作者視角是面向 grabber**:GrabResolver 轉發時 victim 的 Facing = -錨軸,
  ThrowB 的 root motion 位移是在「被抓者局部空間」自驅飛行,所以 ThrowB 的 clip
  位移方向要以「面向抓取者、向後飛出」編排。
- ThrowB 是整個 B 側唯一吃**真烘焙 root motion**的動作:確認該 clip 的匯入設定沒有
  bake into pose(`m_LoopBlendPositionXZ=1` 會烘出零位移段,飛不出去)。

## 步驟 2:ActionType 加成員

`Shared/ActionType.cs`。顯式數值接在現值之後(目前用到 22),**不得位移既有值**(資產存 int):

```csharp
// Grab2 家族:A = 抓取者、B = 被抓者
Grab2Start  = 23,
Grab2IdleA  = 24,
Grab2IdleB  = 25,
Grab2WalkA  = 26,
Grab2WalkB  = 27,
Grab2Atk1A  = 28,
Grab2Atk1B  = 29,
Grab2ThrowA = 30,
Grab2ThrowB = 31,
Grab2BreakA = 32,
Grab2BreakB = 33,
```

Animator state 名 = enum 名(產生器保證),client 端不需要再登記任何字典。

## 步驟 3:建 ActionConfig 資產

`Create > PinionCore > ActionConfig`,放 `Configs/ActionConfigs/`,命名照現例 `Grab2StartAction.asset`。
逐顆欄位(對照第一套實際值):

| 資產 | Loop | Redirectable | Interruptible | HoldRotation | HitEffect | BakeStationary | 備註 |
|------|:---:|:---:|:---:|:---:|:---|:---:|------|
| Grab2Start  | ✗ | ✗ | ✗ | ✓ | **Grab** | ✗ | **必填 HitSegments**(命中窗),否則抓取永不成立(OnValidate 會警告);單目標、首中即止 |
| Grab2IdleA  | ✓ | ✗ | ✗ | ✗ | Damage | ✗ | 抓住循環 |
| Grab2IdleB  | ✓ | ✗ | ✗ | ✗ | Damage | **✓** | 位置由轉發驅動,自身排程必須零位移 |
| Grab2WalkA  | ✓ | **✓** | ✗ | ✗ | Damage | ✗ | 拖行 locomotion,可重定向 |
| Grab2WalkB  | ✓ | ✗ | ✗ | ✗ | Damage | **✓** | 同 IdleB;Redirectable 必須 ✗(OnValidate 與 BakeStationary 互斥) |
| Grab2Atk1A  | ✗ | ✗ | ✗ | ✓ | Damage | ✗ | 補打:**帶 HitSegments**,B 反應走 Damage 路由(不是鏡射) |
| Grab2Atk1B  | ✗ | ✗ | ✗ | ✗ | Damage | **✓** | 受創反應,只由轉移圖 Damage 進入 |
| Grab2ThrowA | ✗ | ✗ | ✗ | ✓ | Damage | ✗ | 丟投起手 |
| Grab2ThrowB | ✗ | ✗ | ✗ | ✓ | Damage | ✗ | **真 root motion 飛行**,不是 stationary |
| Grab2BreakA | ✗ | ✗ | ✗ | ✓ | Damage | ✗ | 被掙脫後搖 |
| Grab2BreakB | ✗ | ✗ | ✗ | ✓ | Damage | ✗ | 掙脫動作 |

共通:`Action` 選對應 enum、`Stance = Battle`、`ChainWindow = 0`、`Clip` 指到烘焙來源動畫。
模型 prefab 的 Animator 若無 Avatar,`BakeRig` 指定有 Avatar 的 rig。
`Duration`/`Segments` 不用手填,烘焙器會寫。

## 步驟 4:烘焙

兩個選單都要跑(改過 Clip 之後永遠如此):

1. `PinionCore > Bake Action Motions` — 從 Clip 取樣寫入 `Duration`/`Segments`
   (BakeStationary 的資產只寫 Duration + 單一零位移段)。
2. `PinionCore > Generate Action Animator States` — 重生 AnimatorController 的 states
   (state 名 = enum 名)。**AnimatorController 是產生器產物,手編會被下次生成蓋掉。**

## 步驟 5:註冊進 ActorConfig

`Configs/ActorConfigs/ActorConfig.asset`、`ActorConfig2.asset`(以及任何要會這套動作的角色)
的 ActionConfigs 清單加入這 11 顆資產。沒註冊 = 該角色收到這些動作一律拒收。

## 步驟 6:轉移圖 StandardTransitionProvider

`Shared/StandardTransitionProvider.cs`,照第一套的樣板複製 11 個 `Transition` 並加進字典:

| 節點 | Playables(白名單) | Next | Damage |
|------|--------------------|------|--------|
| Grab2Start  | 空(起手不可動) | UnarmedIdle | UnarmedDamage |
| Grab2IdleA  | Grab2WalkA / Grab2Atk1A / Grab2ThrowA | Grab2IdleA | UnarmedDamage |
| Grab2WalkA  | 自身(=重定向)/ Grab2IdleA / Grab2Atk1A / Grab2ThrowA | Grab2IdleA | UnarmedDamage |
| Grab2Atk1A  | 空 | Grab2IdleA | UnarmedDamage |
| Grab2ThrowA | 空 | UnarmedIdle | UnarmedDamage |
| Grab2BreakA | 空 | UnarmedIdle | UnarmedDamage |
| Grab2IdleB  | **只有 Grab2BreakB** | Grab2IdleB | **Grab2Atk1B** |
| Grab2WalkB  | **只有 Grab2BreakB** | Grab2IdleB | **Grab2Atk1B** |
| Grab2Atk1B  | 空 | Grab2IdleB | Grab2Atk1B(連續挨打刷新) |
| Grab2ThrowB | 空 | UnarmedIdle | UnarmedDamage(配對已解除) |
| Grab2BreakB | 空 | UnarmedIdle | UnarmedDamage |

另外把 **Grab2Start 加進出招入口**:`battleIdle.Playables` 與 `battleWalk.Playables`
(第一套的 UnarmedGrabStart 就在這兩處)。

要點:
- 「A 側被第三方打會放開 B」不用寫任何規則——A 側 Damage 指到 UnarmedDamage,
  離開家族即觸發 GrabResolver 自動解體。
- B 側 Damage 指到 Grab2Atk1B 是「被抓中挨打(含抓取者補打)仍被抓」的關鍵,別指 UnarmedDamage。
- 轉移圖是 server 權威 + client 預測共用同一份,改完兩端同 commit 重編。

## 步驟 7:GrabResolver 加一筆家族描述

`Worlds/GrabResolver.cs` 的節點對映由 `_Families` 家族描述表驅動
(`GrabFamily`:Start/IdleA/IdleB/WalkA/WalkB/Atk1A/Atk1B/ThrowA/ThrowB/BreakA/BreakB
+ 每套自己的 `AnchorDistance` 錨距)。擴充新套 = 在表裡加一筆:

```csharp
new GrabFamily
{
    Start = ActionType.Grab2Start,
    IdleA = ActionType.Grab2IdleA,   IdleB = ActionType.Grab2IdleB,
    WalkA = ActionType.Grab2WalkA,   WalkB = ActionType.Grab2WalkB,
    Atk1A = ActionType.Grab2Atk1A,   Atk1B = ActionType.Grab2Atk1B,
    ThrowA = ActionType.Grab2ThrowA, ThrowB = ActionType.Grab2ThrowB,
    BreakA = ActionType.Grab2BreakA, BreakB = ActionType.Grab2BreakB,
    AnchorDistance = 0.9f,   // 依新動作組體型/招式目測調參
},
```

配對生命週期邏輯(鏡射/轉發/解體規則)全家族共通,只認欄位不認 enum 值。
**不要在 GrabResolver 的 handler 裡寫任何 `ActionType.XXX` 的硬比對**——
handler 的 else 分支語意是「離開所屬家族即解體」,寫死節點名會讓其他家族被誤解體。

也不要動 enqueue→Tick 結算與訂閱/退訂的順序:類註解裡的時序前提
(ForceTransition 同步 swap、stale-EndEvent 競態結構性不存在)依賴這個結構。

## 步驟 8:client 選單圖示

只有「玩家主動出的招」需要圖示:**Grab2Start、Grab2Atk1A、Grab2ThrowA、Grab2BreakB**
(Walk 類是 Loop,選單過濾掉;B 側其餘節點是被動進入,不進選單)。每顆:

1. `AddressableAssets/Icons/` 加 icon prefab(照 `UnarmedGrabStart.prefab` 複製改圖,設 Addressable)。
2. `Configs/ActionIconConfigs/` 建 `ActionIconConfig` 資產:`Action` 選 enum、`Icon` 指 prefab。
3. 把資產加進 `ActionIconConfigSet.asset` 的 Configs 清單。

沒登記不會壞,只是退回文字按鈕 fallback。

## 步驟 9:測試

- 伺服器邏輯照 `Tests/Scripts/GrabTests.cs` 樣板加 Grab2 的配對/拖行/掙脫/丟投/傷害路由案例。
- 圖示照 `ActionMenuIconTests.cs`:配對類轉移用合成方式驅動(真配對需要兩個 client)。
- E2E 手測注意:兩角色同點出招,目標在正後方**必不中**——要拉開距離並帶 direction 出招。

---

## 陷阱備忘

- `BakeStationary` 與 `Redirectable` 同開會被 OnValidate 擋下(語意矛盾)。
- `HitEffect=Grab` 沒填 HitSegments → 抓取永遠不成立(有警告,別忽略)。
- 抓取命中是**單目標**:同一次揮擊首中即止,由 HitResolver 的 victims 去重保證。
- 同幀互抓:先到先贏,後到方節點已被換走,`_TryEstablish` 的「當前動作 = 某家族 Start」
  驗證自動失敗——這是配對的唯一驗證點,別繞過。
- Shared 檔案(ActionType、轉移圖)server/client 同 repo 共用,改完一次重編兩端即可,
  但不要分兩個 commit 各改一半。
