using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using PinionCore.Project2.Shared;
using UnityEngine;
using UnityEngine.TestTools;

namespace PinionCore.Project2.Tests
{
    /// <summary>
    /// 攻擊命中判定的免場景權威管線測試(仿 WorldTestScript):
    /// 手作 ActionConfig(含 HitSegments)直接 new World,兩顆 actor 同 Entrance 出生(距離 0),
    /// 攻擊者 Play(BattleIdle)→Play(BattleAttack),斷言目標被 HitResolver 命中後
    /// 轉移到對應 Damage 硬直狀態。時鐘是真實 Stopwatch,斷言一律「輪詢直到條件成立 + 逾時」,
    /// 不比對精確幀。
    /// </summary>
    public class HitDetectionTests
    {
        PinionCore.Project2.Worlds.World _world;
        ActionConfig _attack;

        [SetUp]
        public void SetUp()
        {
            var worldInfo = ScriptableObject.CreateInstance<WorldConfig>();
            worldInfo.Name = "HitTestWorld";
            worldInfo.TerrainPrefab = new UnityEngine.AddressableAssets.AssetReferenceGameObject("84e3641b69ee6b2419379df04933bb0d");

            _attack = _Config(ActionType.BattleAttack, loop: false, duration: 0.6f);

            var actorConfig = ScriptableObject.CreateInstance<ActorConfig>();
            actorConfig.Name = "TestActor";
            actorConfig.Actions = new[]
            {
                _Config(ActionType.AdventureIdle, loop: true, duration: 0.5f),
                _Config(ActionType.BattleIdle, loop: true, duration: 0.5f),
                _attack,
                _Config(ActionType.AdventureDamage, loop: false, duration: 0.4f),
                _Config(ActionType.BattleDamage, loop: false, duration: 0.4f),
            };
            _world = new PinionCore.Project2.Worlds.World(System.Guid.NewGuid(), worldInfo, new[] { actorConfig });
        }

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();
            _world = null;
        }

        static ActionConfig _Config(ActionType action, bool loop, float duration)
        {
            var config = ScriptableObject.CreateInstance<ActionConfig>();
            config.Action = action;
            config.Loop = loop;
            config.Duration = duration;
            config.Segments = new[] { new ActionConfig.MotionSegment { LocalOffset = Vector2.zero, Duration = duration } };
            return config;
        }

        static ActionConfig.HitSegment _Circle(Vector2 offset, float radius, float start, float duration) => new ActionConfig.HitSegment
        {
            Shape = HitShapeType.Circle,
            LocalOffset = offset,
            Radius = radius,
            StartTime = start,
            Duration = duration,
        };

        // 進場並取回 controller;訂閱 TransitionEvent 記錄每次轉移的 Current 動作
        //(訂閱即回放當前狀態,首筆是進場時的 AdventureIdle)
        PinionCore.Project2.Worlds.PlayerController _Enter(string name, List<ActionType> transitions)
        {
            var actorId = System.Guid.NewGuid();
            var entered = false;
            ((IWorld)_world).Enter(actorId, new ActorInfo { ModelName = "TestActor", DisplayName = name })
                .OnValue += (r, _) => entered = r;
            Assert.IsTrue(entered, $"{name} 應成功進入世界");

            PinionCore.Project2.Worlds.PlayerController found = null;
            foreach (var controller in _world.ControllerItems)
            {
                System.Guid id = controller.ActorId;
                if (id == actorId)
                    found = controller;
            }
            Assert.IsNotNull(found, $"{name} 進場後應在 ControllerItems 中");
            ((IControllable)found).TransitionEvent += t => transitions.Add(t.Current.Action);
            return found;
        }

        // 每幀泵 World.Update 直到條件成立;逾時回傳 false
        IEnumerator _DriveUntil(System.Func<bool> condition, float seconds, System.Action<bool> onDone)
        {
            var deadline = Time.realtimeSinceStartup + seconds;
            while (!condition() && Time.realtimeSinceStartup < deadline)
            {
                _world.Update();
                yield return null;
            }
            onDone(condition());
        }

        // 固定泵 N 幀(讓初始/新推入的控制狀態 Enter)
        IEnumerator _Drive(int frames)
        {
            for (var i = 0; i < frames; i++)
            {
                _world.Update();
                yield return null;
            }
        }

        static bool _Play(PinionCore.Project2.Worlds.PlayerController controller, ActionType action)
        {
            var accepted = false;
            ((IControllable)controller).Play(action, Vector2.zero).OnValue += (r, _) => accepted = r;
            return accepted;
        }

