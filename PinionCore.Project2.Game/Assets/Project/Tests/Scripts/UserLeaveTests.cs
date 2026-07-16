using System.Collections;
using System.Linq;
using NUnit.Framework;
using UniRx;                       // First/Timeout/ToYieldInstruction 等 UniRx 擴充
using UnityEngine;
using UnityEngine.TestTools;
using PinionCore.NetSync.UniRx;    // SupplyEvent()/RemoteValue():把 INotifier<T>/Value<T> 轉成 IObservable
using PinionCore.Project2.Shared;
using PinionCore.Project2.Shared.Users;

namespace PinionCore.Project2.Tests
{
    /// <summary>
    /// 斷線清理端到端測試:
    /// 比照 ActorDisplayNameTests 的四場景 Standalone 流程,
    /// client 登入進入世界後主動斷線,
    /// User 服務的 Entry 應由 binder 找回 User 並 Dispose,
    /// 讓 UserGame.Leave 呼叫 world.Leave,伺服器世界的玩家數歸零。
    /// 斷言走伺服器側 Universe 的權威狀態(InternalsVisibleTo),
    /// 因為斷線的 client 自己收不到 Unsupply(見 gateway 斷線行為)。
    /// </summary>
    public class UserLeaveTests
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

        // 競態路徑:伺服器一出現玩家就斷線,Enter 回應多半尚未被 User 服務消化
        // (UserGame._Join 未跑),仰賴 _EnterWorld 的補償退場把 actor 清掉。
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator DisconnectRemovesPlayerFromWorldTest()
        {
            const string PlayerName = "LeaveTester";

            // 伺服器側的權威世界管理者,用來直接斷言玩家數
            var universe = _Scenes.FindComponent<PinionCore.Project2.Worlds.Universe>("World", "Universe");
            Assert.NotNull(universe, "World 場景應有 Universe");

            yield return _VerifyAs(PlayerName);

            // UserGame 走 QueryWorld → Enter 的非同步鏈進入世界;逐幀輪詢權威玩家數
            var entered = TestWait.Until(
                () => universe.WorldItems.SelectMany(w => w.PlayerItems).Count() == 1,
                System.TimeSpan.FromSeconds(15));
            yield return entered;
            TestWait.AssertDone(entered, "Verify 通過後伺服器世界應有一位玩家");

            // 模擬 client 斷線:session 關閉 → Entry._UserLeave 由 binder 找回 User 並 Dispose
            _Connector.Disconnect();

            var left = TestWait.Until(
                () => universe.WorldItems.SelectMany(w => w.PlayerItems).Count() == 0,
                System.TimeSpan.FromSeconds(15));
            yield return left;
            TestWait.AssertDone(left, "斷線後伺服器世界的玩家應被移除(world.Leave 未被呼叫?)");
        }

        // 正常路徑:等 client 收到 IActor(代表 UserGame._Join 已完成、dispose handlers 已註冊)
        // 再斷線,驗證 UserGame.Leave 的 world.Leave 清理。
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator DisconnectAfterJoinRemovesPlayerFromWorldTest()
        {
            const string PlayerName = "LeaveTester2";

            var universe = _Scenes.FindComponent<PinionCore.Project2.Worlds.Universe>("World", "Universe");
            Assert.NotNull(universe, "World 場景應有 Universe");

            // 先建等待再登入,避免漏接 IActor 供應;
            // IActor 沿合約鏈(IUserEntry.Games → IGame.Players → IPlayer.Actors)取得
            var actorSupply = TestWait.First(
                _Client.Queryer.QueryNotifier<IUserEntry>().SupplyEvent()
                    .SelectMany(entry => entry.Games.SupplyEvent())
                    .SelectMany(game => game.Player.SupplyEvent())
                    .SelectMany(player => player.Actors.SupplyEvent()),
                System.TimeSpan.FromSeconds(15));

            yield return _VerifyAs(PlayerName);

            // client 收到 IActor = _Join 已跑完(IGame/IView 已綁定、world.Leave handler 已註冊)
            yield return actorSupply;
            TestWait.AssertDone(actorSupply, "Verify 通過後 client 應收到 IActor");
            Assert.AreEqual(1, universe.WorldItems.SelectMany(w => w.PlayerItems).Count(),
                "client 收到 IActor 時伺服器世界應有一位玩家");

            _Connector.Disconnect();

            var left = TestWait.Until(
                () => universe.WorldItems.SelectMany(w => w.PlayerItems).Count() == 0,
                System.TimeSpan.FromSeconds(15));
            yield return left;
            TestWait.AssertDone(left, "斷線後伺服器世界的玩家應被移除(world.Leave 未被呼叫?)");
        }

        // 單一 client 的登入流程:等 IVerifier → Verify 通過
        IEnumerator _VerifyAs(string playerName)
        {
            var verifiableSupply = TestWait.First(
                _Client.Queryer.QueryNotifier<IUserEntry>().SupplyEvent()
                    .SelectMany(entry => entry.Verifiers.SupplyEvent()),
                System.TimeSpan.FromSeconds(10));
            yield return verifiableSupply;
            TestWait.AssertDone(verifiableSupply, "連線後 client 應從 User 服務收到 IVerifier");

            var verifyResult = TestWait.First(
                verifiableSupply.Result.Verify(playerName, ModelType.Cube).RemoteValue(),
                System.TimeSpan.FromSeconds(10));
            yield return verifyResult;
            TestWait.AssertDone(verifyResult, "Verify 未收到回傳值");
            Assert.IsTrue(verifyResult.Result, "首次註冊的名字 Verify 應回傳 true");
        }
    }
}
