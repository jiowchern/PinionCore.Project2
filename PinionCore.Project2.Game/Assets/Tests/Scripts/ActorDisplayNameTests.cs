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
        PinionCore.NetSync.QueryerHost _Client;
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
            PinionCore.NetSync.QueryerHost host = null;
            PinionCore.NetSync.Standalone.Listener listener = null;
            var found = TestWait.Until(() =>
            {
                if (host == null)
                    host = _Scenes.FindClientHost();
                if (host != null && _Connector == null)
                    _Connector = host.GetComponent<PinionCore.NetSync.Standalone.Connector>();
                if (host != null && listener == null)
                {
                    var locator = host.GetComponent<PinionCore.NetSync.Standalone.ListenerLocator>();
                    if (locator != null)
                        listener = locator.Find();
                }
                return listener != null && _Connector != null;
            }, System.TimeSpan.FromSeconds(30));
            yield return found;
            TestWait.AssertDone(found, "SetUp:應在時限內從 QueryHost 解析出 Connector 與 Standalone 連線目標");
            _Client = host;

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
            var verifiableSupply = TestWait.First(
                _Client.Queryer.QueryNotifier<IVerifiable>().SupplyEvent(),
                System.TimeSpan.FromSeconds(10));
            yield return verifiableSupply;
            TestWait.AssertDone(verifiableSupply, "連線後 client 應從 User 服務收到 IVerifiable");
            var verifiable = verifiableSupply.Result;

            // 2. Verify 通過
            var verifyResult = TestWait.First(
                verifiable.Verify(PlayerName).RemoteValue(),
                System.TimeSpan.FromSeconds(10));
            yield return verifyResult;
            TestWait.AssertDone(verifyResult, "Verify 未收到回傳值");
            Assert.IsTrue(verifyResult.Result, "首次註冊的名字 Verify 應回傳 true");

            // 3. Verify 後 UserGame 進入世界,World 把玩家以 IActor 同步回 client
            var actorSupply = TestWait.First(
                _Client.Queryer.QueryNotifier<IActor>().SupplyEvent(),
                System.TimeSpan.FromSeconds(15));
            yield return actorSupply;
            TestWait.AssertDone(actorSupply, "Verify 通過後 client 應收到 IActor");

            // 4. Client 場景的 ActorProvider 收到 Supply 後同步建立殼(Actor + 名牌);
            //    ActorProvider.SupplyEvent 會 replay 既有殼,晚訂閱安全。
            //    _Create 在 OnNext 前已同步跑完 shell.Setup,名牌可立即斷言。
            var provider = _Scenes.FindComponent<PinionCore.Project2.Client.ActorProvider>("Client", "Handlers");
            Assert.NotNull(provider, "Client 場景應有 ActorProvider");
            var shellWait = TestWait.First(provider.SupplyEvent(), System.TimeSpan.FromSeconds(15));
            yield return shellWait;
            TestWait.AssertDone(shellWait, "ActorProvider 應在 Client 場景實例化出 Client.Actor");
            var actorComponent = shellWait.Result;

            Assert.NotNull(actorComponent.DisplayName, "Actor prefab 應已設定 DisplayName 的 TMP 參考");
            Assert.AreEqual(PlayerName, actorComponent.DisplayName.text, "Actor 名牌應顯示 Verify 時的 DisplayName");

            // 5. 模型由 Actor 內部從 Addressables 非同步載入,掛在 Target 底下;
            //    載入完成沒有對外事件,以逐幀條件等待 CapsuleCollider 出現
            var modelLoaded = TestWait.Until(
                () => actorComponent.GetComponentInChildren<CapsuleCollider>(true) != null,
                System.TimeSpan.FromSeconds(15));
            yield return modelLoaded;
            TestWait.AssertDone(modelLoaded, "Actor 應從 Addressables 載入模型並掛在殼底下");
        }
    }
}
