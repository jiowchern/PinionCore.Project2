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
    /// 走路(Locomotion)動作端到端測試:
    /// 比照 ActorMoveTests 的四場景 Standalone 流程,
    /// Play(AdventureWalk) → IActor.ActionEvent 廣播 AdventureWalk(循環動作,不發 None)
    /// → 殼跟分段 MoveInfo 位移且旋轉不凍結(面向移動方向)
    /// → Play(AdventureIdle) 停走 → ActionEvent 廣播 AdventureIdle + 駐留(取代制,無中間 None);
    /// 走路中攻擊直接取代(BattleWalk→BattleAttack)、攻擊完自動回 BattleIdle 後可重新起走。
    /// </summary>
    public class ActorWalkTests
    {
        StandaloneSceneLoader _Scenes;
        PinionCore.NetSync.Standalone.Connector _Connector;
        PinionCore.NetSync.QueryerHost _Client;
        bool _PreviousRunInBackground;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _PreviousRunInBackground = Application.runInBackground;
            Application.runInBackground = true;

            _Scenes = new StandaloneSceneLoader();

            yield return _Scenes.Load("Gateway");
            yield return _Scenes.Load("World");
            yield return _Scenes.Load("User");
            yield return _Scenes.Load("Client");

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

        // 等待帶指定 Current 動作的控制 soul 供應:狀態轉移 = 新 soul 供應,
        // SupplyEvent 有 replay,已轉移完成的晚訂閱也安全
        ObservableYieldInstruction<IControllable> _WaitControllable(ActionType current, IControllable sender, ActionType play, Vector2 direction)
        {
            return TestWait.FirstWithRetry(
                () => _PlayerGhost.Controllable.SupplyEvent()
                    .Where(c => c.Transition.Value.Current.Action == current),
                onAttempt: () => sender.Play(play, direction).RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
        }

        /// <summary>
        /// 走路生命週期:Play(AdventureWalk) → ActionEvent 廣播 AdventureWalk、殼跟分段 MoveInfo 位移、
        /// 旋轉不凍結(面向移動方向);Play(AdventureIdle) → ActionEvent 廣播 AdventureIdle + 駐留,殼停住。
        /// </summary>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator WalkLifecycleTest()
        {
            yield return _EnterWorld("WalkTester");

            var startPos = _Shell.Target.position;

            // 起走 → 等 ActionEvent 廣播 AdventureWalk(訂閱 replay 是 AdventureIdle,被 predicate 濾掉);
            // RPC 可能與 soul 轉移競態掉失,逾時重訂閱+重送(重送打在舊 soul 上被靜默丟棄,無害)
            var walkEvent = TestWait.FirstWithRetry(
                () => TestWait.ActionEvents(_ActorGhost).Where(a => a.Action == ActionType.AdventureWalk),
                onAttempt: () => _ControllableGhost.Play(ActionType.AdventureWalk, new Vector2(1f, 0f)).RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return walkEvent;
            TestWait.AssertDone(walkEvent, "Play(AdventureWalk) 後 ActionEvent 應廣播 AdventureWalk");
            var walkStartTicks = walkEvent.Result.StartTicks;

            // 殼跟著分段 MoveInfo 位移
            var displaced = TestWait.Until(
                () => (_Shell.Target.position - startPos).magnitude > 0.5f,
                System.TimeSpan.FromSeconds(15));
            yield return displaced;
            TestWait.AssertDone(displaced, "走路期間殼應跟著分段 MoveInfo 位移");

            // 走路不凍結旋轉:殼面向移動方向(+X);位移方向也應沿 +X
            Assert.Greater(_Shell.Target.forward.x, 0.9f, "走路中殼應面向移動方向(旋轉不凍結)");
            var displacement = _Shell.Target.position - startPos;
            var directionXZ = new Vector2(displacement.x, displacement.z).normalized;
            Assert.Greater(Vector2.Dot(directionXZ, new Vector2(1f, 0f)), 0.98f, "位移方向應沿指令方向");

            // 停走:取得走路狀態的新 soul,Play(AdventureIdle) 轉移回 idle
            //(取代制:直接廣播 AdventureIdle,無中間 None)+ 駐留 MoveInfo
            var walkSoul = TestWait.First(
                _PlayerGhost.Controllable.SupplyEvent()
                    .Where(c => c.Transition.Value.Current.Action == ActionType.AdventureWalk),
                System.TimeSpan.FromSeconds(10));
            yield return walkSoul;
            TestWait.AssertDone(walkSoul, "走路中應供應 Current==AdventureWalk 的控制 soul");

            var stand = TestWait.First(
                TestWait.MoveEvents(_ActorGhost), i => i.Speed == 0f, System.TimeSpan.FromSeconds(10));
            var idleEvent = TestWait.FirstWithRetry(
                () => TestWait.ActionEvents(_ActorGhost)
                    .Where(a => a.Action == ActionType.AdventureIdle && a.StartTicks > walkStartTicks),
                onAttempt: () => walkSoul.Result.Play(ActionType.AdventureIdle, Vector2.zero).RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return idleEvent;
            TestWait.AssertDone(idleEvent, "Play(AdventureIdle) 後 ActionEvent 應廣播 AdventureIdle");
            yield return stand;
            TestWait.AssertDone(stand, "停走後應收到駐留 MoveInfo(Speed==0)");

            // 殼吸附到駐留點後不再移動
            var standInfo = stand.Result;
            var snapped = TestWait.Until(() =>
            {
                var pos = _Shell.Target.position;
                return Vector2.Distance(new Vector2(pos.x, pos.z), standInfo.Position) < 0.1f;
            }, System.TimeSpan.FromSeconds(5));
            yield return snapped;
            TestWait.AssertDone(snapped, "殼應吸附到伺服器駐留點");

            var stopPos = _Shell.Target.position;
            var held = TestWait.HoldFrames(
                () => (_Shell.Target.position - stopPos).magnitude < 0.02f,
                System.TimeSpan.FromSeconds(1));
            yield return held;
            Assert.IsFalse(held.Result, "停走後殼不應再移動");
        }

        /// <summary>
        /// 走路中攻擊:Play(BattleAttack) 直接取代 BattleWalk(無中間 None),
        /// 攻擊結束(None)後自動回 BattleIdle,重新 Play(BattleWalk) 可再起走。
        /// </summary>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator WalkAttackResumeTest()
        {
            yield return _EnterWorld("WalkAttackTester");

            // 收集 ActionEvent 序列(replay AdventureIdle 也會進來,驗證時以 ticks 篩選)
            var actionLog = new System.Collections.Generic.List<ActionInfo>();
            var actionSub = TestWait.ActionEvents(_ActorGhost).Subscribe(actionLog.Add);

            // 進戰鬥(攻擊只在戰鬥系狀態的白名單內;先切戰鬥態再起走)
            var battleIdle = _WaitControllable(ActionType.BattleIdle, _ControllableGhost, ActionType.BattleIdle, Vector2.zero);
            yield return battleIdle;
            TestWait.AssertDone(battleIdle, "Play(BattleIdle) 後應供應 Current==BattleIdle 的新 soul");

            // 起走(戰鬥走路)
            var walkEvent = TestWait.FirstWithRetry(
                () => TestWait.ActionEvents(_ActorGhost).Where(a => a.Action == ActionType.BattleWalk),
                onAttempt: () => battleIdle.Result.Play(ActionType.BattleWalk, new Vector2(1f, 0f)).RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return walkEvent;
            TestWait.AssertDone(walkEvent, "Play(BattleWalk) 後 ActionEvent 應廣播 BattleWalk");
            var walkStartTicks = walkEvent.Result.StartTicks;

            // 走路中攻擊:取得走路 soul,Play(BattleAttack) 直接取代走路
            var walkSoul = TestWait.First(
                _PlayerGhost.Controllable.SupplyEvent()
                    .Where(c => c.Transition.Value.Current.Action == ActionType.BattleWalk),
                System.TimeSpan.FromSeconds(10));
            yield return walkSoul;
            TestWait.AssertDone(walkSoul, "走路中應供應 Current==BattleWalk 的控制 soul");

            var attackEvent = TestWait.FirstWithRetry(
                () => TestWait.ActionEvents(_ActorGhost).Where(a => a.Action == ActionType.BattleAttack),
                onAttempt: () => walkSoul.Result.Play(ActionType.BattleAttack, Vector2.zero).RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return attackEvent;
            TestWait.AssertDone(attackEvent, "走路中 Play(BattleAttack) 應被受理並廣播");
            var attackStartTicks = attackEvent.Result.StartTicks;

            // None 已不再廣播:進場後的整段事件流(走路→攻擊→接續)不得出現 None
            foreach (var info in actionLog)
            {
                if (info.StartTicks >= walkStartTicks)
                    Assert.AreNotEqual(ActionType.None, info.Action, "動作事件流全程不得廣播 None");
            }

            // 攻擊播完直接接下一狀態的 BattleIdle 廣播(無 None 空窗)
            var idleEvent = TestWait.First(
                TestWait.ActionEvents(_ActorGhost),
                a => a.Action == ActionType.BattleIdle && a.StartTicks > attackStartTicks,
                System.TimeSpan.FromSeconds(10));
            yield return idleEvent;
            TestWait.AssertDone(idleEvent, "攻擊播完應直接廣播下一狀態的 BattleIdle");

            // 攻擊播完自動轉移回 BattleIdle:新 soul 供應(SupplyEvent 有 replay,晚訂閱安全)
            var idleResupply = TestWait.First(
                _PlayerGhost.Controllable.SupplyEvent()
                    .Where(c => c.Transition.Value.Current.Action == ActionType.BattleIdle),
                System.TimeSpan.FromSeconds(10));
            yield return idleResupply;
            TestWait.AssertDone(idleResupply, "攻擊結束後應自動供應 Current==BattleIdle 的新 soul");

            // 攻擊結束後可重新起走:重新廣播 BattleWalk(新 StartTicks)
            var resumeWalk = TestWait.FirstWithRetry(
                () => TestWait.ActionEvents(_ActorGhost)
                    .Where(a => a.Action == ActionType.BattleWalk && a.StartTicks > attackStartTicks),
                onAttempt: () => idleResupply.Result.Play(ActionType.BattleWalk, new Vector2(0f, 1f)).RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return resumeWalk;
            TestWait.AssertDone(resumeWalk, "攻擊結束後 Play(BattleWalk) 應恢復並重新廣播");

            actionSub.Dispose();
        }

        IPlayer _PlayerGhost;
        IControllable _ControllableGhost;
        IActor _ActorGhost;
        PinionCore.Project2.Client.ActorShell _Shell;

        // 共用進場流程:Verify → 取得 IPlayer / IControllable / IActor → 等 ActorProvider 建出對應殼
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
                _Client.Queryer.QueryNotifier<IUserEntry>().SupplyEvent()
                    .SelectMany(entry => entry.Games.SupplyEvent())
                    .SelectMany(game => game.Player.SupplyEvent()),
                System.TimeSpan.FromSeconds(15));
            yield return playerSupply;
            TestWait.AssertDone(playerSupply, "Verify 通過後 client 應收到 IPlayer");
            _PlayerGhost = playerSupply.Result;
            System.Guid actorId = _PlayerGhost.ActorId;

            // 控制能力只供應給擁有者(進場即 AdventureIdle 狀態)
            var controllableSupply = TestWait.First(
                _PlayerGhost.Controllable.SupplyEvent(),
                System.TimeSpan.FromSeconds(15));
            yield return controllableSupply;
            TestWait.AssertDone(controllableSupply, "client 應收到自身的 IControllable ghost");
            _ControllableGhost = controllableSupply.Result;

            var actorSupply = TestWait.First(
                _PlayerGhost.Actors.SupplyEvent(), a => a.ActorId == actorId,
                System.TimeSpan.FromSeconds(15));
            yield return actorSupply;
            TestWait.AssertDone(actorSupply, "client 應收到自身的 IActor ghost");
            _ActorGhost = actorSupply.Result;

            var provider = _Scenes.FindComponent<PinionCore.Project2.Client.ActorProvider>("Client", "Handlers");
            Assert.NotNull(provider, "Client 場景應有 ActorProvider");
            var shellWait = TestWait.First(provider.SupplyEvent(), a => a.ActorId == actorId, System.TimeSpan.FromSeconds(15));
            yield return shellWait;
            TestWait.AssertDone(shellWait, "ActorProvider 應在 Client 場景實例化出對應 ActorId 的 Client.ActorShell");
            _Shell = shellWait.Result;
        }
    }
}
