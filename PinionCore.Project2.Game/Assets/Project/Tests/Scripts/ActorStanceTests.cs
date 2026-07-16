using System.Collections;
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
    /// 冒險/戰鬥狀態切換端到端測試:
    /// 比照 ActorMoveTests 的四場景 Standalone 流程,進場後驗證
    /// IActor.StanceEvent 訂閱即 replay 初始冒險狀態;
    /// 經 IAdventure.ToBattle / IBattle.ToAdventure 切換伺服器狀態機時,
    /// 狀態由 world 端 Adventure/Battle 子狀態切換時廣播到 IActor ghost,殼(Client.Actor.Status)跟著切換。
    /// </summary>
    public class ActorStanceTests
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

            yield return null;

            yield return _Scenes.UnloadAll();
            _Scenes.Dispose();
            _Scenes = null;

            Application.runInBackground = _PreviousRunInBackground;
        }

        /// <summary>
        /// 初始 replay 冒險 → ToBattle 廣播戰鬥 → ToAdventure 廣播回冒險,
        /// 每段都驗證 IActor ghost 的 StanceEvent 與殼的 Status。
        /// </summary>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator StatusSwitchTest()
        {
            yield return _EnterWorld("StatusTester");

            // 訂閱即 replay:新訂閱應收到當前狀態(晚一個網路往返)
            var replay = TestWait.First(TestWait.StanceEvents(_ActorGhost), System.TimeSpan.FromSeconds(10));
            yield return replay;
            TestWait.AssertDone(replay, "StanceEvent 訂閱後應 replay 當前狀態");
            Assert.AreEqual(StanceType.Adventure, replay.Result, "進場初始狀態應為冒險");
            Assert.AreEqual(StanceType.Adventure, _Shell.Stance, "殼的初始狀態應為冒險");

            // IAdventure 只供應給本地玩家(IPlayer.Adventure,world 端子狀態開關);
            // 進場即冒險狀態,replay 保證晚訂閱可取得
            var adventureSupply = TestWait.First(
                _PlayerGhost.Adventure.SupplyEvent(),
                System.TimeSpan.FromSeconds(15));
            yield return adventureSupply;
            TestWait.AssertDone(adventureSupply, "進場後應供應 IAdventure");

            // 切戰鬥:RPC 可能與 soul 綁定競態掉失,單次逾時重訂閱(replay)+重送;
            // IBattle 供應抵達 = 伺服器狀態機已切換
            var adventure = adventureSupply.Result;
            var battleSupply = TestWait.FirstWithRetry(
                () => _PlayerGhost.Battle.SupplyEvent(),
                onAttempt: () => adventure.ToBattle().RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return battleSupply;
            TestWait.AssertDone(battleSupply, "ToBattle 後應供應 IBattle");

            // 狀態廣播抵達 IActor ghost(晚訂閱安全:已切換則 replay 即滿足)與殼
            var battleStatus = TestWait.First(
                TestWait.StanceEvents(_ActorGhost), s => s == StanceType.Battle, System.TimeSpan.FromSeconds(10));
            yield return battleStatus;
            TestWait.AssertDone(battleStatus, "ToBattle 後 StanceEvent 應廣播戰鬥狀態");

            var shellBattle = TestWait.Until(() => _Shell.Stance == StanceType.Battle, System.TimeSpan.FromSeconds(10));
            yield return shellBattle;
            TestWait.AssertDone(shellBattle, "殼應切到戰鬥狀態");

            // 切回冒險(同樣的重送保護;ToAdventure 重送若打在已解除的 soul 上會被靜默丟棄,無害)
            var battle = battleSupply.Result;
            var adventureBack = TestWait.FirstWithRetry(
                () => _PlayerGhost.Adventure.SupplyEvent(),
                onAttempt: () => battle.ToAdventure().RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return adventureBack;
            TestWait.AssertDone(adventureBack, "ToAdventure 後應重新供應 IAdventure");

            var adventureStatus = TestWait.First(
                TestWait.StanceEvents(_ActorGhost), s => s == StanceType.Adventure, System.TimeSpan.FromSeconds(10));
            yield return adventureStatus;
            TestWait.AssertDone(adventureStatus, "ToAdventure 後 StanceEvent 應廣播冒險狀態");

            var shellAdventure = TestWait.Until(() => _Shell.Stance == StanceType.Adventure, System.TimeSpan.FromSeconds(10));
            yield return shellAdventure;
            TestWait.AssertDone(shellAdventure, "殼應切回冒險狀態");
        }

        IPlayer _PlayerGhost;
        IActor _ActorGhost;
        PinionCore.Project2.Client.Actor _Shell;

        // 統一入口:entry.Games 合約鏈(能力介面 IAdventure/IBattle 由 IPlayer 供應)
        System.IObservable<IGame> _Games()
        {
            return _Client.Queryer.QueryNotifier<IUserEntry>().SupplyEvent()
                .SelectMany(entry => entry.Games.SupplyEvent());
        }

        // 共用進場流程:Verify → 取得 IPlayer / IActor ghost → 等 ActorProvider 建出對應殼
        IEnumerator _EnterWorld(string playerName)
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

            var playerSupply = TestWait.First(
                _Games().SelectMany(game => game.Player.SupplyEvent()),
                System.TimeSpan.FromSeconds(15));
            yield return playerSupply;
            TestWait.AssertDone(playerSupply, "Verify 通過後 client 應收到 IPlayer");
            _PlayerGhost = playerSupply.Result;
            System.Guid actorId = _PlayerGhost.ActorId;

            // 自身的 IActor ghost(經 IPlayer.Actors 供應):StanceEvent 的權威狀態來源
            var actorSupply = TestWait.First(
                _PlayerGhost.Actors.SupplyEvent(), a => a.ActorId == actorId,
                System.TimeSpan.FromSeconds(15));
            yield return actorSupply;
            TestWait.AssertDone(actorSupply, "client 應收到自身的 IActor ghost");
            _ActorGhost = actorSupply.Result;

            // ActorProvider.SupplyEvent 會 replay 既有殼,晚訂閱安全
            var provider = _Scenes.FindComponent<PinionCore.Project2.Client.ActorProvider>("Client", "Handlers");
            Assert.NotNull(provider, "Client 場景應有 ActorProvider");
            var shellWait = TestWait.First(provider.SupplyEvent(), a => a.ActorId == actorId, System.TimeSpan.FromSeconds(15));
            yield return shellWait;
            TestWait.AssertDone(shellWait, "ActorProvider 應在 Client 場景實例化出對應 ActorId 的 Client.Actor");
            _Shell = shellWait.Result;
        }
    }
}
