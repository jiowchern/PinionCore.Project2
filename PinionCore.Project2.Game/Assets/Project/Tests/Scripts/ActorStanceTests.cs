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
    /// 冒險/戰鬥表現狀態切換端到端測試:
    /// 比照 ActorMoveTests 的四場景 Standalone 流程。StanceEvent 已拆除,
    /// 表現狀態由 IActor.ActionEvent 廣播的 ActionType 查 ActionConfig.Stance:
    /// 經 IControllable.Play(UnarmedIdle / AdventureIdle) 驅動 world 端控制狀態機轉移時,
    /// TransitionEvent 廣播新 Transition(Current 隨之切換;soul 恆存,不再換 soul),
    /// ActionEvent 廣播戰鬥/冒險系動作,殼(Client.ActorShell.Stance)推導跟著切換。
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
        /// 初始 replay 冒險系 → Play(UnarmedIdle) 廣播戰鬥系動作 → Play(AdventureIdle) 廣播回冒險系,
        /// 每段都驗證新供應 soul 的 Transition.Current、ActionEvent 的 ActionType 推導與殼的 Stance。
        /// </summary>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator StanceSwitchTest()
        {
            yield return _EnterWorld("StatusTester");

            // 訂閱即 replay:新訂閱應收到當前動作(晚一個網路往返),stance 由 ActionConfig.Stance 查表
            var replay = TestWait.First(
                TestWait.ActionEvents(_ActorGhost), a => a.Action != ActionType.None,
                System.TimeSpan.FromSeconds(10));
            yield return replay;
            TestWait.AssertDone(replay, "ActionEvent 訂閱後應 replay 當前動作");
            Assert.AreEqual(ActionType.AdventureIdle, replay.Result.Action, "進場初始動作應為冒險 idle");
            Assert.AreEqual(StanceType.Adventure, _Shell.Stance, "殼的初始狀態應為冒險");

            // IControllable 只供應給本地玩家(IPlayer.Controllable),soul 與角色同生命週期;
            // 進場即供應,supply replay 保證晚訂閱可取得
            var idleSupply = TestWait.First(
                _PlayerGhost.Controllable.SupplyEvent(),
                System.TimeSpan.FromSeconds(15));
            yield return idleSupply;
            TestWait.AssertDone(idleSupply, "進場後應供應 IControllable");
            var controllable = idleSupply.Result;

            // TransitionEvent 訂閱即回放當前 Transition(晚一個網路往返)
            var idleTransition = TestWait.First(
                TestWait.TransitionEvents(controllable),
                System.TimeSpan.FromSeconds(10));
            yield return idleTransition;
            TestWait.AssertDone(idleTransition, "TransitionEvent 訂閱後應回放當前 Transition");
            Assert.AreEqual(ActionType.AdventureIdle, idleTransition.Result.Current.Action,
                "進場初始控制狀態應為 AdventureIdle");

            // 切戰鬥:RPC 掉失保護,單次逾時重訂閱(replay)+重送;
            // Current==UnarmedIdle 的 TransitionEvent 抵達 = 伺服器狀態機已切換
            var battleTransition = TestWait.FirstWithRetry(
                () => TestWait.TransitionEvents(controllable)
                    .Where(t => t.Current.Action == ActionType.UnarmedIdle),
                onAttempt: () => controllable.Play(ActionType.UnarmedIdle, Vector2.zero).RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return battleTransition;
            TestWait.AssertDone(battleTransition, "Play(UnarmedIdle) 後控制狀態應切到 Current==UnarmedIdle");

            // 戰鬥系動作廣播抵達 IActor ghost(晚訂閱安全:已切換則 replay 即滿足)與殼推導
            var battleAction = TestWait.First(
                TestWait.ActionEvents(_ActorGhost),
                a => a.Action == ActionType.UnarmedIdle, System.TimeSpan.FromSeconds(10));
            yield return battleAction;
            TestWait.AssertDone(battleAction, "Play(UnarmedIdle) 後 ActionEvent 應廣播戰鬥系動作");

            var shellBattle = TestWait.Until(() => _Shell.Stance == StanceType.Battle, System.TimeSpan.FromSeconds(10));
            yield return shellBattle;
            TestWait.AssertDone(shellBattle, "殼應推導切到戰鬥狀態");

            // 切回冒險(同樣的重送保護;重送不在白名單只回 false,無害)
            var adventureBack = TestWait.FirstWithRetry(
                () => TestWait.TransitionEvents(controllable)
                    .Where(t => t.Current.Action == ActionType.AdventureIdle),
                onAttempt: () => controllable.Play(ActionType.AdventureIdle, Vector2.zero).RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return adventureBack;
            TestWait.AssertDone(adventureBack, "Play(AdventureIdle) 後控制狀態應切回 Current==AdventureIdle");

            var adventureAction = TestWait.First(
                TestWait.ActionEvents(_ActorGhost),
                a => a.Action == ActionType.AdventureIdle,
                System.TimeSpan.FromSeconds(10));
            yield return adventureAction;
            TestWait.AssertDone(adventureAction, "Play(AdventureIdle) 後 ActionEvent 應廣播冒險系動作");

            var shellAdventure = TestWait.Until(() => _Shell.Stance == StanceType.Adventure, System.TimeSpan.FromSeconds(10));
            yield return shellAdventure;
            TestWait.AssertDone(shellAdventure, "殼應推導切回冒險狀態");
        }

        IPlayer _PlayerGhost;
        IActor _ActorGhost;
        PinionCore.Project2.Client.ActorShell _Shell;

        // 統一入口:entry.Games 合約鏈(控制能力 IControllable 由 IPlayer.Controllable 供應)
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

            // 自身的 IActor ghost(經 IPlayer.Actors 供應):ActionEvent 的權威狀態來源
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
            TestWait.AssertDone(shellWait, "ActorProvider 應在 Client 場景實例化出對應 ActorId 的 Client.ActorShell");
            _Shell = shellWait.Result;
        }
    }
}
