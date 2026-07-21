using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using PinionCore.Project2.Shared;
using UnityEngine;
using UnityEngine.TestTools;

namespace PinionCore.Project2.Tests
{
    /// <summary>
    /// 抓取系統(GrabResolver)的免場景權威測試(仿 ChainWindowTests):
    /// 手作 ActionConfig 直連 world,雙(三)玩家同點進場 —— 玩家間無碰撞,
    /// UnarmedGrabStart 用大半徑命中圈保證同點必中;配對後被抓者吸附錨點、白名單鎖死,
    /// 解體路徑(第三方打抓取者/任一方離場)各自驗證。
    /// 時鐘是真實 Stopwatch,斷言一律「輪詢直到條件成立 + 逾時」。
    /// </summary>
    public class GrabTests
    {
        // GrabResolver.AnchorDistance 的鏡像常數:被抓者吸附在抓取者前方此距離
        const float AnchorDistance = 0.9f;

        PinionCore.Project2.Worlds.World _world;

        [SetUp]
        public void SetUp()
        {
            var worldInfo = ScriptableObject.CreateInstance<WorldConfig>();
            worldInfo.Name = "GrabTestWorld";
            worldInfo.TerrainPrefab = new UnityEngine.AddressableAssets.AssetReferenceGameObject("84e3641b69ee6b2419379df04933bb0d");

            var actorConfig = ScriptableObject.CreateInstance<ActorConfig>();
            actorConfig.Name = "TestActor";
            actorConfig.Actions = _BuildRoster();
            _world = new PinionCore.Project2.Worlds.World(System.Guid.NewGuid(), worldInfo, new[] { actorConfig });
        }

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();
            _world = null;
        }

        // 全套手作動作:零位移循環 idle、抓取家族、傷害用攻擊;
        // UnarmedGrabStart 命中圈半徑 1.5(同點必中)、UnarmedAttack 半徑 0.3(只打得到同點目標)
        static ActionConfig[] _BuildRoster()
        {
            var grabStart = _Config(ActionType.UnarmedGrabStart, loop: false, duration: 0.5f);
            grabStart.HitEffect = HitEffectType.Grab;
            grabStart.HitSegments = new[] { _Hit(startTime: 0.05f, duration: 0.4f, radius: 1.5f) };

            var grabAtk1A = _Config(ActionType.UnarmedGrabAtk1A, loop: false, duration: 0.5f);
            grabAtk1A.HitSegments = new[] { _Hit(startTime: 0.05f, duration: 0.4f, radius: 1.5f) };

            var battleAttack = _Config(ActionType.UnarmedAttack, loop: false, duration: 0.5f);
            battleAttack.HitSegments = new[] { _Hit(startTime: 0.05f, duration: 0.3f, radius: 0.3f) };

            var grabWalkA = _Config(ActionType.UnarmedGrabWalkA, loop: true, duration: 0.6f, redirectable: true,
                offset: new Vector2(0f, 1.2f));   // 拖行走速 2 m/s

            return new[]
            {
                _Config(ActionType.AdventureIdle, loop: true, duration: 0.5f),
                _Config(ActionType.UnarmedIdle, loop: true, duration: 0.5f),
                _Config(ActionType.UnarmedDamage, loop: false, duration: 0.4f),
                battleAttack,
                grabStart,
                _Config(ActionType.UnarmedGrabIdleA, loop: true, duration: 0.6f),
                _Config(ActionType.UnarmedGrabIdleB, loop: true, duration: 0.6f),
                grabWalkA,
                _Config(ActionType.UnarmedGrabWalkB, loop: true, duration: 0.6f),
                grabAtk1A,
                _Config(ActionType.UnarmedGrabAtk1B, loop: false, duration: 0.4f),
                _Config(ActionType.UnarmedGrabThrowA, loop: false, duration: 0.8f),
                _Config(ActionType.UnarmedGrabThrowB, loop: false, duration: 0.8f, offset: new Vector2(0f, -2.4f)),  // 沿自身背後飛 2.4m
                _Config(ActionType.UnarmedGrabBreakA, loop: false, duration: 0.5f),
                _Config(ActionType.UnarmedGrabBreakB, loop: false, duration: 0.5f),
            };
        }

