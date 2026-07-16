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
    /// 伺服器權威視野的直接單元測試(WorldTestScript 模式:直接 new World,不載場景)。
    /// 地形用 Terrain.prefab:牆世界盒 x∈[-2.5,2.5], y∈[0,1], z∈[-3.5,-2.5]。
    /// 視野判定一律以 world.TickSight() 顯式驅動(不跑 world.Update),debounce 次數可精確斷言;
    /// 移動走真實時鐘,等待迴圈只 yield 取樣 MoveInfo,不觸發視野 tick。
    /// 位置前置條件依賴幀間隔,編輯器嚴重卡頓時可能誤差過大,訊息會標明是前置失敗。
    /// </summary>
    public class WorldSightTests
    {
        const float Radius = 0.3f;

        PinionCore.Project2.Worlds.World _world;
        WorldConfig _worldInfo;

        void _CreateWorld(float moveSpeed, float sightRadius, Vector3 entrance)
        {
            _worldInfo = ScriptableObject.CreateInstance<WorldConfig>();
            _worldInfo.Name = "SightTestWorld";
            _worldInfo.TerrainPrefab = new UnityEngine.AddressableAssets.AssetReferenceGameObject("84e3641b69ee6b2419379df04933bb0d");
            _worldInfo.Entrance = entrance;

            // 移動走 Locomotion:單段直線走路,段速度 = moveSpeed(位置取樣仍是等速直線外推)
            var walk = ScriptableObject.CreateInstance<ActionConfig>();
            walk.Action = ActionType.AdventureWalk;
            walk.Category = ActionCategory.Locomotion;
            walk.Duration = 1f;
            walk.Segments = new[]
            {
                new ActionConfig.MotionSegment { LocalOffset = new Vector2(0f, moveSpeed), Duration = 1f },
            };

            var actorConfig = ScriptableObject.CreateInstance<ActorConfig>();
            actorConfig.Name = "TestActor";
            actorConfig.MoveAcceptInterval = 0.05f;
            actorConfig.Radius = Radius;
            actorConfig.SightRadius = sightRadius;
            actorConfig.Actions = new[] { walk };

            _world = new PinionCore.Project2.Worlds.World(System.Guid.NewGuid(), _worldInfo, new[] { actorConfig });
        }

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();
            _world = null;
        }

        PinionCore.Project2.Worlds.PlayerController _Enter(string name)
        {
            IWorld world = _world;
            var actorId = System.Guid.NewGuid();
            var entered = false;
            world.Enter(actorId, new ActorInfo { ModelName = "TestActor", DisplayName = name }).OnValue += (ok, error) => entered = ok;
            Assert.IsTrue(entered, $"{name} Enter 應成功");
            return _world.ControllerItems.First(c => c.ActorId == actorId);
        }

        static bool _Sees(PinionCore.Project2.Worlds.PlayerController observer, PinionCore.Project2.Worlds.PlayerController target)
        {
            return observer.VisibleActors.Items.Contains(target);
        }

        // 以當下世界時間取樣權威位置(MoveInfo 是等速直線,不需要跑 world.Update)
        Vector2 _Position(PinionCore.Project2.Worlds.PlayerController controller)
        {
            var info = controller.Player.CurrentMoveInfo;
            var elapsed = (_world.ElapsedTicks - info.StartTicks) / (double)System.TimeSpan.TicksPerSecond;
            MoveSampler.Sample(info, System.Math.Max(0.0, elapsed), out var position, out _);
            return position;
        }

        float _Distance(PinionCore.Project2.Worlds.PlayerController a, PinionCore.Project2.Worlds.PlayerController b)
        {
            return Vector2.Distance(_Position(a), _Position(b));
        }

        // 朝 direction 走到 arrived 成立即 Stop;過程零視野 tick(視野變化全由測試顯式驅動)
        IEnumerator _WalkUntil(PinionCore.Project2.Worlds.PlayerController mover, Vector2 direction, System.Func<bool> arrived, float timeoutSeconds)
        {
            // 走位直接呼叫伺服器端 Player 純模擬核心(ICharacter 已不含移動介面)
            var accepted = false;
            mover.Player.Move(direction.normalized).OnValue += (r, error) => accepted = r;
            Assert.IsTrue(accepted, "Move 應被接受");
            var deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!arrived() && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.IsTrue(arrived(), "移動未在時限內到達目標");
            mover.Player.Stop();
        }

        [UnityTest]
        public IEnumerator MutualVisibilityAtEnterTest()
        {
            _CreateWorld(moveSpeed: 1f, sightRadius: 5f, entrance: Vector3.zero);
            var a = _Enter("A");

            // B 進入前先訂閱 A 的視野:Enter 當下就該收到 B 的 Supply(綁定 replay 正確性)
            var supplied = new List<IActor>();
            IPlayer viewOfA = a;
            viewOfA.Actors.Base.Supply += actor => supplied.Add(actor);
            supplied.Clear();   // 丟掉訂閱 replay(此時名單只有 A 自己)

            var b = _Enter("B");

            Assert.IsTrue(_Sees(a, a), "A 應看得到自己");
            Assert.IsTrue(_Sees(a, b), "同點進入,A 應看得到 B");
            Assert.IsTrue(_Sees(b, b), "B 應看得到自己");
            Assert.IsTrue(_Sees(b, a), "同點進入,B 應看得到 A");
            Assert.AreEqual(2, a.VisibleActors.Items.Count, "A 的視野應恰為 {A, B}");
            Assert.AreEqual(2, b.VisibleActors.Items.Count, "B 的視野應恰為 {A, B}");

            IActor bActor = b;
            Assert.AreEqual(1, supplied.Count, "B 進入當下 A 應收到恰一次 Supply");
            Assert.AreSame(bActor, supplied[0], "Supply 的對象應是 B");
            yield break;
        }

        [UnityTest]
        public IEnumerator OutOfRangeAtEnterTest()
        {
            _CreateWorld(moveSpeed: 10f, sightRadius: 2f, entrance: Vector3.zero);
            var a = _Enter("A");

            // A 往 +X 走離出生點(遠離牆),超過離開半徑(2.2)再停
            yield return _WalkUntil(a, Vector2.right, () => _Position(a).x > 2.6f, 5f);

            var b = _Enter("B");
            Assert.IsFalse(_Sees(b, a), "B 進入時 A 已在視野外,不應互見");
            Assert.IsFalse(_Sees(a, b), "A 也不應看到 B");
            Assert.IsTrue(_Sees(a, a), "A 仍看得到自己");
            Assert.IsTrue(_Sees(b, b), "B 仍看得到自己");
        }

        [UnityTest]
        public IEnumerator DebounceExactCountTest()
        {
            _CreateWorld(moveSpeed: 10f, sightRadius: 2f, entrance: Vector3.zero);
            var a = _Enter("A");
            var b = _Enter("B");
            Assert.IsTrue(_Sees(a, b), "前置:同點進入互見");

            // B 走到距離 3(> 離開半徑 2.2)再停,過程零視野 tick → debounce 從 0 起算
            yield return _WalkUntil(b, Vector2.right, () => _Position(b).x > 3f, 5f);

            _world.TickSight();
            Assert.IsTrue(_Sees(a, b), "第 1 次失敗 tick 不應移除(debounce)");
            _world.TickSight();
            Assert.IsTrue(_Sees(a, b), "第 2 次失敗 tick 不應移除(debounce)");
            _world.TickSight();
            Assert.IsFalse(_Sees(a, b), "第 3 次失敗 tick 應移除");
            Assert.IsFalse(_Sees(b, a), "同配置半徑,移除應雙向");
            Assert.IsTrue(_Sees(a, a), "A 仍看得到自己");
            Assert.IsTrue(_Sees(b, b), "B 仍看得到自己");
        }

        [UnityTest]
        public IEnumerator HysteresisBandTest()
        {
            _CreateWorld(moveSpeed: 1f, sightRadius: 2f, entrance: Vector3.zero);
            var a = _Enter("A");
            var b = _Enter("B");

            // 停在 (2.0, 2.2) 緩衝帶內:已可見配對用離開半徑 2.2 判,不應移除
            yield return _WalkUntil(b, Vector2.right, () => _Position(b).x >= 2.05f, 10f);
            var stopX = _Position(b).x;
            Assert.Greater(stopX, 2.0f, "前置:應停在緩衝帶內");
            Assert.Less(stopX, 2.2f, "前置:應停在緩衝帶內(編輯器卡頓可能超出)");
            for (var i = 0; i < 10; i++)
                _world.TickSight();
            Assert.IsTrue(_Sees(a, b), "緩衝帶內(未超過離開半徑 2.2)不應移除");
            Assert.IsTrue(_Sees(b, a), "緩衝帶內不應移除(反向)");

            // 走出到 3.0:超過離開半徑,debounce 滿即移除
            yield return _WalkUntil(b, Vector2.right, () => _Position(b).x > 3f, 5f);
            for (var i = 0; i < PinionCore.Project2.Worlds.Sight.InvisibleDebounceTicks; i++)
                _world.TickSight();
            Assert.IsFalse(_Sees(a, b), "超出離開半徑後 debounce 滿應移除");

            // 走回緩衝帶 (2.0, 2.2):未見配對用進入半徑 2.0 判,不應加回
            yield return _WalkUntil(b, Vector2.left, () => _Position(b).x <= 2.15f, 5f);
            var backX = _Position(b).x;
            Assert.Greater(backX, 2.0f, "前置:應停在緩衝帶內(編輯器卡頓可能超出)");
            for (var i = 0; i < 10; i++)
                _world.TickSight();
            Assert.IsFalse(_Sees(a, b), "緩衝帶內(未進入半徑 2.0)不應加回");

            // 走進 < 1.9:下一 tick 立即加回(加入無 debounce)
            yield return _WalkUntil(b, Vector2.left, () => _Position(b).x < 1.9f, 5f);
            _world.TickSight();
            Assert.IsTrue(_Sees(a, b), "進入半徑內首次 tick 即加回");
            Assert.IsTrue(_Sees(b, a), "加回應雙向");
        }

        [UnityTest]
        public IEnumerator OcclusionBehindWallTest()
        {
            // Entrance 在牆北側 (0,0,-1);牆盒 x∈[-2.5,2.5], y∈[0,1], z∈[-3.5,-2.5]
            _CreateWorld(moveSpeed: 10f, sightRadius: 5f, entrance: new Vector3(0f, 0f, -1f));
            var a = _Enter("A");
            var b = _Enter("B");
            Assert.IsTrue(_Sees(a, b), "前置:同點進入互見");

            // B 繞過牆走到正南側 (0, -4.5):與 A 距離 3.5 < 5,但視線被牆擋
            yield return _WalkUntil(b, Vector2.right, () => _Position(b).x >= 4f, 5f);
            yield return _WalkUntil(b, Vector2.down, () => _Position(b).y <= -4.5f, 5f);
            yield return _WalkUntil(b, Vector2.left, () => _Position(b).x <= 0f, 5f);

            Assert.Less(_Distance(a, b), 5f, "前置:距離仍在視野半徑內,遮蔽才是唯一失去原因");
            for (var i = 0; i < PinionCore.Project2.Worlds.Sight.InvisibleDebounceTicks; i++)
                _world.TickSight();
            Assert.IsFalse(_Sees(a, b), "牆後的 B 應被移出 A 的視野");
            Assert.IsFalse(_Sees(b, a), "遮蔽對稱,A 也應被移出 B 的視野");

            // B 走回東側再北上到 (4, -1):視線不過牆且距離 4 < 5 → 立即可見
            yield return _WalkUntil(b, Vector2.right, () => _Position(b).x >= 4f, 5f);
            yield return _WalkUntil(b, Vector2.up, () => _Position(b).y >= -1f, 5f);
            _world.TickSight();
            Assert.IsTrue(_Sees(a, b), "視線恢復且在半徑內應立即加回");
            Assert.IsTrue(_Sees(b, a), "加回應雙向");
        }

        [UnityTest]
        public IEnumerator LeaveCleansSightStateTest()
        {
            _CreateWorld(moveSpeed: 10f, sightRadius: 2f, entrance: Vector3.zero);
            var a = _Enter("A");
            var b = _Enter("B");

            var unsupplied = new List<IActor>();
            IPlayer viewOfA = a;
            viewOfA.Actors.Base.Unsupply += actor => unsupplied.Add(actor);

            // B 走出視野,tick 一次讓 debounce 進行中(streak=1,未達移除)
            yield return _WalkUntil(b, Vector2.right, () => _Position(b).x > 3f, 5f);
            _world.TickSight();
            Assert.IsTrue(_Sees(a, b), "前置:debounce 未滿,B 仍在 A 視野");

            IWorld world = _world;
            System.Guid bId = b.ActorId;
            var left = false;
            world.Leave(bId).OnValue += (r, error) => left = r;
            Assert.IsTrue(left, "Leave 應成功");

            IActor bActor = b;
            Assert.IsFalse(_Sees(a, b), "Leave 應立即移除,不等 debounce");
            Assert.AreEqual(0, b.VisibleActors.Items.Count, "離開者的視野應清空");
            Assert.AreEqual(1, unsupplied.Count(x => ReferenceEquals(x, bActor)), "A 應恰收到一次 B 的 Unsupply");

            // 殘餘 streak 不應影響後續 tick,也不應重複 Unsupply
            _world.TickSight();
            _world.TickSight();
            Assert.IsTrue(_Sees(a, a), "A 仍看得到自己");
            Assert.AreEqual(1, a.VisibleActors.Items.Count, "A 的視野應只剩自己");
            Assert.AreEqual(1, unsupplied.Count(x => ReferenceEquals(x, bActor)), "不應重複 Unsupply");
        }
    }
}
