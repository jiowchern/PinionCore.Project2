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
    /// 但同時連入兩個 client(第二個為測試動態複製連線物件產生的 headless client,
    /// 不需要場景表現,host 型別跟隨目前拓撲)模擬兩位玩家分別登入。
    /// 兩人進入同名世界後,雙方都應收到「自己 + 對方」共兩個 IActor,
    /// 且 Client 場景的 ActorProvider 應在 ActorRoot 底下生出兩個殼。
    /// </summary>
    public class ActorVisibilityTests
    {
        StandaloneSceneLoader _Scenes;
        PinionCore.NetSync.Standalone.Connector _ConnectorA;
        PinionCore.NetSync.QueryerHost _ClientA;
        GameObject _ClientBObject;
        PinionCore.NetSync.Standalone.Connector _ConnectorB;
        PinionCore.NetSync.QueryerHost _ClientB;
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

            // 從 QueryHost wrapper 解析目前拓撲的連線物件;Connector 與連線目標(ListenerLocator)都在其上
            // UnitySetUp 不受 [Timeout] 保護,找元件必須有界限,否則會掛死整輪
            PinionCore.NetSync.QueryerHost hostA = null;
            PinionCore.NetSync.Standalone.Listener listener = null;
            var found = TestWait.Until(() =>
            {
                if (hostA == null)
                    hostA = _Scenes.FindClientHost();
                if (hostA != null && _ConnectorA == null)
                    _ConnectorA = hostA.GetComponent<PinionCore.NetSync.Standalone.Connector>();
                if (hostA != null && listener == null)
                {
                    var locator = hostA.GetComponent<PinionCore.NetSync.Standalone.ListenerLocator>();
                    if (locator != null)
                        listener = locator.Find();
                }
                return listener != null && _ConnectorA != null;
            }, System.TimeSpan.FromSeconds(30));
            yield return found;
            TestWait.AssertDone(found, "SetUp:應在時限內從 QueryHost 解析出 Connector 與 Standalone 連線目標");
            _ClientA = hostA;

            // 第二位玩家:複製連線物件產生 headless client,
            // host 型別、Provider 與 Connector 都自動跟隨目前拓撲
            _ClientBObject = Object.Instantiate(hostA.gameObject);
            _ClientBObject.name = "TestClientB";
            _ClientB = _ClientBObject.GetComponent<PinionCore.NetSync.QueryerHost>();
            _ConnectorB = _ClientBObject.GetComponent<PinionCore.NetSync.Standalone.Connector>();

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

            // 先建等待(建構即訂閱)再登入,避免漏接進場廣播;
            // 逾時 60s 從訂閱(登入前)起算,涵蓋兩段 Verify(各自另有 10s 逾時)
            var actorsA = TestWait.Count(
                _Actors(_ClientA.Queryer), 2, System.TimeSpan.FromSeconds(60));
            var actorsB = TestWait.Count(
                _Actors(_ClientB.Queryer), 2, System.TimeSpan.FromSeconds(60));

            yield return _Verify(_ClientA.Queryer, PlayerNameA);
            yield return _Verify(_ClientB.Queryer, PlayerNameB);

            // 兩位玩家都在同一個世界時,各自應收到兩個 IActor(自己與對方)
            yield return actorsA;
            TestWait.AssertDone(actorsA, "client A 應看到兩個 actor(自己與 client B)");
            yield return actorsB;
            TestWait.AssertDone(actorsB, "client B 應看到兩個 actor(自己與 client A)");

            // 雙方看到的是同一組玩家
            var expectedNames = new[] { PlayerNameA, PlayerNameB };
            CollectionAssert.AreEquivalent(expectedNames, actorsA.Result.Select(a => a.DisplayName.Value).ToArray(),
                "client A 收到的 actor 名單應為兩位玩家");
            CollectionAssert.AreEquivalent(expectedNames, actorsB.Result.Select(a => a.DisplayName.Value).ToArray(),
                "client B 收到的 actor 名單應為兩位玩家");

            // Client 場景的 ActorProvider 應在 ActorRoot 底下生出兩個殼
            // (SupplyEvent 會 replay 既有殼,晚訂閱安全)
            var provider = _Scenes.FindComponent<PinionCore.Project2.Client.ActorProvider>("Client", "Handlers");
            Assert.NotNull(provider, "Client 場景應有 ActorProvider");
            var shells = TestWait.Count(provider.SupplyEvent(), 2, System.TimeSpan.FromSeconds(15));
            yield return shells;
            TestWait.AssertDone(shells, "Client 場景 ActorRoot 底下應有兩個 ActorShell");
        }

        // 統一入口:IActor 沿合約鏈(IUserEntry.Games → IGame.Players → IPlayer.Actors)取得
        System.IObservable<IActor> _Actors(PinionCore.Remote.INotifierQueryable queryer)
        {
            return queryer.QueryNotifier<IUserEntry>().SupplyEvent()
                .SelectMany(entry => entry.Games.SupplyEvent())
                .SelectMany(game => game.Player.SupplyEvent())
                .SelectMany(player => player.Actors.SupplyEvent());
        }

        // 單一 client 的登入流程:等 IVerifiable → Verify 通過
        IEnumerator _Verify(PinionCore.Remote.INotifierQueryable queryer, string playerName)
        {
            var verifiableSupply = TestWait.First(
                queryer.QueryNotifier<IUserEntry>().SupplyEvent()
                    .SelectMany(entry => entry.Verifiables.SupplyEvent()),
                System.TimeSpan.FromSeconds(10));
            yield return verifiableSupply;
            TestWait.AssertDone(verifiableSupply, $"{playerName}:連線後 client 應從 User 服務收到 IVerifiable");

            var verifyResult = TestWait.First(
                verifiableSupply.Result.Verify(playerName, CharactorType.Cube).RemoteValue(),
                System.TimeSpan.FromSeconds(10));
            yield return verifyResult;
            TestWait.AssertDone(verifyResult, $"{playerName}:Verify 未收到回傳值");
            Assert.IsTrue(verifyResult.Result, $"{playerName}:首次註冊的名字 Verify 應回傳 true");
        }
    }
}
