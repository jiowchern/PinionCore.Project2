using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using PinionCore.Project2.Shared;
using UnityEngine;
using UnityEngine.TestTools;

namespace PinionCore.Project2.Tests
{
    /// <summary>
    /// ActionConfig.ChainWindow(接招窗)的免場景權威測試(仿 HitDetectionTests):
    /// 窗 > 0 的一次性動作播完後,控制狀態(含 Playables 白名單)續留至窗到期 ——
    /// 窗內 Play 白名單動作照常接招、窗外(到期)自動轉移 Next 且 combo 分支失效。
    /// 時鐘是真實 Stopwatch,斷言一律「輪詢直到條件成立 + 逾時」,不比對精確幀。
    /// </summary>
    public class ChainWindowTests
    {
        PinionCore.Project2.Worlds.World _world;
        ActionConfig _attack0;

        [SetUp]
        public void SetUp()
        {
            var worldInfo = ScriptableObject.CreateInstance<WorldConfig>();
            worldInfo.Name = "ChainWindowTestWorld";
            worldInfo.TerrainPrefab = new UnityEngine.AddressableAssets.AssetReferenceGameObject("84e3641b69ee6b2419379df04933bb0d");

            _attack0 = _Config(ActionType.UnarmedAttack0, loop: false, duration: 0.3f);

            var actorConfig = ScriptableObject.CreateInstance<ActorConfig>();
            actorConfig.Name = "TestActor";
            actorConfig.Actions = new[]
            {
                _Config(ActionType.AdventureIdle, loop: true, duration: 0.5f),
                _Config(ActionType.UnarmedIdle, loop: true, duration: 0.5f),
                _attack0,
                _Config(ActionType.UnarmedAttack0_0, loop: false, duration: 0.3f),
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

        PinionCore.Project2.Worlds.PlayerController _Enter(List<ActionType> transitions)
        {
            var actorId = System.Guid.NewGuid();
            var entered = false;
            ((IWorld)_world).Enter(actorId, new ActorInfo { ModelName = "TestActor", DisplayName = "Chainer" })
                .OnValue += (r, _) => entered = r;
            Assert.IsTrue(entered, "應成功進入世界");

            PinionCore.Project2.Worlds.PlayerController found = null;
            foreach (var controller in _world.ControllerItems)
            {
                System.Guid id = controller.ActorId;
                if (id == actorId)
                    found = controller;
            }
            Assert.IsNotNull(found, "進場後應在 ControllerItems 中");
            ((IControllable)found).TransitionEvent += t => transitions.Add(t.Current.Action);
            return found;
        }

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

        // 進場並打出 UnarmedAttack0,等它自然播完(窗開啟中);回傳播完前的 UnarmedIdle 轉移次數
        IEnumerator _EnterAndFinishAttack0(PinionCore.Project2.Worlds.PlayerController controller, List<ActionType> transitions, System.Action<int> idleCountOut)
        {
            yield return _Drive(2);   // 先讓初始狀態 Enter
            Assert.IsTrue(_Play(controller, ActionType.UnarmedIdle), "進戰鬥應被接受");
            Assert.IsTrue(_Play(controller, ActionType.UnarmedAttack0), "出招應被接受");

            var started = false;
            yield return _DriveUntil(
                () => controller.Player.CurrentActionConfig != null && controller.Player.CurrentActionConfig.Action == ActionType.UnarmedAttack0,
                5f, d => started = d);
            Assert.IsTrue(started, "UnarmedAttack0 應開始播放");

            var idleCount = _Count(transitions, ActionType.UnarmedIdle);

            var finished = false;
            yield return _DriveUntil(() => controller.Player.CurrentActionConfig == null, 5f, d => finished = d);
            Assert.IsTrue(finished, "UnarmedAttack0 應自然播完");
            idleCountOut(idleCount);
        }

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator ChainWithinWindowSucceeds()
        {
            _attack0.ChainWindow = 2f;   // 寬窗:播完後的接招判定不受幀率抖動影響
            var transitions = new List<ActionType>();
            var controller = _Enter(transitions);

            var idleBefore = 0;
            yield return _EnterAndFinishAttack0(controller, transitions, c => idleBefore = c);

            // 播完但窗未到期:狀態仍是 UnarmedAttack0(未轉移 UnarmedIdle),白名單照常生效
            Assert.AreEqual(idleBefore, _Count(transitions, ActionType.UnarmedIdle),
                "接招窗內不得先轉移到 Next(UnarmedIdle)");
            Assert.IsFalse(_Play(controller, ActionType.UnarmedWalk), "窗內非白名單動作仍應拒收");
            Assert.IsTrue(_Play(controller, ActionType.UnarmedAttack0_0), "窗內接招 UnarmedAttack0_0 應被接受");
            Assert.AreEqual(1, _Count(transitions, ActionType.UnarmedAttack0_0), "接招應轉移到 UnarmedAttack0_0");
            Assert.AreEqual(idleBefore, _Count(transitions, ActionType.UnarmedIdle),
                "接招不得中途彈回 UnarmedIdle");
        }

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator WindowExpiryTransitionsToNext()
        {
            _attack0.ChainWindow = 0.25f;
            var transitions = new List<ActionType>();
            var controller = _Enter(transitions);

            var idleBefore = 0;
            yield return _EnterAndFinishAttack0(controller, transitions, c => idleBefore = c);

            // 無人接招:窗到期應補發 Next 轉移回 UnarmedIdle
            var expired = false;
            yield return _DriveUntil(() => _Count(transitions, ActionType.UnarmedIdle) > idleBefore, 5f, d => expired = d);
            Assert.IsTrue(expired, "接招窗到期應轉移到 Next(UnarmedIdle)");

            // 回到 UnarmedIdle 後 combo 分支失效(UnarmedIdle 白名單無 UnarmedAttack0_0)
            Assert.IsFalse(_Play(controller, ActionType.UnarmedAttack0_0), "窗到期後接招應被拒收");
        }
    }
}