        static int _Count(List<ActionType> transitions, ActionType action)
        {
            var count = 0;
            foreach (var t in transitions)
                if (t == action)
                    count++;
            return count;
        }

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator HitDamagesAdventureTargetAndNotSelf()
        {
            _attack.HitSegments = new[] { _Circle(Vector2.zero, 1f, 0.05f, 0.4f) };
            var attackerLog = new List<ActionType>();
            var victimLog = new List<ActionType>();
            var attacker = _Enter("Attacker", attackerLog);
            _Enter("Victim", victimLog);

            yield return _Drive(2);   // 先讓初始狀態 Enter
            Assert.IsTrue(_Play(attacker, ActionType.BattleIdle), "進戰鬥應被接受");
            Assert.IsTrue(_Play(attacker, ActionType.BattleAttack), "出招應被接受");

            var hit = false;
            yield return _DriveUntil(() => _Count(victimLog, ActionType.AdventureDamage) >= 1, 5f, d => hit = d);
            Assert.IsTrue(hit, "冒險態目標被命中應轉移到 AdventureDamage");

            Assert.AreEqual(0, _Count(attackerLog, ActionType.AdventureDamage) + _Count(attackerLog, ActionType.BattleDamage),
                "攻擊者不得打中自己");
        }

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator HitDamagesBattleTarget()
        {
            _attack.HitSegments = new[] { _Circle(Vector2.zero, 1f, 0.05f, 0.4f) };
            var attackerLog = new List<ActionType>();
            var victimLog = new List<ActionType>();
            var attacker = _Enter("Attacker", attackerLog);
            var victim = _Enter("Victim", victimLog);

            yield return _Drive(2);
            Assert.IsTrue(_Play(victim, ActionType.BattleIdle), "目標進戰鬥應被接受");
            Assert.IsTrue(_Play(attacker, ActionType.BattleIdle), "進戰鬥應被接受");
            Assert.IsTrue(_Play(attacker, ActionType.BattleAttack), "出招應被接受");

            var hit = false;
            yield return _DriveUntil(() => _Count(victimLog, ActionType.BattleDamage) >= 1, 5f, d => hit = d);
            Assert.IsTrue(hit, "戰鬥態目標被命中應轉移到 BattleDamage");
        }

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator OutOfRangeDoesNotDamage()
        {
            // hitbox 錨在前方 5m、半徑 0.2:目標與攻擊者同點(距離 5 > 0.2+0.3)不應命中
            _attack.HitSegments = new[] { _Circle(new Vector2(0f, 5f), 0.2f, 0f, 0.6f) };
            var attackerLog = new List<ActionType>();
            var victimLog = new List<ActionType>();
            var attacker = _Enter("Attacker", attackerLog);
            _Enter("Victim", victimLog);

            yield return _Drive(2);
            Assert.IsTrue(_Play(attacker, ActionType.BattleIdle), "進戰鬥應被接受");
            var idleBeforeAttack = _Count(attackerLog, ActionType.BattleIdle);
            Assert.IsTrue(_Play(attacker, ActionType.BattleAttack), "出招應被接受");

            // 等攻擊自然播完回 BattleIdle(= 命中窗已全數結清)
            var finished = false;
            yield return _DriveUntil(() => _Count(attackerLog, ActionType.BattleIdle) > idleBeforeAttack, 10f, d => finished = d);
            Assert.IsTrue(finished, "攻擊應自然結束回 BattleIdle");

            Assert.AreEqual(0, _Count(victimLog, ActionType.AdventureDamage) + _Count(victimLog, ActionType.BattleDamage),
                "超出範圍的攻擊不得造成受擊");
        }

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator OneHitPerSwingAndNextSwingHitsAgain()
        {
            // 命中窗蓋滿整個動作:每幀都與目標重疊,去重應保證一次揮擊只中一次
            _attack.HitSegments = new[] { _Circle(Vector2.zero, 1f, 0f, 0.6f) };
            var attackerLog = new List<ActionType>();
            var victimLog = new List<ActionType>();
            var attacker = _Enter("Attacker", attackerLog);
            _Enter("Victim", victimLog);

            yield return _Drive(2);
            Assert.IsTrue(_Play(attacker, ActionType.BattleIdle), "進戰鬥應被接受");
            var idleBeforeAttack = _Count(attackerLog, ActionType.BattleIdle);
            Assert.IsTrue(_Play(attacker, ActionType.BattleAttack), "出招應被接受");

            var finished = false;
            yield return _DriveUntil(() => _Count(attackerLog, ActionType.BattleIdle) > idleBeforeAttack, 10f, d => finished = d);
            Assert.IsTrue(finished, "第一次攻擊應自然結束回 BattleIdle");
            Assert.AreEqual(1, _Count(victimLog, ActionType.AdventureDamage),
                "命中窗蓋滿整個動作時,一次揮擊同一目標只得命中一次");

            // 第二次揮擊是新的動作實例:去重集重置,目標應再次被命中
            Assert.IsTrue(_Play(attacker, ActionType.BattleAttack), "第二次出招應被接受");
            var hitAgain = false;
            yield return _DriveUntil(() => _Count(victimLog, ActionType.AdventureDamage) >= 2, 5f, d => hitAgain = d);
            Assert.IsTrue(hitAgain, "新揮擊應能再次命中同一目標");
        }
    }
}
