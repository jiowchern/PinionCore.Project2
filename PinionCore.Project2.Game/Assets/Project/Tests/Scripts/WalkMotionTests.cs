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
    /// 走路(Locomotion)動作的伺服器權威單元測試(WorldTestScript 模式:直接 new World)。
    /// 走路 = 循環播放的分段 root motion:Move 啟動(恰一發 ActionInfo)、段邊界 wrap 不發 None、
    /// Move 重定向不重發 ActionInfo(邊界 tick 不動)、Stop 結束、Cast 可直接取代(無中間 None)。
    /// 地形同 ActionMotionTests:Wall 世界盒 x∈[-2.5,2.5], z∈[-3.5,-2.5],半徑 0.3 接觸面 z≈-2.2。
    /// </summary>
    public class WalkMotionTests
    {
        const float Radius = 0.3f;
        const float MoveAcceptInterval = 0.1f;
        const float ContactZ = -2.2f;
        const float PenetrationLimitZ = ContactZ - 0.01f;

        // 走路循環:快段 0.5m/0.25s(2m/s)+ 慢段 0.2m/0.25s(0.8m/s),
        // 段速度刻意不同 → 每個段邊界(含 wrap)都觀察得到 MoveInfo
        const float FastDistance = 0.5f;
        const float SlowDistance = 0.2f;
        const float SegmentDuration = 0.25f;
        const float CycleDistance = FastDistance + SlowDistance;
        static readonly long SegmentTicks = (long)(SegmentDuration * (double)System.TimeSpan.TicksPerSecond);
        static readonly long CycleTicks = SegmentTicks * 2;

        // 攻擊(Cast):前衝 1m(0.25s)+ 原地收招(0.25s),同 ActionMotionTests
        const float DashDistance = 1f;
        const float DashDuration = 0.25f;
        const float RecoverDuration = 0.25f;
        static readonly long AttackTotalTicks = (long)((DashDuration + RecoverDuration) * (double)System.TimeSpan.TicksPerSecond);

        PinionCore.Project2.Worlds.World _world;
        WorldConfig _worldInfo;

        [SetUp]
        public void SetUp()
        {
            _worldInfo = ScriptableObject.CreateInstance<WorldConfig>();
            _worldInfo.Name = "WalkMotionTestWorld";
            _worldInfo.TerrainPrefab = new UnityEngine.AddressableAssets.AssetReferenceGameObject("84e3641b69ee6b2419379df04933bb0d");

            var walk = ScriptableObject.CreateInstance<ActionConfig>();
            walk.Action = ActionType.AdventureWalk;
            walk.Loop = walk.Redirectable = walk.Interruptible = true;
            walk.Duration = SegmentDuration * 2f;
            walk.Segments = new[]
            {
                new ActionConfig.MotionSegment { LocalOffset = new Vector2(0f, FastDistance), Duration = SegmentDuration },
                new ActionConfig.MotionSegment { LocalOffset = new Vector2(0f, SlowDistance), Duration = SegmentDuration },
            };

            var attack = ScriptableObject.CreateInstance<ActionConfig>();
            attack.Action = ActionType.BattleAttack;
            attack.Loop = false;
            attack.Duration = DashDuration + RecoverDuration;
            attack.Segments = new[]
            {
                new ActionConfig.MotionSegment { LocalOffset = new Vector2(0f, DashDistance), Duration = DashDuration },
                new ActionConfig.MotionSegment { LocalOffset = Vector2.zero, Duration = RecoverDuration },
            };

            var walker = ScriptableObject.CreateInstance<ActorConfig>();
            walker.Name = "Walker";
            walker.MoveAcceptInterval = MoveAcceptInterval;
            walker.Radius = Radius;
            walker.Actions = new[] { walk, attack };

            // 單段直線走路:wrap 的新段與外推等價 → 不 emit(冗餘抑制)
            var straightWalk = ScriptableObject.CreateInstance<ActionConfig>();
            straightWalk.Action = ActionType.AdventureWalk;
            straightWalk.Loop = straightWalk.Redirectable = straightWalk.Interruptible = true;
            straightWalk.Duration = SegmentDuration;
            straightWalk.Segments = new[]
            {
                new ActionConfig.MotionSegment { LocalOffset = new Vector2(0f, FastDistance), Duration = SegmentDuration },
            };

            var straightWalker = ScriptableObject.CreateInstance<ActorConfig>();
            straightWalker.Name = "StraightWalker";
            straightWalker.MoveAcceptInterval = MoveAcceptInterval;
            straightWalker.Radius = Radius;
            straightWalker.Actions = new[] { straightWalk };

            // 無走路動作:Move 一律拒收(位移權威只來自 root motion 排程)
            var legacy = ScriptableObject.CreateInstance<ActorConfig>();
            legacy.Name = "Legacy";
            legacy.MoveAcceptInterval = MoveAcceptInterval;
            legacy.Radius = Radius;
            legacy.Actions = new[] { attack };

            _world = new PinionCore.Project2.Worlds.World(System.Guid.NewGuid(), _worldInfo, new[] { walker, straightWalker, legacy });
        }

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();
            _world = null;
        }

        PinionCore.Project2.Worlds.Player _Enter(string modelName, out List<MoveInfo> moveEvents, out List<ActionInfo> actionEvents)
        {
            IWorld world = _world;
            var actorId = System.Guid.NewGuid();
            var entered = false;
            world.Enter(actorId, new ActorInfo { ModelName = modelName, DisplayName = "Tester" }).OnValue += (ok, error) => entered = ok;
            Assert.IsTrue(entered, "Enter 應成功");

            var player = _world.PlayerItems.First();

            var moves = new List<MoveInfo>();
            player.MoveEvent += info => moves.Add(info);
            moves.Clear();   // 丟掉訂閱 replay

            var actions = new List<ActionInfo>();
            player.ActionEvent += info => actions.Add(info);
            Assert.AreEqual(ActionType.None, actions[0].Action, "初始動作狀態應為 None");
            actions.Clear();

            moveEvents = moves;
            actionEvents = actions;
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

        static bool _Accepted(PinionCore.Remote.Value<bool> value)
        {
            var accepted = false;
            value.OnValue += (r, error) => accepted = r;
            return accepted;
        }

        [UnityTest]
        public IEnumerator WalkLoopTest()
        {
            var player = _Enter("Walker", out var moveEvents, out var actionEvents);
            var start = player.CurrentMoveInfo.Position;

            Assert.IsTrue(_Accepted(player.Move(new Vector2(0f, 1f))), "啟動走路應被接受");
            Assert.AreEqual(1, actionEvents.Count, "啟動走路應恰發一次 ActionInfo");
            Assert.AreEqual(ActionType.AdventureWalk, actionEvents[0].Action);
            var walkStart = actionEvents[0].StartTicks;

            // 泵超過兩個循環:不得出現 None、不得重發 ActionInfo
            yield return _PumpUntil(
                () => _world.ElapsedTicks >= walkStart + CycleTicks * 2 + SegmentTicks / 2,
                timeoutSeconds: 5f);
            Assert.AreEqual(1, actionEvents.Count, "循環 wrap 不得重發 ActionInfo,也不得發 None");

            // 段邊界 tick 精確:第 k 發 MoveInfo 的 StartTicks = walkStart + k * SegmentTicks
            //(wrap 基準 = 上一輪最後邊界,零漂移;快慢段速度不同,每個邊界都必 emit)
            Assert.GreaterOrEqual(moveEvents.Count, 5, "兩個循環應至少有 5 發段 MoveInfo");
            var fastSpeed = FastDistance / SegmentDuration;
            var slowSpeed = SlowDistance / SegmentDuration;
            for (var k = 0; k < 5; k++)
            {
                Assert.AreEqual(walkStart + SegmentTicks * k, moveEvents[k].StartTicks, $"第 {k} 段起點 tick 應精確落在排程邊界");
                Assert.AreEqual(k % 2 == 0 ? fastSpeed : slowSpeed, moveEvents[k].Speed, 0.001f, $"第 {k} 段速度應為段名目速度");
            }

            // 位移:每循環淨前進 CycleDistance;第 2 輪起點 = start + 1 * CycleDistance
            var secondCycleFirst = moveEvents[2];
            Assert.AreEqual(start.x, secondCycleFirst.Position.x, 0.01f);
            Assert.AreEqual(start.y + CycleDistance, secondCycleFirst.Position.y, 0.01f, "wrap 起點應為上一輪累計位移");
        }

        [UnityTest]
        public IEnumerator WalkStraightLineQuietTest()
        {
            var player = _Enter("StraightWalker", out var moveEvents, out var actionEvents);

            Assert.IsTrue(_Accepted(player.Move(new Vector2(0f, 1f))), "啟動走路應被接受");
            var walkStart = actionEvents[0].StartTicks;
            Assert.AreEqual(1, moveEvents.Count, "啟動時應發出第一段 MoveInfo");

            // 單段直線循環:wrap 的新段與現行外推同向同速位置吻合 → 不 emit
            yield return _PumpUntil(
                () => _world.ElapsedTicks >= walkStart + SegmentTicks * 3 + SegmentTicks / 2,
                timeoutSeconds: 5f);
            Assert.AreEqual(1, actionEvents.Count, "循環中不得有新 ActionInfo");
            Assert.AreEqual(1, moveEvents.Count, "直線循環 wrap 不得重發冗餘 MoveInfo");

            // 內部權威狀態照常前進(抑制的是網路 emit,不是模擬)
            var pos = _SamplePosition(player.CurrentMoveInfo, _world.ElapsedTicks);
            Assert.Greater(pos.y, FastDistance * 3f - 0.05f, "位置應持續前進");
        }

        [UnityTest]
        public IEnumerator WalkRedirectTest()
        {
            var player = _Enter("Walker", out var moveEvents, out var actionEvents);

            Assert.IsTrue(_Accepted(player.Move(new Vector2(0f, 1f))));
            var walkStart = actionEvents[0].StartTicks;

            // 泵過節流窗(0.1s < SegmentDuration,一般會落在第一段;編輯器卡頓落到後面的段也成立)
            yield return _PumpUntil(
                () => _world.ElapsedTicks >= walkStart + (long)(MoveAcceptInterval * 1.2 * System.TimeSpan.TicksPerSecond),
                timeoutSeconds: 5f);

            moveEvents.Clear();
            Assert.IsTrue(_Accepted(player.Move(new Vector2(1f, 0f))), "走路中重定向應被接受");
            Assert.AreEqual(1, actionEvents.Count, "重定向不得重發 ActionInfo");
            Assert.AreEqual(1, moveEvents.Count, "重定向應立即重發現行段 MoveInfo");
            Assert.AreEqual(1f, moveEvents[0].Facing.x, 0.001f, "新段朝向 = 新指令方向");

            // 段邊界是等差數列(段等長 + wrap 零漂移):由重定向時刻推算所屬段與下一個邊界
            var redirectTick = moveEvents[0].StartTicks;
            var segmentIndex = (redirectTick - walkStart) / SegmentTicks;
            var expectedSpeed = segmentIndex % 2 == 0 ? FastDistance / SegmentDuration : SlowDistance / SegmentDuration;
            Assert.AreEqual(expectedSpeed, moveEvents[0].Speed, 0.001f, "重定向不改段名目速度");

            // 邊界 tick 不動(相位保持):下一發段 MoveInfo 仍精確落在原排程邊界
            yield return _PumpUntil(() => moveEvents.Count >= 2, timeoutSeconds: 5f);
            Assert.AreEqual(walkStart + SegmentTicks * (segmentIndex + 1), moveEvents[1].StartTicks, "重定向不得平移段邊界");
            Assert.AreEqual(1f, moveEvents[1].Facing.x, 0.001f, "後續段沿新基底");
        }

        [UnityTest]
        public IEnumerator WalkThrottleTest()
        {
            var player = _Enter("Walker", out _, out var actionEvents);

            Assert.IsTrue(_Accepted(player.Move(new Vector2(0f, 1f))));
            Assert.IsFalse(_Accepted(player.Move(new Vector2(1f, 0f))), "節流窗內的重定向應被拒收");
            Assert.AreEqual(1, actionEvents.Count);

            // Stop 不受節流限制
            Assert.IsTrue(_Accepted(player.Stop()), "Stop 不受 MoveAcceptInterval 限制");
            yield break;
        }

        [UnityTest]
        public IEnumerator WalkStopTest()
        {
            var player = _Enter("Walker", out var moveEvents, out var actionEvents);

            Assert.IsTrue(_Accepted(player.Move(new Vector2(0f, 1f))));
            var walkStart = actionEvents[0].StartTicks;

            // 泵到第一段中途再停
            yield return _PumpUntil(
                () => _world.ElapsedTicks >= walkStart + SegmentTicks / 2,
                timeoutSeconds: 5f);

            moveEvents.Clear();
            Assert.IsTrue(_Accepted(player.Stop()), "走路中 Stop 應被接受");
            Assert.AreEqual(2, actionEvents.Count, "Stop 應發出 None");
            Assert.AreEqual(ActionType.None, actionEvents[1].Action);
            Assert.AreEqual(1, moveEvents.Count, "Stop 應發出終停 MoveInfo");
            Assert.AreEqual(0f, moveEvents[0].Speed);
            Assert.Greater(moveEvents[0].Facing.y, 0.999f, "終停朝向 = 移動指令方向");

            // 停止後(過節流窗)可再啟動走路
            yield return _PumpUntil(
                () => _world.ElapsedTicks >= walkStart + (long)(MoveAcceptInterval * 1.5 * System.TimeSpan.TicksPerSecond),
                timeoutSeconds: 5f);
            Assert.IsTrue(_Accepted(player.Move(new Vector2(0f, 1f))), "停止後 Move 應恢復可用");
            Assert.AreEqual(3, actionEvents.Count, "重新啟動走路應發新 ActionInfo");
            Assert.AreEqual(ActionType.AdventureWalk, actionEvents[2].Action);
        }

        [UnityTest]
        public IEnumerator CastInterruptsWalkTest()
        {
            var player = _Enter("Walker", out _, out var actionEvents);

            Assert.IsTrue(_Accepted(player.Move(new Vector2(1f, 0f))), "先朝 +X 走");
            var walkStart = actionEvents[0].StartTicks;

            yield return _PumpUntil(
                () => _world.ElapsedTicks >= walkStart + SegmentTicks / 2,
                timeoutSeconds: 5f);

            // Cast 直接取代走路:無中間 None
            var interruptPos = _SamplePosition(player.CurrentMoveInfo, _world.ElapsedTicks);
            Assert.IsTrue(player.StartAction(ActionType.BattleAttack, force: false), "走路中出招應被接受");
            Assert.AreEqual(2, actionEvents.Count, "取代不得發中間 None");
            Assert.AreEqual(ActionType.BattleAttack, actionEvents[1].Action);
            var attackStart = actionEvents[1].StartTicks;

            // 攻擊進行中:Move / Stop 拒收(Cast 閘)
            Assert.IsFalse(_Accepted(player.Move(new Vector2(0f, 1f))), "Cast 中 Move 應被拒收");
            Assert.IsFalse(_Accepted(player.Stop()), "Cast 中 Stop 應被拒收");

            yield return _PumpUntil(
                () => actionEvents.Any(a => a.Action == ActionType.None),
                timeoutSeconds: 5f);

            // 攻擊基底沿用走路方向(+X):前衝終點 = 打斷點 + (DashDistance, 0)
            var none = actionEvents.First(a => a.Action == ActionType.None);
            Assert.AreEqual(attackStart + AttackTotalTicks, none.StartTicks, "攻擊結束時刻應以取代時刻起算");
            var finalPos = player.CurrentMoveInfo.Position;
            Assert.AreEqual(interruptPos.x + DashDistance, finalPos.x, 0.01f, "攻擊應沿走路方向前衝");
            Assert.AreEqual(interruptPos.y, finalPos.y, 0.01f);

            // 結束後 Move 恢復(過節流窗後)
            Assert.IsTrue(_Accepted(player.Move(new Vector2(0f, 1f))), "攻擊結束後 Move 應恢復可用");
        }

        [UnityTest]
        public IEnumerator WalkIntoWallTest()
        {
            var player = _Enter("Walker", out _, out var actionEvents);

            // 朝牆(-Z)走:接觸面 z≈-2.2,平均速度 1.4m/s,兩秒多可達;
            // 逐幀驗證不穿牆,且撞停後循環不終止(無 None)
            Assert.IsTrue(_Accepted(player.Move(new Vector2(0f, -1f))));
            var walkStart = actionEvents[0].StartTicks;

            yield return _PumpUntil(
                () => _world.ElapsedTicks >= walkStart + CycleTicks * 6,
                timeoutSeconds: 10f,
                perFrame: () =>
                {
                    var pos = _SamplePosition(player.CurrentMoveInfo, _world.ElapsedTicks);
                    Assert.GreaterOrEqual(pos.y, PenetrationLimitZ, "走路位移不得穿入牆面");
                });

            Assert.AreEqual(1, actionEvents.Count, "撞牆不得終止走路循環(無 None)");
            var finalPos = _SamplePosition(player.CurrentMoveInfo, _world.ElapsedTicks);
            Assert.AreEqual(ContactZ, finalPos.y, 0.1f, "應停在牆邊接觸面附近");

            // 撞牆卡住仍可 Stop
            Assert.IsTrue(_Accepted(player.Stop()), "撞牆卡住仍應能停止走路");
            Assert.AreEqual(ActionType.None, actionEvents.Last().Action);
        }

        [UnityTest]
        public IEnumerator WalkReplayTest()
        {
            var player = _Enter("Walker", out _, out var actionEvents);

            Assert.IsTrue(_Accepted(player.Move(new Vector2(0f, 1f))));
            var walkStart = actionEvents[0].StartTicks;

            // 泵過一個 wrap 之後再訂閱:replay 仍應為 Walk + 原始 StartTicks
            //(晚加入者以 (worldTime - StartTicks) mod clip 長度算動畫偏移)
            yield return _PumpUntil(
                () => _world.ElapsedTicks >= walkStart + CycleTicks + SegmentTicks / 2,
                timeoutSeconds: 5f);

            var lateReplay = new List<ActionInfo>();
            System.Action<ActionInfo> handler = info => lateReplay.Add(info);
            player.ActionEvent += handler;
            Assert.AreEqual(1, lateReplay.Count, "訂閱應立即 replay");
            Assert.AreEqual(ActionType.AdventureWalk, lateReplay[0].Action, "走路循環中 replay 應為 Walk");
            Assert.AreEqual(walkStart, lateReplay[0].StartTicks, "wrap 不得改 replay 的原始 StartTicks");
            player.ActionEvent -= handler;

            // MoveInfo replay = 現行段(速度 > 0),晚加入者取樣即得正確位置
            var moveReplay = new List<MoveInfo>();
            System.Action<MoveInfo> moveHandler = info => moveReplay.Add(info);
            player.MoveEvent += moveHandler;
            Assert.AreEqual(1, moveReplay.Count);
            Assert.Greater(moveReplay[0].Speed, 0f, "走路中 MoveInfo replay 應為進行中的段");
            player.MoveEvent -= moveHandler;
        }

        [UnityTest]
        public IEnumerator MoveRejectedWithoutLocomotionTest()
        {
            var player = _Enter("Legacy", out var moveEvents, out var actionEvents);
            var spawn = player.CurrentMoveInfo.Position;

            // 無走路動作的角色:Move 一律拒收,不發任何事件
            Assert.IsFalse(_Accepted(player.Move(new Vector2(0f, 1f))), "無 Locomotion config 的 Move 應被拒收");
            Assert.AreEqual(0, moveEvents.Count, "拒收的 Move 不得發 MoveInfo");
            Assert.AreEqual(0, actionEvents.Count, "拒收的 Move 不得發 ActionInfo");

            // 泵一段時間:角色維持靜止、無任何事件
            var start = _world.ElapsedTicks;
            yield return _PumpUntil(
                () => _world.ElapsedTicks >= start + CycleTicks * 2,
                timeoutSeconds: 5f);
            Assert.AreEqual(0, moveEvents.Count);
            Assert.AreEqual(0, actionEvents.Count);
            Assert.AreEqual(0f, player.CurrentMoveInfo.Speed);
            Assert.AreEqual(spawn, player.CurrentMoveInfo.Position, "拒收後位置不得改變");

            // 靜止中 Stop 也無事可停
            Assert.IsFalse(_Accepted(player.Stop()), "靜止中 Stop 應回 false");
        }
    }
}