        static ActionConfig _Config(ActionType action, bool loop, float duration, bool redirectable = false, Vector2 offset = default)
        {
            var config = ScriptableObject.CreateInstance<ActionConfig>();
            config.Action = action;
            config.Loop = loop;
            config.Redirectable = redirectable;
            config.Duration = duration;
            config.Segments = new[] { new ActionConfig.MotionSegment { LocalOffset = offset, Duration = duration } };
            return config;
        }

        static ActionConfig.HitSegment _Hit(float startTime, float duration, float radius)
        {
            return new ActionConfig.HitSegment
            {
                StartTime = startTime,
                Duration = duration,
                Shape = HitShapeType.Circle,
                LocalOffset = Vector2.zero,
                Radius = radius,
            };
        }

        PinionCore.Project2.Worlds.PlayerController _Enter(string displayName, out System.Guid actorId)
        {
            actorId = System.Guid.NewGuid();
            var id = actorId;
            var entered = false;
            ((IWorld)_world).Enter(id, new ActorInfo { ModelName = "TestActor", DisplayName = displayName })
                .OnValue += (r, _) => entered = r;
            Assert.IsTrue(entered, $"{displayName} 應成功進入世界");

            PinionCore.Project2.Worlds.PlayerController found = null;
            foreach (var controller in _world.ControllerItems)
            {
                System.Guid cid = controller.ActorId;
                if (cid == id)
                    found = controller;
            }
            Assert.IsNotNull(found, $"{displayName} 進場後應在 ControllerItems 中");
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
            return _PlayDir(controller, action, Vector2.zero);
        }

        static bool _PlayDir(PinionCore.Project2.Worlds.PlayerController controller, ActionType action, Vector2 direction)
        {
            var accepted = false;
            ((IControllable)controller).Play(action, direction).OnValue += (r, _) => accepted = r;
            return accepted;
        }

        static ActionType _Node(PinionCore.Project2.Worlds.PlayerController controller)
        {
            return controller.CurrentTransition.Current.Action;
        }

