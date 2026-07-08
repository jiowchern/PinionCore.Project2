using System.Collections;
using NUnit.Framework;
using UniRx;                       // First/Timeout/ToYieldInstruction 等 UniRx 擴充
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using PinionCore.NetSync.UniRx;    // SupplyEvent()/RemoteValue():把 INotifier<T>/Value<T> 轉成 IObservable
using PinionCore.Project2.Shared;
using PinionCore.Project2.Shared.Users;

namespace PinionCore.Project2.Tests
{
    /// <summary>
    /// Actor 移動端到端測試:
    /// 比照 ActorDisplayNameTests 的四場景 Standalone 流程,
    /// Verify 進入遊戲後取得 IActor / IPlayer,
    /// 由 Client.Player.Move 發出移動請求,World 依 ActorConfig.MoveSpeed 推進並發出 PathEvent,
    /// 驗證 Client.Actor 殼確實沿路徑移動到目的地。
    /// </summary>
    public class ActorMoveTests
    {
        // 與 Assets/Configs/ActorConfigs/ActorConfig.asset 一致:
        // 該 asset 未序列化 MoveSpeed,執行期使用 script 預設 1.0
        const float MoveSpeed = 1.0f;

        StandaloneSceneLoader _Scenes;
        PinionCore.NetSync.Standalone.Connector _Connector;
        PinionCore.NetSync.Gateways.GatewayClient _Client;
        bool _PreviousRunInBackground;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            // 編輯器失焦時 player loop 會停住,連線流程會卡住
            _PreviousRunInBackground = Application.runInBackground;
            Application.runInBackground = true;

            _Scenes = new StandaloneSceneLoader();

            // 先起 Gateway 與 World 的 Listener,
            // 再載 User 讓 GatewayService / WorldAgent 的 AutoConnector(已覆寫為 Standalone)連上
            yield return _Scenes.Load("Gateway");
            yield return _Scenes.Load("World");
            yield return _Scenes.Load("User");
            yield return _Scenes.Load("Client");

            // Gateway 場景有兩個 Listener(SessionEndpoint / RegistryEndpoint),必須用物件名區分
            PinionCore.NetSync.Standalone.Listener listener = null;
            while (listener == null || _Connector == null)
            {
                if (listener == null)
                    listener = _Scenes.FindComponent<PinionCore.NetSync.Standalone.Listener>("Gateway", "SessionEndpoint");
                if (_Connector == null)
                    _Connector = _Scenes.FindComponent<PinionCore.NetSync.Standalone.Connector>("Client", "GatewayClient");
                yield return null;
            }
            _Client = _Connector.GetComponent<PinionCore.NetSync.Gateways.GatewayClient>();

            // 等一個 frame:StandaloneStartToBind 綁定 Listener、User 場景的 GatewayService 註冊進 Router
            yield return null;

            _Connector.Connect(listener);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_Connector != null && _Connector.IsConnect())
                _Connector.Disconnect();
            _Connector = null;
            _Client = null;

            // 讓斷線的 session leave 流程跑完再卸場景
            yield return null;

            yield return _Scenes.UnloadAll();
            _Scenes.Dispose();
            _Scenes = null;

            Application.runInBackground = _PreviousRunInBackground;
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator MoveActorToDestinationTest()
        {
            const string PlayerName = "MoveTester";

            // 1. 連上 Gateway 後,Router 把 session 路由到 User 服務,收到 IVerifiable
            var verifiableSupply = _Client.Queryer.QueryNotifier<IVerifiable>().SupplyEvent()
                .First()
                .Timeout(System.TimeSpan.FromSeconds(10))
                .ToYieldInstruction(throwOnError: false);
            yield return verifiableSupply;
            Assert.IsFalse(verifiableSupply.HasError, "連線後 client 應從 User 服務收到 IVerifiable");
            var verifiable = verifiableSupply.Result;

            // 2. Verify 通過
            var verifyResult = verifiable.Verify(PlayerName).RemoteValue()
                .First()
                .Timeout(System.TimeSpan.FromSeconds(10))
                .ToYieldInstruction(throwOnError: false);
            yield return verifyResult;
            Assert.IsFalse(verifyResult.HasError, "Verify 未收到回傳值");
            Assert.IsTrue(verifyResult.Result, "首次註冊的名字 Verify 應回傳 true");

            // 3. Verify 後 UserGame 進入世界,World 把玩家以 IActor 同步回 client
            var actorSupply = _Client.Queryer.QueryNotifier<IActor>().SupplyEvent()
                .First()
                .Timeout(System.TimeSpan.FromSeconds(15))
                .ToYieldInstruction(throwOnError: false);
            yield return actorSupply;
            Assert.IsFalse(actorSupply.HasError, "Verify 通過後 client 應收到 IActor");

            // 4. 擁有者另外收到 IPlayer(可下移動指令的介面)
            var playerSupply = _Client.Queryer.QueryNotifier<IPlayer>().SupplyEvent()
                .First()
                .Timeout(System.TimeSpan.FromSeconds(15))
                .ToYieldInstruction(throwOnError: false);
            yield return playerSupply;
            Assert.IsFalse(playerSupply.HasError, "Verify 通過後 client 應收到 IPlayer");
            var playerGhost = playerSupply.Result;
            System.Guid actorId = playerGhost.ActorId;

            // 5. 以 ActorId 從 Client 場景找出「自己的」殼(場景中可能有其他玩家的殼)
            PinionCore.Project2.Client.Actor shell = null;
            var deadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < deadline)
            {
                shell = _FindActor(actorId);
                if (shell != null)
                    break;
                yield return null;
            }
            Assert.NotNull(shell, "ActorProvider 應在 Client 場景實例化出對應 ActorId 的 Client.Actor");
            var startPos = shell.Target.position;

