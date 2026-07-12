using System.Collections;
using System.Linq;
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
    /// Client.Actor 端到端測試:
    /// 比照 ViewTimeTicksTests 的四場景 Standalone 流程,
    /// Verify 進入遊戲後 client 收到 IActor,
    /// ActorProvider 依 ActorConfig 從 Addressables 實例化 Client.Actor,
    /// 驗證 TMP 名牌顯示的 DisplayName 與 Verify 的名字一致。
    /// </summary>
    public class ActorDisplayNameTests
    {
        StandaloneSceneLoader _Scenes;
        PinionCore.NetSync.Standalone.Connector _Connector;
        PinionCore.NetSync.Client _Client;
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

            // 直連實驗:Client 直接連 User 場景 GatewayService 上的 Standalone Listener(不經 Gateway)
            PinionCore.NetSync.Standalone.Listener listener = null;
            while (listener == null || _Connector == null)
            {
                if (listener == null)
                    listener = _Scenes.FindComponent<PinionCore.NetSync.Standalone.Listener>("User", "GatewayService");
                if (_Connector == null)
                    _Connector = _Scenes.FindComponent<PinionCore.NetSync.Standalone.Connector>("Client", "GatewayClient");
                yield return null;
            }
            _Client = _Connector.GetComponent<PinionCore.NetSync.Client>();

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
        public IEnumerator VerifyThenReceiveActorTest()
        {
            const string PlayerName = "ActorTester";

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

            // 4. Client 場景的 ActorProvider 收到 Supply 後同步建立殼(Actor + 名牌),
            //    輪詢直到名牌文字就緒(殼建立與 Setup 皆同步,但 Supply 事件本身跨 frame)
            PinionCore.Project2.Client.Actor actorComponent = null;
            var deadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < deadline)
            {
                actorComponent = _FindActor();
                if (actorComponent != null &&
                    actorComponent.DisplayName != null &&
                    !string.IsNullOrEmpty(actorComponent.DisplayName.text))
                    break;
                yield return null;
            }

            Assert.NotNull(actorComponent, "ActorProvider 應在 Client 場景實例化出 Client.Actor");
            Assert.NotNull(actorComponent.DisplayName, "Actor prefab 應已設定 DisplayName 的 TMP 參考");
            Assert.AreEqual(PlayerName, actorComponent.DisplayName.text, "Actor 名牌應顯示 Verify 時的 DisplayName");

            // 5. 模型由 Actor 內部從 Addressables 非同步載入,掛在 Target 底下;
            //    以 CapsuleCollider 辨識模型實例(殼與 TMP 名牌都沒有 collider)
            CapsuleCollider model = null;
            deadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < deadline)
            {
                model = actorComponent.GetComponentInChildren<CapsuleCollider>(true);
                if (model != null)
                    break;
                yield return null;
            }
            Assert.NotNull(model, "Actor 應從 Addressables 載入模型並掛在殼底下");
        }

        // 從 Client 場景搜出第一個 Client.Actor;實例名稱是 "(Clone)" 結尾,不能靠物件名找
        PinionCore.Project2.Client.Actor _FindActor()
        {
            var scene = SceneManager.GetSceneByName("Client");
            if (!scene.isLoaded)
                return null;

            foreach (var root in scene.GetRootGameObjects())
            {
                var found = root.GetComponentsInChildren<PinionCore.Project2.Client.Actor>(true).FirstOrDefault();
                if (found != null)
                    return found;
            }
            return null;
        }
    }
}
