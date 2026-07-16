# ProjectGame2

## 命名規則(2026-07 整頓定案,新程式碼必須遵守)

### Status vs Stance
- `Status` 保留給**狀態機**:`PinionCore.Utility.IStatus`/`StatusMachine` 與 world 端的 `-Status` 狀態類(`ControllerStatus`、`UnconsciousStatus`…)。
- **表現狀態**一律 `Stance`:`StanceType` 與 client 殼 `ActorShell.Stance`。Stance 不再獨立過線(`IActor.StanceEvent`/`Player.SetStance` 已拆除),一律由 `ActionType` 推導(`ActionTypeExtensions.StanceOf`)——ActionType 自帶 stance 語意(BattleX/AdventureX)。
- Animator 參數名仍是字串 `"status"`(asset 側,勿在 C# 端混用)。

### Notifier / Depot 屬性單複數 = 供應數量
`Notifier<T>` 型別本身看不出會供應一個還是多個實例,以屬性名單複數標示:
- **單數 = 至多供應一個**:`IPlayer.Controllable`、`IGame.Player/View`。
- **複數 = 會供應多個**:`IPlayer.Actors`、`IUniverse.Worlds`、`IUserEntry.Verifiers/Games`、`IWorld.Players`。
- 內部 Depot 走訪屬性用 `-Items` 後綴:`ControllerItems`、`PlayerItems`、`WorldItems`。
- 「XxxNotifier」屬性後綴風格已淘汰,不要新增。

### 一詞一義
- 驗證鏈用 **Verify** 一詞:`IVerifier.Verify()`、`IUserEntry.Verifiers`、`UserVerifier`。不要引入 Login/Auth 混用。
- 角色拼字是 **Character**(`ICharacter`),不是 Charactor。
- 登入選角的外觀選項是 `ModelType`(映射到 `ActorInfo.ModelName`;刻意不用 Avatar,避免與 Unity Animator Avatar 混淆)。

### 跨層類別命名
- **server(Worlds)端保留裸領域名**:`Player`(模擬核心)、`PlayerController`(協議曝光面+狀態機)、`World`、`Universe`。
- **client 端 MonoBehaviour 帶表現層角色後綴**:`ActorShell`(殼,表現層)、`PlayerRemote`(RPC 發送器)、`-Handler`(輸入/鏡頭/時間等處理器)。
- 場景啟動器以模組名+Entry:`UsersEntry`、`WorldsEntry`(不叫 `UserEntry`,避免與協議介面 `IUserEntry` 混淆)。
- 過線資料 struct 用 `-Info` 後綴:`MoveInfo`、`ActionInfo`、`ActorInfo`。

### 協議介面改名注意
- 標了 `Protocolable` 的介面改名是 breaking change:server/client 必須同 commit 重編(協議由 source generator 重生,本專案兩端同 repo,一次重編即可)。
- 改 MonoBehaviour 類名必須連 `.cs` 檔名一起改、`.meta` 跟著搬(GUID 保留,scene/prefab 引用不斷);scene YAML 裡的 `m_EditorClassIdentifier` 與 UnityEvent 的 `m_TargetAssemblyTypeName` 字串要手動同步。
- 改 MonoBehaviour 的**序列化欄位名**會斷 scene/prefab 資料,需 `[FormerlySerializedAs]`。