            // 6. 透過 Client.Player 發出移動請求
            var clientPlayer = _Scenes.FindComponent<PinionCore.Project2.Client.Player>("Client", "Handlers");
            Assert.NotNull(clientPlayer, "Client 場景的 Handlers 應掛有 Client.Player");

            // Entrance 為 (0,0,0),距離約 2.24,MoveSpeed 1.0 → 約 2.3 秒抵達
            var destination = new Vector3(2f, 0f, 1f);
            clientPlayer.Move(destination);

            // 7. 輪詢殼的位置:先確認開始移動(PathEvent 已送達並播放),再等待抵達
            var destinationXZ = new Vector2(destination.x, destination.z);
            var travelDistance = Vector2.Distance(new Vector2(startPos.x, startPos.z), destinationXZ);
            var moved = false;
            deadline = Time.realtimeSinceStartup + travelDistance / MoveSpeed + 15f;
            while (Time.realtimeSinceStartup < deadline)
            {
                var pos = shell.Target.position;
                if (!moved && (pos - startPos).sqrMagnitude > 0.01f)
                    moved = true;
                if (Vector2.Distance(new Vector2(pos.x, pos.z), destinationXZ) < 0.05f)
                    break;
                yield return null;
            }

            Assert.IsTrue(moved, "收到 MoveEvent 後殼應開始移動");
            var finalPos = shell.Target.position;
            Assert.Less(Vector2.Distance(new Vector2(finalPos.x, finalPos.z), destinationXZ), 0.05f,
                "殼最終應移動到目的地(XZ)");
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator MoveRedirectTest()
        {
            yield return _EnterWorld("RedirectTester");

            // 擷取 ghost 收到的最新 MoveInfo(訂閱時 replay 保證有初始駐留值)
            MoveInfo? lastMove = null;
            var moveSub = UniRx.Observable.FromEvent<MoveInfo>(
                    h => _PlayerGhost.MoveEvent += h, h => _PlayerGhost.MoveEvent -= h)
                .Subscribe(info => lastMove = info);

            var startPos = _Shell.Target.position;
            _ClientPlayer.Move(new Vector3(2f, 0f, 1f));

            // 等殼開始移動後,途中改道
            var deadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if ((_Shell.Target.position - startPos).sqrMagnitude > 0.01f)
                    break;
                yield return null;
            }
            Assert.Greater((_Shell.Target.position - startPos).sqrMagnitude, 0.01f, "第一段移動應已開始");

            var destinationXZ = new Vector2(-1f, 2f);
            _ClientPlayer.Move(new Vector3(destinationXZ.x, 0f, destinationXZ.y));

            // 兩段距離總和(約 2.24 + 3.2)/ MoveSpeed + 緩衝
            deadline = Time.realtimeSinceStartup + 6f / MoveSpeed + 15f;
            while (Time.realtimeSinceStartup < deadline)
            {
                var pos = _Shell.Target.position;
                if (Vector2.Distance(new Vector2(pos.x, pos.z), destinationXZ) < 0.05f)
                    break;
                yield return null;
            }

            var finalPos = _Shell.Target.position;
            Assert.Less(Vector2.Distance(new Vector2(finalPos.x, finalPos.z), destinationXZ), 0.05f,
                "改道後殼應移動到第二個目的地(XZ)");
            Assert.IsTrue(lastMove.HasValue, "ghost 應收到 MoveEvent");
            Assert.Less(Vector2.Distance(lastMove.Value.Paths[lastMove.Value.Paths.Length - 1].End, destinationXZ), 0.05f,
                "改道後的 MoveInfo 終點應為第二個目的地");

