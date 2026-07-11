using System.Collections;
using System.Collections.Generic;
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
    /// 多人可視性端到端測試:
    /// 比照 ActorDisplayNameTests 的四場景 Standalone 流程,
    /// 但同時連入兩個 GatewayClient(第二個為測試動態建立的 headless client,
    /// 不需要場景表現)模擬兩位玩家分別登入。
    /// 兩人進入同名世界後,雙方都應收到「自己 + 對方」共兩個 IActor,
    /// 且 Client 場景的 ActorProvider 應在 ActorRoot 底下生出兩個殼。
    /// </summary>
    public class ActorVisibilityTests
    {
        StandaloneSceneLoader _Scenes;
        PinionCore.NetSync.Standalone.Connector _ConnectorA;
        PinionCore.NetSync.Gateways.GatewayClient _ClientA;
        GameObject _ClientBObject;
        PinionCore.NetSync.Standalone.Connector _ConnectorB;
        PinionCore.NetSync.Gateways.GatewayClient _ClientB;
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
            while (listener == null || _ConnectorA == null)
            {
                if (listener == null)
                    listener = _Scenes.FindComponent<PinionCore.NetSync.Standalone.Listener>("Gateway", "SessionEndpoint");
                if (_ConnectorA == null)
                    _ConnectorA = _Scenes.FindComponent<PinionCore.NetSync.Standalone.Connector>("Client", "GatewayClient");
                yield return null;
            }
            _ClientA = _ConnectorA.GetComponent<PinionCore.NetSync.Gateways.GatewayClient>();

            // 第二位玩家:headless GatewayClient,Provider 沿用場景上的協議資產
            _ClientBObject = new GameObject("TestGatewayClientB");
            _ClientB = _ClientBObject.AddComponent<PinionCore.NetSync.Gateways.GatewayClient>();
            _ClientB.Provider = _ClientA.Provider;
            _ConnectorB = _ClientBObject.AddComponent<PinionCore.NetSync.Standalone.Connector>();

            // 等一個 frame:StandaloneStartToBind 綁定 Listener、User 場景的 GatewayService 註冊進 Router
            yield return null;

            _ConnectorA.Connect(listener);
            _ConnectorB.Connect(listener);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_ConnectorA != null && _ConnectorA.IsConnect())
                _ConnectorA.Disconnect();
            if (_ConnectorB != null && _ConnectorB.IsConnect())
                _ConnectorB.Disconnect();
            _ConnectorA = null;
            _ConnectorB = null;
            _ClientA = null;
            _ClientB = null;

            if (_ClientBObject != null)
                Object.Destroy(_ClientBObject);
            _ClientBObject = null;

            // 讓斷線的 session leave 流程跑完再卸場景
            yield return null;

            yield return _Scenes.UnloadAll();
            _Scenes.Dispose();
            _Scenes = null;

            Application.runInBackground = _PreviousRunInBackground;
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator TwoClientsSeeEachOtherTest()
        {
            const string PlayerNameA = "VisTesterA";
            const string PlayerNameB = "VisTesterB";

            // 先訂閱雙方的 IActor Supply 再登入,避免漏接進場廣播
            var actorsA = new List<IActor>();
            var actorsB = new List<IActor>();
            var subA = _ClientA.Queryer.QueryNotifier<IActor>().SupplyEvent().Subscribe(actorsA.Add);
            var subB = _ClientB.Queryer.QueryNotifier<IActor>().SupplyEvent().Subscribe(actorsB.Add);

            yield return _Verify(_ClientA.Queryer, PlayerNameA);
            yield return _Verify(_ClientB.Queryer, PlayerNameB);

            // 兩位玩家都在同一個世界時,各自應收到兩個 IActor(自己與對方)
            var deadline = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (actorsA.Count >= 2 && actorsB.Count >= 2)
                    break;
                yield return null;
            }
            Assert.AreEqual(2, actorsA.Count, "client A 應看到兩個 actor(自己與 client B)");
            Assert.AreEqual(2, actorsB.Count, "client B 應看到兩個 actor(自己與 client A)");

            // 雙方看到的是同一組玩家
            var expectedNames = new[] { PlayerNameA, PlayerNameB };
            CollectionAssert.AreEquivalent(expectedNames, actorsA.Select(a => a.DisplayName.Value).ToArray(),
                "client A 收到的 actor 名單應為兩位玩家");
            CollectionAssert.AreEquivalent(expectedNames, actorsB.Select(a => a.DisplayName.Value).ToArray(),
                "client B 收到的 actor 名單應為兩位玩家");

            // Client 場景的 ActorProvider 應在 ActorRoot 底下生出兩個殼
            var shells = 0;
            deadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < deadline)
            {
                shells = _CountShells();
                if (shells >= 2)
                    break;
                yield return null;
            }
            Assert.AreEqual(2, shells, "Client 場景 ActorRoot 底下應有兩個 ActorShell");

            subA.Dispose();
            subB.Dispose();
        }

        // 單一 client 的登入流程:等 IVerifiable → Verify 通過
        IEnumerator _Verify(PinionCore.Remote.INotifierQueryable queryer, string playerName)
        {
            var verifiableSupply = queryer.QueryNotifier<IVerifiable>().SupplyEvent()
                .First()
                .Timeout(System.TimeSpan.FromSeconds(10))
                .ToYieldInstruction(throwOnError: false);
            yield return verifiableSupply;
            Assert.IsFalse(verifiableSupply.HasError, $"{playerName}:連線後 client 應從 User 服務收到 IVerifiable");

            var verifyResult = verifiableSupply.Result.Verify(playerName).RemoteValue()
                .First()
                .Timeout(System.TimeSpan.FromSeconds(10))
                .ToYieldInstruction(throwOnError: false);
            yield return verifyResult;
            Assert.IsFalse(verifyResult.HasError, $"{playerName}:Verify 未收到回傳值");
            Assert.IsTrue(verifyResult.Result, $"{playerName}:首次註冊的名字 Verify 應回傳 true");
        }

        // 數 Client 場景中的殼;實例名稱是 "(Clone)" 結尾,不能靠物件名找
        int _CountShells()
        {
            var scene = SceneManager.GetSceneByName("Client");
            if (!scene.isLoaded)
                return 0;

            var count = 0;
            foreach (var root in scene.GetRootGameObjects())
                count += root.GetComponentsInChildren<PinionCore.Project2.Client.Actor>(true).Length;
            return count;
        }
    }
}
