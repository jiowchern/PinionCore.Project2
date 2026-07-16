using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using PinionCore.Project2.Shared;
using UnityEngine;
using UnityEngine.TestTools;

namespace PinionCore.Project2.Tests
{
    /// <summary>
    /// 自帶位移動作(root motion 烘焙段)的伺服器權威單元測試(WorldTestScript 模式:直接 new World)。
    /// 動作 = 分段等速直線,依絕對 tick 排程:每段一發 MoveInfo、結束發終停 MoveInfo 與內部 EndEvent
    /// (不再廣播 ActionInfo.None;None 只剩訂閱 replay 的哨兵)。
    /// 地形同 WorldCollisionTests:Wall 世界盒 x∈[-2.5,2.5], y∈[0,1], z∈[-3.5,-2.5],半徑 0.3 接觸面 z≈-2.2。
    /// 時鐘是真實 Stopwatch,位置比對留寬鬆容差;tick 排程值是決定性 long,可精確比對。
    /// </summary>
    public class ActionMotionTests
    {
        const float Radius = 0.3f;
        // 走位用的走路段速度;單段長時直走,測試觀察窗內不會 wrap
        const float MoveSpeed = 2f;
        const float WalkSegmentDuration = 10f;
        const float ContactZ = -2.2f;
        const float PenetrationLimitZ = ContactZ - 0.01f;

        // 測試動作:前衝 1m(0.25s)+ 原地收招(0.25s)
        const float DashDistance = 1f;
        const float DashDuration = 0.25f;
        const float RecoverDuration = 0.25f;
        static readonly long DashTicks = (long)(DashDuration * (double)System.TimeSpan.TicksPerSecond);
        static readonly long TotalTicks = (long)((DashDuration + RecoverDuration) * (double)System.TimeSpan.TicksPerSecond);

        PinionCore.Project2.Worlds.World _world;
        WorldConfig _worldInfo;

        [SetUp]
        public void SetUp()
        {
            _worldInfo = ScriptableObject.CreateInstance<WorldConfig>();
            _worldInfo.Name = "ActionMotionTestWorld";
            _worldInfo.TerrainPrefab = new UnityEngine.AddressableAssets.AssetReferenceGameObject("84e3641b69ee6b2419379df04933bb0d");

            var attack = ScriptableObject.CreateInstance<ActionConfig>();
            attack.Action = ActionType.BattleAttack;
            attack.Loop = false;
            attack.Duration = DashDuration + RecoverDuration;
            attack.Segments = new[]
            {
                new ActionConfig.MotionSegment { LocalOffset = new Vector2(0f, DashDistance), Duration = DashDuration },
                new ActionConfig.MotionSegment { LocalOffset = Vector2.zero, Duration = RecoverDuration },
            };

            // 走位移動:循環、可重定向、可被出招(非 Loop)打斷
            var walk = ScriptableObject.CreateInstance<ActionConfig>();
            walk.Action = ActionType.AdventureWalk;
            walk.Loop = walk.Redirectable = walk.Interruptible = true;
            walk.Duration = WalkSegmentDuration;
            walk.Segments = new[]
            {
                new ActionConfig.MotionSegment { LocalOffset = new Vector2(0f, MoveSpeed * WalkSegmentDuration), Duration = WalkSegmentDuration },
            };

            var actorConfig = ScriptableObject.CreateInstance<ActorConfig>();
            actorConfig.Name = "TestActor";
            actorConfig.MoveAcceptInterval = 0.1f;
            actorConfig.Radius = Radius;
            actorConfig.Actions = new[] { attack, walk };

            _world = new PinionCore.Project2.Worlds.World(System.Guid.NewGuid(), _worldInfo, new[] { actorConfig });
        }

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();
            _world = null;
        }