            moveSub.Dispose();
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator MoveStopTest()
        {
            yield return _EnterWorld("StopTester");

            MoveInfo? lastMove = null;
            var moveSub = UniRx.Observable.FromEvent<MoveInfo>(
                    h => _PlayerGhost.MoveEvent += h, h => _PlayerGhost.MoveEvent -= h)
                .Subscribe(info => lastMove = info);

            var startPos = _Shell.Target.position;
            _ClientPlayer.Move(new Vector3(3f, 0f, 0f));

            // 等殼走出約 1 單位再喊停
            var deadline = Time.realtimeSinceStartup + 1f / MoveSpeed + 15f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if ((_Shell.Target.position - startPos).magnitude >= 1f)
                    break;
                yield return null;
            }
            Assert.GreaterOrEqual((_Shell.Target.position - startPos).magnitude, 1f, "移動應已進行約 1 單位");

            // 清空擷取值,等 Stop 產生的駐留 MoveInfo(Start==End),
            // 以免跟訂閱時 replay 的初始駐留混淆
            lastMove = null;
            _ClientPlayer.Stop();

            deadline = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (lastMove.HasValue)
                {
                    var p = lastMove.Value.Paths[0];
                    if (Vector2.Distance(p.Start, p.End) < 0.001f)
                        break;
                }
                yield return null;
            }
            Assert.IsTrue(lastMove.HasValue, "Stop 後 ghost 應收到 MoveEvent");
            var standPath = lastMove.Value.Paths[0];
            Assert.Less(Vector2.Distance(standPath.Start, standPath.End), 0.001f, "Stop 後應收到駐留路徑(Start==End)");

            // 確認殼真的停住:1 秒內位置不再變化
            var stopPos = _Shell.Target.position;
            var holdUntil = Time.realtimeSinceStartup + 1f;
            while (Time.realtimeSinceStartup < holdUntil)
                yield return null;

            Assert.Less((_Shell.Target.position - stopPos).magnitude, 0.02f, "Stop 後殼不應再移動");
            Assert.Less(Vector2.Distance(new Vector2(_Shell.Target.position.x, _Shell.Target.position.z), standPath.End), 0.1f,
                "殼的停止位置應與伺服器駐留點一致(容忍延遲取樣差)");

            moveSub.Dispose();
        }

        IPlayer _PlayerGhost;
        PinionCore.Project2.Client.Actor _Shell;
        PinionCore.Project2.Client.Player _ClientPlayer;

        // 共用進場流程:Verify → 取得 IPlayer → 以 ActorId 找到殼 → 取得 Client.Player
        IEnumerator _EnterWorld(string playerName)
        {
            var verifiableSupply = _Client.Queryer.QueryNotifier<IVerifiable>().SupplyEvent()
                .First()
                .Timeout(System.TimeSpan.FromSeconds(10))
                .ToYieldInstruction(throwOnError: false);
            yield return verifiableSupply;
            Assert.IsFalse(verifiableSupply.HasError, "連線後 client 應從 User 服務收到 IVerifiable");

            var verifyResult = verifiableSupply.Result.Verify(playerName).RemoteValue()
                .First()
                .Timeout(System.TimeSpan.FromSeconds(10))
                .ToYieldInstruction(throwOnError: false);
            yield return verifyResult;
            Assert.IsFalse(verifyResult.HasError, "Verify 未收到回傳值");
            Assert.IsTrue(verifyResult.Result, "首次註冊的名字 Verify 應回傳 true");

            var playerSupply = _Client.Queryer.QueryNotifier<IPlayer>().SupplyEvent()
                .First()
                .Timeout(System.TimeSpan.FromSeconds(15))
                .ToYieldInstruction(throwOnError: false);
            yield return playerSupply;
            Assert.IsFalse(playerSupply.HasError, "Verify 通過後 client 應收到 IPlayer");
            _PlayerGhost = playerSupply.Result;
            System.Guid actorId = _PlayerGhost.ActorId;

            _Shell = null;
            var deadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < deadline)
            {
                _Shell = _FindActor(actorId);
                if (_Shell != null)
                    break;
                yield return null;
            }
            Assert.NotNull(_Shell, "ActorProvider 應在 Client 場景實例化出對應 ActorId 的 Client.Actor");

            _ClientPlayer = _Scenes.FindComponent<PinionCore.Project2.Client.Player>("Client", "Handlers");
            Assert.NotNull(_ClientPlayer, "Client 場景的 Handlers 應掛有 Client.Player");
        }

        // 以 ActorId 從 Client 場景找出對應的殼;實例名稱是 "(Clone)" 結尾,不能靠物件名找
        PinionCore.Project2.Client.Actor _FindActor(System.Guid actorId)
        {
            var scene = SceneManager.GetSceneByName("Client");
            if (!scene.isLoaded)
                return null;

            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var actor in root.GetComponentsInChildren<PinionCore.Project2.Client.Actor>(true))
                {
                    if (actor.ActorId == actorId)
                        return actor;
                }
            }
            return null;
        }
    }
}
