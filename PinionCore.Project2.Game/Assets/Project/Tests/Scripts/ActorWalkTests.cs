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
    /// Move → IActor.ActionEvent 廣播 Walk(循環動作,不發 None)→ 殼跟分段 MoveInfo 位移
    /// 且旋轉不凍結(面向移動方向)→ Stop → ActionEvent None + 駐留;
    /// 走路中攻擊直接取代(Walk→Attack)、攻擊結束後 Move 恢復重新起走。
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

        /// <summary>
        /// 走路生命週期:Move → ActionEvent 廣播 Walk、殼跟分段 MoveInfo 位移、
        /// 旋轉不凍結(面向移動方向);Stop → None + 駐留 MoveInfo,殼停住。
        /// </summary>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator WalkLifecycleTest()
        {
            yield return _EnterWorld("WalkTester");

            var startPos = _Shell.Target.position;

            // Move → 等 ActionEvent 廣播 Walk(訂閱 replay 是 None,被 predicate 濾掉);
            // RPC 可能與 soul 綁定競態掉失,逾時重訂閱+重送(重送在走路中 = 重定向,無害)
            var walkEvent = TestWait.FirstWithRetry(
                () => TestWait.ActionEvents(_ActorGhost).Where(a => a.Action == ActionType.Walk),
                onAttempt: () => _MoveableGhost.Move(new Vector2(1f, 0f)).RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return walkEvent;
            TestWait.AssertDone(walkEvent, "Move 後 ActionEvent 應廣播 Walk");
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

            // Stop → None(StartTicks 晚於走路開始)+ 駐留 MoveInfo
            var stand = TestWait.First(
                TestWait.MoveEvents(_ActorGhost), i => i.Speed == 0f, System.TimeSpan.FromSeconds(10));
            var noneEvent = TestWait.First(
                TestWait.ActionEvents(_ActorGhost),
                a => a.Action == ActionType.None && a.StartTicks > walkStartTicks,
                System.TimeSpan.FromSeconds(10));
            _MoveableGhost.Stop();

            yield return noneEvent;
            TestWait.AssertDone(noneEvent, "Stop 後 ActionEvent 應廣播 None");
            yield return stand;
            TestWait.AssertDone(stand, "Stop 後應收到駐留 MoveInfo(Speed==0)");

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
            Assert.IsFalse(held.Result, "Stop 後殼不應再移動");
        }

        /// <summary>
        /// 走路中攻擊:Attack 直接取代 Walk(無中間 None),
        /// 攻擊結束(None)後 Move 恢復可用並重新廣播 Walk。
        /// </summary>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator WalkAttackResumeTest()
        {
            yield return _EnterWorld("WalkAttackTester");

            // 收集 ActionEvent 序列(replay None 也會進來,驗證時以 ticks 篩選)
            var actionLog = new System.Collections.Generic.List<ActionInfo>();
            var actionSub = TestWait.ActionEvents(_ActorGhost).Subscribe(actionLog.Add);

            // 進戰鬥(走路不佔用 IBattle;先切戰鬥態再起走)
            var adventureSupply = TestWait.First(
                _PlayerGhost.Adventure.SupplyEvent(),
                System.TimeSpan.FromSeconds(15));
            yield return adventureSupply;
            TestWait.AssertDone(adventureSupply, "進場後應供應 IAdventure");

            var battleSupply = TestWait.FirstWithRetry(
                () => _PlayerGhost.Battle.SupplyEvent(),
                onAttempt: () => adventureSupply.Result.ToBattle().RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return battleSupply;
            TestWait.AssertDone(battleSupply, "ToBattle 後應供應 IBattle");

            // 起走
            var walkEvent = TestWait.FirstWithRetry(
                () => TestWait.ActionEvents(_ActorGhost).Where(a => a.Action == ActionType.Walk),
                onAttempt: () => _MoveableGhost.Move(new Vector2(1f, 0f)).RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return walkEvent;
            TestWait.AssertDone(walkEvent, "Move 後 ActionEvent 應廣播 Walk");
            var walkStartTicks = walkEvent.Result.StartTicks;

            // 走路中攻擊:Attack 直接取代 Walk
            var battle = battleSupply.Result;
            var attackEvent = TestWait.FirstWithRetry(
                () => TestWait.ActionEvents(_ActorGhost).Where(a => a.Action == ActionType.Attack),
                onAttempt: () => battle.Attack(ActionType.Attack).RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return attackEvent;
            TestWait.AssertDone(attackEvent, "走路中 Attack 應被受理並廣播");
            var attackStartTicks = attackEvent.Result.StartTicks;

            // 取代不發中間 None:走路開始與攻擊開始之間不得有 None 事件
            foreach (var info in actionLog)
            {
                if (info.Action == ActionType.None)
                    Assert.IsFalse(info.StartTicks > walkStartTicks && info.StartTicks < attackStartTicks,
                        "攻擊取代走路不得發中間 None");
            }

            // 攻擊結束 → None
            var noneEvent = TestWait.First(
                TestWait.ActionEvents(_ActorGhost),
                a => a.Action == ActionType.None && a.StartTicks > attackStartTicks,
                System.TimeSpan.FromSeconds(10));
            yield return noneEvent;
            TestWait.AssertDone(noneEvent, "攻擊應以 ActionEvent None 結束");

            // CastStatus 期間 IMoveable 被收回、結束後重新供應:取新 ghost 再送 Move
            //(SupplyEvent 有 replay,晚訂閱安全)
            var moveableResupply = TestWait.First(
                _PlayerGhost.Moveable.SupplyEvent(),
                System.TimeSpan.FromSeconds(10));
            yield return moveableResupply;
            TestWait.AssertDone(moveableResupply, "攻擊結束後應重新供應 IMoveable");

            // 攻擊結束後 Move 恢復:重新廣播 Walk(新 StartTicks)
            var resumeWalk = TestWait.FirstWithRetry(
                () => TestWait.ActionEvents(_ActorGhost)
                    .Where(a => a.Action == ActionType.Walk && a.StartTicks > attackStartTicks),
                onAttempt: () => moveableResupply.Result.Move(new Vector2(0f, 1f)).RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return resumeWalk;
            TestWait.AssertDone(resumeWalk, "攻擊結束後 Move 應恢復並重新廣播 Walk");

            moveableResupply.Result.Stop();
            actionSub.Dispose();
        }

        IPlayer _PlayerGhost;
        IMoveable _MoveableGhost;
        IActor _ActorGhost;
        PinionCore.Project2.Client.Actor _Shell;

        // 共用進場流程:Verify → 取得 IPlayer / IMoveable / IActor → 等 ActorProvider 建出對應殼
        IEnumerator _EnterWorld(string playerName)
        {
            var verifiableSupply = TestWait.First(
                _Client.Queryer.QueryNotifier<IUserEntry>().SupplyEvent()
                    .SelectMany(entry => entry.Verifiables.SupplyEvent()),
                System.TimeSpan.FromSeconds(10));
            yield return verifiableSupply;
            TestWait.AssertDone(verifiableSupply, "連線後 client 應從 User 服務收到 IVerifiable");

            var verifyResult = TestWait.First(
                verifiableSupply.Result.Verify(playerName, CharactorType.Cube).RemoteValue(),
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

            var moveableSupply = TestWait.First(
                _PlayerGhost.Moveable.SupplyEvent(), m => m.ActorId == actorId,
                System.TimeSpan.FromSeconds(15));
            yield return moveableSupply;
            TestWait.AssertDone(moveableSupply, "client 應收到自身的 IMoveable ghost");
            _MoveableGhost = moveableSupply.Result;

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
            TestWait.AssertDone(shellWait, "ActorProvider 應在 Client 場景實例化出對應 ActorId 的 Client.Actor");
            _Shell = shellWait.Result;
        }
    }
}