        PinionCore.Project2.Worlds.Player _Enter(out List<MoveInfo> moveEvents, out List<ActionInfo> actionEvents, out List<(ActionType Action, long Ticks)> endEvents)
        {
            IWorld world = _world;
            var actorId = System.Guid.NewGuid();
            var entered = false;
            world.Enter(actorId, new ActorInfo { ModelName = "TestActor", DisplayName = "Tester" }).OnValue += (ok, error) => entered = ok;
            Assert.IsTrue(entered, "Enter 應成功");

            var player = _world.PlayerItems.First();

            var moves = new List<MoveInfo>();
            player.MoveEvent += info => moves.Add(info);
            moves.Clear();   // 丟掉訂閱 replay

            var actions = new List<ActionInfo>();
            player.ActionEvent += info => actions.Add(info);
            Assert.AreEqual(1, actions.Count, "ActionEvent 訂閱應 replay 一次");
            Assert.AreEqual(ActionType.None, actions[0].Action, "初始動作狀態應為 None(哨兵)");
            actions.Clear();

            var ends = new List<(ActionType, long)>();
            player.EndEvent += (type, tick) => ends.Add((type, tick));

            moveEvents = moves;
            actionEvents = actions;
            endEvents = ends;
            return player;
        }

        IEnumerator _PumpUntil(System.Func<bool> done, float timeoutSeconds, System.Action perFrame = null)
        {
            var deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!done() && Time.realtimeSinceStartup < deadline)
            {
                _world.Update();
                perFrame?.Invoke();
                yield return null;
            }
        }

        static Vector2 _SamplePosition(MoveInfo info, long nowTicks)
        {
            var elapsed = (nowTicks - info.StartTicks) / (double)System.TimeSpan.TicksPerSecond;
            MoveSampler.Sample(info, System.Math.Max(0.0, elapsed), out var position, out _);
            return position;
        }

        [UnityTest]
        public IEnumerator TwoSegmentDisplacementTest()
        {
            var player = _Enter(out var moveEvents, out var actionEvents, out var endEvents);

            var start = player.CurrentMoveInfo.Position;

            // 玩家觸發路徑 = StartAction(force: false);RPC 端到端由 ActorAttackTests 的 IControllable.Play 覆蓋
            var accepted = player.StartAction(ActionType.BattleAttack, force: false);
            Assert.IsTrue(accepted, "無動作進行中的出招應被接受");
            Assert.AreEqual(1, actionEvents.Count, "接受後應立即發出 ActionInfo");
            Assert.AreEqual(ActionType.BattleAttack, actionEvents[0].Action);

            yield return _PumpUntil(() => endEvents.Count > 0, timeoutSeconds: 5f);

            // 動作事件:只有 Attack 一發(結束不廣播 None);EndEvent 時刻 = 開始 + 總時長(絕對 tick 排程,精確)
            Assert.AreEqual(1, actionEvents.Count, "動作結束不應再廣播 ActionInfo");
            var attackStart = actionEvents[0].StartTicks;
            Assert.AreEqual(1, endEvents.Count, "應恰有一發 EndEvent");
            Assert.AreEqual(ActionType.BattleAttack, endEvents[0].Action, "EndEvent 應帶結束的動作型別");
            Assert.AreEqual(attackStart + TotalTicks, endEvents[0].Ticks, "結束時刻應為開始 + 總時長");

            // 移動事件:前衝段、收招駐留段、終停,共三發;邊界時刻精確落在排程 tick
            Assert.AreEqual(3, moveEvents.Count, "應恰有三個移動事件:前衝段、駐留段、終停");
            var dash = moveEvents[0];
            Assert.AreEqual(attackStart, dash.StartTicks, "前衝段起點 = 動作開始");
            Assert.AreEqual(DashDistance / DashDuration, dash.Speed, 0.001f, "段速度 = 位移 / 時長");
            var recover = moveEvents[1];
            Assert.AreEqual(attackStart + DashTicks, recover.StartTicks, "駐留段起點 = 前衝段邊界");
            Assert.AreEqual(0f, recover.Speed, "零位移段應為駐留");
            var final = moveEvents[2];
            Assert.AreEqual(attackStart + TotalTicks, final.StartTicks, "終停起點 = 動作結束");
            Assert.AreEqual(0f, final.Speed);

            // 位移:出生面向 +Z,前衝 1m → 終點 = 起點 + (0, 1)
            Assert.AreEqual(start.x, final.Position.x, 0.01f, "終點 X 不應偏移");
            Assert.AreEqual(start.y + DashDistance, final.Position.y, 0.01f, "終點應前移 DashDistance");
            Assert.AreEqual(0f, final.Facing.x, 0.001f, "終停應恢復動作起始朝向");
            Assert.Greater(final.Facing.y, 0.999f, "終停應恢復動作起始朝向(+Z)");
        }

        [UnityTest]
        public IEnumerator MoveRejectedDuringActionTest()
        {
            var player = _Enter(out _, out var actionEvents, out var endEvents);

            player.StartAction(ActionType.BattleAttack, force: false);
            Assert.AreEqual(1, actionEvents.Count, "前置條件:動作已開始");

            // 動作進行中:Move / Stop / 重入出招一律拒收
            var moveAccepted = player.Move(new Vector2(1f, 0f));
            Assert.IsFalse(moveAccepted, "動作進行中 Move 應被拒收");

            var stopAccepted = true;
            player.Stop().OnValue += (r, error) => stopAccepted = r;
            Assert.IsFalse(stopAccepted, "動作進行中 Stop 應被拒收");

            var replayAccepted = player.StartAction(ActionType.BattleAttack, force: false);
            Assert.IsFalse(replayAccepted, "動作進行中不可重入");

            yield return _PumpUntil(() => endEvents.Count > 0, timeoutSeconds: 5f);
            Assert.IsTrue(endEvents.Any(e => e.Action == ActionType.BattleAttack), "動作應準時結束(EndEvent)");

            // 結束後移動恢復可用
            var acceptedAfter = player.Move(new Vector2(1f, 0f));
            Assert.IsTrue(acceptedAfter, "動作結束後 Move 應恢復可用");
        }

        [UnityTest]
        public IEnumerator LungeIntoWallTest()
        {
            var player = _Enter(out var moveEvents, out var actionEvents, out var endEvents);

            // 先朝牆(-Z)走到 z ≤ -1.4 讓朝向面牆,再出招:
            // 前衝 1m 的目標 z ≤ -2.4 超出接觸面(-2.2),必定撞牆
            player.Move(new Vector2(0f, -1f));
            yield return _PumpUntil(
                () => _SamplePosition(player.CurrentMoveInfo, _world.ElapsedTicks).y <= -1.4f,
                timeoutSeconds: 5f);

            moveEvents.Clear();
            actionEvents.Clear();

            var accepted = player.StartAction(ActionType.BattleAttack, force: false);
            Assert.IsTrue(accepted, "面牆出招應被接受(撞牆是伺服器的事,不是拒收)");
            var attackStart = actionEvents[0].StartTicks;

            // 前衝 1m 會超出接觸面(出招點 z ≤ -1.4,接觸面 -2.2):逐幀驗證不穿牆
            yield return _PumpUntil(
                () => endEvents.Count > 0,
                timeoutSeconds: 5f,
                perFrame: () =>
                {
                    var pos = _SamplePosition(player.CurrentMoveInfo, _world.ElapsedTicks);
                    Assert.GreaterOrEqual(pos.y, PenetrationLimitZ, "動作位移不得穿入牆面");
                });

            // 撞牆不平移排程:EndEvent 仍準時(開始 + 總時長)
            Assert.AreEqual(1, endEvents.Count, "應恰有一發 EndEvent");
            Assert.AreEqual(attackStart + TotalTicks, endEvents[0].Ticks, "撞牆不應延後動作結束時刻");

            // 最終停在接觸面附近,且再也不動
            var finalPos = player.CurrentMoveInfo.Position;
            Assert.AreEqual(0f, finalPos.x, 0.05f, "正面撞牆不應側移");
            Assert.AreEqual(ContactZ, finalPos.y, 0.1f, "應停在牆邊接觸面附近");
            Assert.AreEqual(0f, player.CurrentMoveInfo.Speed, "動作結束應為停狀態");
        }

        [UnityTest]
        public IEnumerator ReplaySemanticsTest()
        {
            var player = _Enter(out _, out var actionEvents, out var endEvents);

            player.StartAction(ActionType.BattleAttack, force: false);
            var attackStart = actionEvents[0].StartTicks;

            // 動作進行中新訂閱:replay 應為 Attack + 原 StartTicks(晚加入者以此算動畫偏移)
            var midReplay = new List<ActionInfo>();
            System.Action<ActionInfo> midHandler = info => midReplay.Add(info);
            player.ActionEvent += midHandler;
            Assert.AreEqual(1, midReplay.Count, "訂閱應立即 replay");
            Assert.AreEqual(ActionType.BattleAttack, midReplay[0].Action, "動作進行中 replay 應為 Attack");
            Assert.AreEqual(attackStart, midReplay[0].StartTicks, "replay 應帶原始 StartTicks");
            player.ActionEvent -= midHandler;

            yield return _PumpUntil(() => endEvents.Count > 0, timeoutSeconds: 5f);

            // 結束後新訂閱:replay 應為 None 哨兵(不得憑空重播過期的攻擊)
            var lateReplay = new List<ActionInfo>();
            System.Action<ActionInfo> lateHandler = info => lateReplay.Add(info);
            player.ActionEvent += lateHandler;
            Assert.AreEqual(1, lateReplay.Count);
            Assert.AreEqual(ActionType.None, lateReplay[0].Action, "動作結束後 replay 應為 None");
            player.ActionEvent -= lateHandler;
        }

        [UnityTest]
        public IEnumerator ForceOverrideTest()
        {
            var player = _Enter(out var moveEvents, out var actionEvents, out var endEvents);

            player.StartAction(ActionType.BattleAttack, force: false);
            var firstStart = actionEvents[0].StartTicks;

            // 泵到第一段中途(尚未結束)
            yield return _PumpUntil(
                () => _world.ElapsedTicks >= firstStart + DashTicks / 2,
                timeoutSeconds: 5f);
            Assert.AreEqual(0, endEvents.Count, "前置條件:動作仍在進行");

            // 伺服器主動覆蓋(未來僵直/死亡路徑):作廢舊排程、發新 ActionInfo 取代 replay 值
            moveEvents.Clear();
            var overridden = player.StartAction(ActionType.BattleAttack, force: true);
            Assert.IsTrue(overridden, "force 覆蓋應被接受");
            Assert.AreEqual(2, actionEvents.Count, "覆蓋應發出新的 ActionInfo");
            var secondStart = actionEvents[1].StartTicks;
            Assert.AreEqual(ActionType.BattleAttack, actionEvents[1].Action);
            Assert.Greater(secondStart, firstStart, "覆蓋的 StartTicks 應晚於原動作");

            // 覆蓋當下重新起段:新段起點位置 = 覆蓋時刻的取樣位置
            Assert.AreEqual(1, moveEvents.Count, "覆蓋應立即發出新段的 MoveInfo");
            var restartPos = moveEvents[0].Position;

            yield return _PumpUntil(() => endEvents.Count > 0, timeoutSeconds: 5f);

            // 舊排程作廢:EndEvent 時刻以覆蓋時刻起算,不是原動作的結束時刻;
            // 全程不得廣播 None(取代與結束都不發)
            Assert.AreEqual(1, endEvents.Count, "覆蓋只該有一發 EndEvent(舊排程已作廢)");
            Assert.AreEqual(secondStart + TotalTicks, endEvents[0].Ticks, "結束時刻應以覆蓋時刻起算");
            Assert.IsFalse(actionEvents.Any(a => a.Action == ActionType.None), "全程不應廣播 None");

            // 覆蓋沿用原動作的視覺朝向基底(+Z):終點 = 覆蓋點 + (0, DashDistance)
            var finalPos = player.CurrentMoveInfo.Position;
            Assert.AreEqual(restartPos.x, finalPos.x, 0.01f);
            Assert.AreEqual(restartPos.y + DashDistance, finalPos.y, 0.01f, "覆蓋後應完整走完新動作的位移");
        }
    }
}
