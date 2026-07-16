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
    /// 伺服器權威碰撞的直接單元測試(WorldTestScript 模式:直接 new World,不載場景)。
    /// 地形用 Terrain.prefab:地板 Plane + 子物件 Wall,Wall 世界盒 x∈[-2.5,2.5], y∈[0,1], z∈[-3.5,-2.5]。
    /// 半徑 0.3 的角色由原點朝 -Z 走,接觸時球心 z ≈ -2.2(牆面 -2.5 + 半徑 0.3),
    /// 接觸點另沿入射方向回退 Skin(0.02)。
    /// 時鐘是真實 Stopwatch,位置比對一律留寬鬆容差。
    /// </summary>
    public class WorldCollisionTests
    {
        const float Radius = 0.3f;
        // 走路段的名目速度(= 段位移 / 段時長):撞牆滑行縮放以此為基準
        const float MoveSpeed = 2f;
        // 單段長時直走:段時長遠大於測試觀察窗,觀察期間不會有 wrap 再發 MoveInfo
        const float WalkSegmentDuration = 10f;
        // 牆面接觸平面:z = -2.5 + Radius;斷言用的不可穿越界線再留一點浮點餘裕
        const float ContactZ = -2.2f;
        const float PenetrationLimitZ = ContactZ - 0.01f;

        PinionCore.Project2.Worlds.World _world;
        WorldConfig _worldInfo;

        [SetUp]
        public void SetUp()
        {
            _worldInfo = ScriptableObject.CreateInstance<WorldConfig>();
            _worldInfo.Name = "CollisionTestWorld";
            _worldInfo.TerrainPrefab = new UnityEngine.AddressableAssets.AssetReferenceGameObject("84e3641b69ee6b2419379df04933bb0d");

            // 移動一律走循環走路動作(root motion 排程):單段直線提供等速直線移動
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
            actorConfig.Actions = new[] { walk };

            _world = new PinionCore.Project2.Worlds.World(System.Guid.NewGuid(), _worldInfo, new[] { actorConfig });
        }

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();
            _world = null;
        }

        // 進入世界並回傳伺服器側 Player(權威狀態)與收集 MoveEvent 的清單。
        // 注意:訂閱當下會 replay 一次目前狀態,events 只收訂閱之後的變更。
        PinionCore.Project2.Worlds.Player _Enter(out List<MoveInfo> events)
        {
            IWorld world = _world;
            var actorId = System.Guid.NewGuid();
            var entered = false;
            world.Enter(actorId, new ActorInfo { ModelName = "TestActor", DisplayName = "Tester" }).OnValue += (ok, error) => entered = ok;
            Assert.IsTrue(entered, "Enter 應成功");

            var player = _world.PlayerItems.First();
            var received = new List<MoveInfo>();
            player.MoveEvent += info => received.Add(info);
            received.Clear();   // 丟掉訂閱 replay 的初始狀態
            events = received;
            return player;
        }

        // 泵世界直到條件成立或逾時;每幀回呼讓測試檢查不變量。
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
        public IEnumerator HeadOnStopTest()
        {
            var player = _Enter(out var events);

            var accepted = player.Move(new Vector2(0f, -1f));
            Assert.IsTrue(accepted, "朝牆的 Move 應被接受(撞牆是伺服器的事,不是拒收)");

            // 直到停下為止,每幀檢查不可穿牆不變量
            yield return _PumpUntil(
                () => player.CurrentMoveInfo.Speed == 0f,
                timeoutSeconds: 5f,
                perFrame: () =>
                {
                    var pos = _SamplePosition(player.CurrentMoveInfo, _world.ElapsedTicks);
                    Assert.GreaterOrEqual(pos.y, PenetrationLimitZ, "權威位置不得穿入牆面");
                });

            Assert.AreEqual(0f, player.CurrentMoveInfo.Speed, "正面撞牆最終應停止");
            var final = player.CurrentMoveInfo.Position;
            Assert.AreEqual(0f, final.x, 0.05f, "正面撞牆不應產生側移");
            Assert.AreEqual(ContactZ, final.y, 0.1f, "應停在接觸面附近");
            Assert.AreEqual(2, events.Count, "應恰有兩個事件:接受 Move、撞牆停止");
        }

        [UnityTest]
        public IEnumerator SlideAlongWallTest()
        {
            var player = _Enter(out var events);

            // 45 度斜向入射:撞牆後應沿 +X 滑行,速度縮為段速度/√2
            player.Move(new Vector2(1f, -1f).normalized);

            yield return _PumpUntil(
                () => events.Count >= 2,   // Move + 滑行 redirect
                timeoutSeconds: 5f,
                perFrame: () =>
                {
                    var pos = _SamplePosition(player.CurrentMoveInfo, _world.ElapsedTicks);
                    Assert.GreaterOrEqual(pos.y, PenetrationLimitZ, "滑行過程不得穿入牆面");
                });

            Assert.AreEqual(2, events.Count, "應恰有兩個事件:接受 Move、撞牆滑行");
            var slide = events[1];
            Assert.Greater(slide.Speed, 0f, "斜向撞牆應轉為滑行而非停止");
            Assert.AreEqual(MoveSpeed * Mathf.Sqrt(0.5f), slide.Speed, 0.05f, "滑行速度應為切線分量");
            Assert.AreEqual(0f, slide.Facing.y, 0.05f, "滑行方向應平行牆面");
            Assert.Greater(slide.Facing.x, 0.99f, "滑行方向應為 +X");

            // 穩定滑行期間不得灑事件
            var countAfterRedirect = events.Count;
            var xBefore = _SamplePosition(player.CurrentMoveInfo, _world.ElapsedTicks).x;
            yield return _PumpUntil(() => false, timeoutSeconds: 0.5f);
            Assert.AreEqual(countAfterRedirect, events.Count, "穩定滑行期間不應再發 MoveEvent");
            var xAfter = _SamplePosition(player.CurrentMoveInfo, _world.ElapsedTicks).x;
            Assert.Greater(xAfter, xBefore, "滑行應沿 +X 前進");
        }

        [UnityTest]
        public IEnumerator MoveIntoTouchingWallTest()
        {
            var player = _Enter(out var events);

            // 先正面撞停
            player.Move(new Vector2(0f, -1f));
            yield return _PumpUntil(() => player.CurrentMoveInfo.Speed == 0f, timeoutSeconds: 5f);
            Assert.AreEqual(0f, player.CurrentMoveInfo.Speed, "前置條件:已撞牆停止");

            // 超過節流間隔後,貼牆再朝牆 Move:應被接受,且發出的 MoveInfo 一開始就是停狀態(不含穿牆路徑)
            yield return _PumpUntil(() => false, timeoutSeconds: 0.15f);
            events.Clear();
            var positionBefore = player.CurrentMoveInfo.Position;

            var accepted = player.Move(new Vector2(0f, -1f));
            Assert.IsTrue(accepted, "貼牆朝牆的 Move 仍應被接受");
            Assert.AreEqual(1, events.Count, "貼牆即滑應只發一個(已處理過碰撞的)MoveInfo");
            Assert.AreEqual(0f, events[0].Speed, "正對牆的貼牆 Move 應立即轉為停狀態");
            Assert.AreEqual(positionBefore.x, events[0].Position.x, 0.05f, "位置不應改變");
            Assert.AreEqual(positionBefore.y, events[0].Position.y, 0.05f, "位置不應改變(不得被推進牆)");
        }

        [UnityTest]
        public IEnumerator SpawnDepenetrationTest()
        {
            // 出生點設在牆體正中(z=-3):Enter 後應被推出,不得嵌在牆裡
            _worldInfo.Entrance = new Vector3(0f, 0f, -3f);

            var player = _Enter(out _);
            var pos = player.CurrentMoveInfo.Position;

            Assert.IsFalse(_world.Terrain.ComputePenetration(Radius, pos, out _),
                $"出生後不得仍嵌在障礙內(位置 {pos})");
            var outsideZ = pos.y >= ContactZ - 0.05f || pos.y <= -3.8f + 0.05f;
            var outsideX = Mathf.Abs(pos.x) >= 2.5f + Radius - 0.05f;
            Assert.IsTrue(outsideZ || outsideX, $"出生位置應在牆體之外(位置 {pos})");
            yield break;
        }
    }
}
