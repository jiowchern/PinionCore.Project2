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

            Assert.IsTrue(moved, "收到 PathEvent 後殼應開始移動");
            var finalPos = shell.Target.position;
            Assert.Less(Vector2.Distance(new Vector2(finalPos.x, finalPos.z), destinationXZ), 0.05f,
                "殼最終應移動到目的地(XZ)");

            // 8. 伺服器權威位置:到達時才更新 Position 屬性,等它同步過來,確認與前端一致
            deadline = Time.realtimeSinceStartup + 15f;
            Vector3 serverPos = playerGhost.Position;
            while (Time.realtimeSinceStartup < deadline)
            {
                serverPos = playerGhost.Position;
                if (Vector2.Distance(new Vector2(serverPos.x, serverPos.z), destinationXZ) < 0.05f)
                    break;
                yield return null;
            }
            Assert.Less(Vector2.Distance(new Vector2(serverPos.x, serverPos.z), destinationXZ), 0.05f,
                "伺服器權威位置(IActor.Position)到達後應與目的地一致");
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