        // 進場兩人(同點)→ 雙方進 Battle → A 出 UnarmedGrabStart → 等配對成立(A=UnarmedGrabIdleA、B=UnarmedGrabIdleB)
        IEnumerator _EstablishGrab(PinionCore.Project2.Worlds.PlayerController grabber, PinionCore.Project2.Worlds.PlayerController victim)
        {
            yield return _Drive(2);   // 初始狀態 Enter
            Assert.IsTrue(_Play(grabber, ActionType.UnarmedIdle), "抓取者進戰鬥應被接受");
            Assert.IsTrue(_Play(victim, ActionType.UnarmedIdle), "被抓者進戰鬥應被接受");
            yield return _Drive(2);
            Assert.IsTrue(_Play(grabber, ActionType.UnarmedGrabStart), "UnarmedGrabStart 應在 UnarmedIdle 白名單內");

            var paired = false;
            yield return _DriveUntil(
                () => _Node(grabber) == ActionType.UnarmedGrabIdleA && _Node(victim) == ActionType.UnarmedGrabIdleB,
                5f, d => paired = d);
            Assert.IsTrue(paired, $"配對應成立(grabber={_Node(grabber)}, victim={_Node(victim)})");
        }

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator GrabConnectPairsAndAnchorsVictim()
        {
            var grabber = _Enter("Grabber", out _);
            var victim = _Enter("Victim", out _);
            yield return _EstablishGrab(grabber, victim);

            // 被抓者吸附:抓取者前方(初始朝向 +y)錨距處、面向抓取者
            var grabberPos = grabber.Player.SamplePositionNow();
            var victimPos = victim.Player.SamplePositionNow();
            var expected = grabberPos + new Vector2(0f, AnchorDistance);
            Assert.Less((victimPos - expected).magnitude, 0.05f,
                $"被抓者應吸附在錨點(actual={victimPos}, expected={expected})");
            var victimFacing = victim.Player.CurrentMoveInfo.Facing;
            Assert.Less((victimFacing - new Vector2(0f, -1f)).magnitude, 0.05f,
                $"被抓者應面向抓取者(facing={victimFacing})");

            // 白名單鎖死:被抓者不能走路/再抓,只有掙脫可用(不實際送出,Play 會轉移)
            Assert.IsFalse(_Play(victim, ActionType.UnarmedWalk), "被抓者 Move 目標應被拒收");
            Assert.IsFalse(_Play(victim, ActionType.UnarmedGrabStart), "被抓者反抓應被拒收");

            // 抓取期間位置持續貼錨(循環 wrap park 被同幀校正蓋回):再駐留一段時間仍貼錨
            yield return _Drive(50);
            grabberPos = grabber.Player.SamplePositionNow();
            victimPos = victim.Player.SamplePositionNow();
            expected = grabberPos + new Vector2(0f, AnchorDistance);
            Assert.Less((victimPos - expected).magnitude, 0.05f,
                $"駐留後被抓者仍應貼錨(actual={victimPos}, expected={expected})");
        }

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator GrabWhiffRecoversToUnarmedIdle()
        {
            var grabber = _Enter("Grabber", out _);
            yield return _Drive(2);
            Assert.IsTrue(_Play(grabber, ActionType.UnarmedIdle), "進戰鬥應被接受");
            yield return _Drive(2);
            Assert.IsTrue(_Play(grabber, ActionType.UnarmedGrabStart), "空揮 UnarmedGrabStart 應被接受");

            // 無目標:UnarmedGrabStart 自然播完 → Next = UnarmedIdle,不得殘留 UnarmedGrabIdleA
            var started = false;
            yield return _DriveUntil(() => _Node(grabber) == ActionType.UnarmedGrabStart, 5f, d => started = d);
            Assert.IsTrue(started, "UnarmedGrabStart 應開始");
            var recovered = false;
            yield return _DriveUntil(() => _Node(grabber) == ActionType.UnarmedIdle, 5f, d => recovered = d);
            Assert.IsTrue(recovered, $"空揮應自然回 UnarmedIdle(node={_Node(grabber)})");
        }

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator ThirdPartyHitOnGrabberDissolvesPair()
        {
            var grabber = _Enter("Grabber", out _);
            var victim = _Enter("Victim", out _);
            var attacker = _Enter("Third", out _);
            yield return _EstablishGrab(grabber, victim);

            // 第三方(與抓取者同點)攻擊:小半徑只打得到抓取者(被抓者已被吸到 0.9m 外)
            Assert.IsTrue(_Play(attacker, ActionType.UnarmedIdle), "第三方進戰鬥應被接受");
            yield return _Drive(2);
            Assert.IsTrue(_Play(attacker, ActionType.UnarmedAttack), "第三方攻擊應被接受");

            var dissolved = false;
            yield return _DriveUntil(
                () => _Node(grabber) == ActionType.UnarmedDamage && _Node(victim) == ActionType.UnarmedIdle,
                5f, d => dissolved = d);
            Assert.IsTrue(dissolved,
                $"打抓取者應解體:抓取者進硬直、被抓者獲釋(grabber={_Node(grabber)}, victim={_Node(victim)})");

            // 被抓者已自由:掙脫(只在被抓白名單)應被拒收,證明白名單已切換
            Assert.IsFalse(_Play(victim, ActionType.UnarmedGrabBreakB), "獲釋後掙脫應不在白名單");
        }

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator LeaveGrabberFreesVictim()
        {
            var grabber = _Enter("Grabber", out var grabberId);
            var victim = _Enter("Victim", out _);
            yield return _EstablishGrab(grabber, victim);

            var left = false;
            ((IWorld)_world).Leave(grabberId).OnValue += (r, _) => left = r;
            Assert.IsTrue(left, "抓取者離場應成功");

            var freed = false;
            yield return _DriveUntil(() => _Node(victim) == ActionType.UnarmedIdle, 5f, d => freed = d);
            Assert.IsTrue(freed, $"抓取者離場後被抓者應獲釋(node={_Node(victim)})");
            Assert.IsFalse(_Play(victim, ActionType.UnarmedGrabBreakB), "獲釋後掙脫應不在白名單");
        }

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator LeaveVictimFreesGrabber()
        {
            var grabber = _Enter("Grabber", out _);
            var victim = _Enter("Victim", out var victimId);
            yield return _EstablishGrab(grabber, victim);

            var left = false;
            ((IWorld)_world).Leave(victimId).OnValue += (r, _) => left = r;
            Assert.IsTrue(left, "被抓者離場應成功");

            var freed = false;
            yield return _DriveUntil(() => _Node(grabber) == ActionType.UnarmedIdle, 5f, d => freed = d);
            Assert.IsTrue(freed, $"被抓者離場後抓取者應回 UnarmedIdle(node={_Node(grabber)})");
        }

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator UnarmedGrabWalkForwardsVictimAlongAnchor()
        {
            var grabber = _Enter("Grabber", out _);
            var victim = _Enter("Victim", out _);
            yield return _EstablishGrab(grabber, victim);

            // 拖行:抓取者往 +x 走,被抓者應鏡射到 UnarmedGrabWalkB 並持續貼在新錨軸前方
            var dragDirection = new Vector2(1f, 0f);
            var start = grabber.Player.SamplePositionNow();
            Assert.IsTrue(_PlayDir(grabber, ActionType.UnarmedGrabWalkA, dragDirection), "拖行走路應在 UnarmedGrabIdleA 白名單內");

            var dragged = false;
            yield return _DriveUntil(
                () => _Node(victim) == ActionType.UnarmedGrabWalkB &&
                      (grabber.Player.SamplePositionNow() - start).magnitude > 0.8f,
                5f, d => dragged = d);
            Assert.IsTrue(dragged, $"拖行應位移且被抓者進 UnarmedGrabWalkB(victim={_Node(victim)})");

            // 拖行中被抓者的移動/停止都不可用(白名單只剩掙脫;Stop = Play(Next) 同樣被拒)
            Assert.IsFalse(_Play(victim, ActionType.UnarmedGrabWalkB), "被抓者自行走路應被拒收");
            Assert.IsFalse(_Play(victim, ActionType.UnarmedGrabIdleB), "被抓者 Stop(Play(Next))應被拒收");

            // 拖行途中貼錨:被抓者 = 抓取者位置 + 拖行方向 × 錨距
            var grabberPos = grabber.Player.SamplePositionNow();
            var victimPos = victim.Player.SamplePositionNow();
            var expected = grabberPos + dragDirection * AnchorDistance;
            Assert.Less((victimPos - expected).magnitude, 0.15f,
                $"拖行中被抓者應貼錨(actual={victimPos}, expected={expected})");

            // 續拖超過一個循環週期(0.6s×2):被抓者自身的 wrap park 必須被同幀校正蓋回,不得殘留脫錨
            yield return _DriveUntil(
                () => (grabber.Player.SamplePositionNow() - start).magnitude > 2.6f,
                5f, d => dragged = d);
            Assert.IsTrue(dragged, "應持續拖行超過兩個循環週期");
            grabberPos = grabber.Player.SamplePositionNow();
            victimPos = victim.Player.SamplePositionNow();
            expected = grabberPos + dragDirection * AnchorDistance;
            Assert.Less((victimPos - expected).magnitude, 0.15f,
                $"跨循環 wrap 後被抓者仍應貼錨(actual={victimPos}, expected={expected})");

            // 停止拖行:回到 Idle 配對,錨點停住
            Assert.IsTrue(_Play(grabber, ActionType.UnarmedGrabIdleA), "停止拖行應在 UnarmedGrabWalkA 白名單內");
            var settled = false;
            yield return _DriveUntil(
                () => _Node(grabber) == ActionType.UnarmedGrabIdleA && _Node(victim) == ActionType.UnarmedGrabIdleB,
                5f, d => settled = d);
            Assert.IsTrue(settled, $"停止後應回到 Idle 配對(grabber={_Node(grabber)}, victim={_Node(victim)})");
        }

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator UnarmedGrabBreakFreesBothSides()
        {
            var grabber = _Enter("Grabber", out _);
            var victim = _Enter("Victim", out _);
            yield return _EstablishGrab(grabber, victim);

            Assert.IsTrue(_Play(victim, ActionType.UnarmedGrabBreakB), "掙脫應在被抓白名單內");

            // 雙方進 Break 成對動作
            var breaking = false;
            yield return _DriveUntil(
                () => _Node(grabber) == ActionType.UnarmedGrabBreakA && _Node(victim) == ActionType.UnarmedGrabBreakB,
                5f, d => breaking = d);
            Assert.IsTrue(breaking, $"掙脫應帶動雙方 Break(grabber={_Node(grabber)}, victim={_Node(victim)})");

            // Break 播完各自回 UnarmedIdle;被抓者恢復自由(掙脫鍵已不在白名單)
            var freed = false;
            yield return _DriveUntil(
                () => _Node(grabber) == ActionType.UnarmedIdle && _Node(victim) == ActionType.UnarmedIdle,
                5f, d => freed = d);
            Assert.IsTrue(freed, $"Break 播完雙方應回 UnarmedIdle(grabber={_Node(grabber)}, victim={_Node(victim)})");
            Assert.IsFalse(_Play(victim, ActionType.UnarmedGrabBreakB), "解體後掙脫應不在白名單");
        }

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator UnarmedGrabThrowLaunchesVictimByBakedMotion()
        {
            var grabber = _Enter("Grabber", out _);
            var victim = _Enter("Victim", out _);
            yield return _EstablishGrab(grabber, victim);

            var victimStart = victim.Player.SamplePositionNow();
            Assert.IsTrue(_Play(grabber, ActionType.UnarmedGrabThrowA), "丟投應在 UnarmedGrabIdleA 白名單內");

            var thrown = false;
            yield return _DriveUntil(() => _Node(victim) == ActionType.UnarmedGrabThrowB, 5f, d => thrown = d);
            Assert.IsTrue(thrown, $"被抓者應被強制進 UnarmedGrabThrowB(node={_Node(victim)})");

            // 播完雙方回 UnarmedIdle;被抓者位移 = ThrowB 烘焙段(局部 -y 2.4m,基底 = 被抓者朝向(0,-1)→ 世界 +y)
            var done = false;
            yield return _DriveUntil(
                () => _Node(grabber) == ActionType.UnarmedIdle && _Node(victim) == ActionType.UnarmedIdle,
                5f, d => done = d);
            Assert.IsTrue(done, $"丟投播完雙方應回 UnarmedIdle(grabber={_Node(grabber)}, victim={_Node(victim)})");

            var victimEnd = victim.Player.SamplePositionNow();
            var expectedEnd = victimStart + new Vector2(0f, 2.4f);
            Assert.Less((victimEnd - expectedEnd).magnitude, 0.15f,
                $"被抓者應按 ThrowB 烘焙位移飛行(actual={victimEnd}, expected={expectedEnd})");

            // 解體確認:被抓者已自由(掙脫不在白名單),抓取者停在原地未被跟隨
            Assert.IsFalse(_Play(victim, ActionType.UnarmedGrabBreakB), "丟投後掙脫應不在白名單");
        }

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator GrabAtkPlaysVictimReactionAndKeepsPair()
        {
            var grabber = _Enter("Grabber", out _);
            var victim = _Enter("Victim", out _);
            yield return _EstablishGrab(grabber, victim);

            Assert.IsTrue(_Play(grabber, ActionType.UnarmedGrabAtk1A), "抓取中補打應在 UnarmedGrabIdleA 白名單內");

            // 補打命中 → Damage 路由到 UnarmedGrabAtk1B(不經鏡射)
            var reacted = false;
            yield return _DriveUntil(() => _Node(victim) == ActionType.UnarmedGrabAtk1B, 5f, d => reacted = d);
            Assert.IsTrue(reacted, $"被抓者應進受創反應 UnarmedGrabAtk1B(node={_Node(victim)})");

            // 雙方各自播完自然回 Idle 配對,配對不散
            var settled = false;
            yield return _DriveUntil(
                () => _Node(grabber) == ActionType.UnarmedGrabIdleA && _Node(victim) == ActionType.UnarmedGrabIdleB,
                5f, d => settled = d);
            Assert.IsTrue(settled, $"補打結束應回到 Idle 配對(grabber={_Node(grabber)}, victim={_Node(victim)})");

            // 仍被抓:錨點沒散、白名單仍鎖(走路拒收)
            var grabberPos = grabber.Player.SamplePositionNow();
            var victimPos = victim.Player.SamplePositionNow();
            var expected = grabberPos + new Vector2(0f, AnchorDistance);
            Assert.Less((victimPos - expected).magnitude, 0.15f,
                $"補打後被抓者仍應貼錨(actual={victimPos}, expected={expected})");
            Assert.IsFalse(_Play(victim, ActionType.UnarmedWalk), "補打後被抓者移動仍應被拒收");
        }
    }
}
