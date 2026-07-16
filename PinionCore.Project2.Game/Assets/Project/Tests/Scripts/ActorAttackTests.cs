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
    /// 自帶位移攻擊動作端到端測試(比照 ActorStanceTests 的四場景 Standalone 流程):
    /// Play(BattleIdle) 進戰鬥 → Play(BattleAttack) → IActor.ActionEvent 廣播 BattleAttack
    /// → 攻擊中 Transition.Playables 為空(無法移動)→ 殼跟著分段 MoveInfo 位移
    /// → ActionEvent None(結束)→ 自動回 BattleIdle → 殼停在位移後位置、Play(BattleWalk) 恢復可用。
    /// 攻擊位移來自 Configs/ActionConfigs/BattleAttackAction.asset(烘焙自 AttackDash clip),
    /// 期望值由資產推導,烘焙改寫段資料不需改測試。
    /// </summary>
    public class ActorAttackTests
    {
        StandaloneSceneLoader _Scenes;
        PinionCore.NetSync.Standalone.Connector _Connector;
        PinionCore.NetSync.QueryerHost _Client;
        bool _PreviousRunInBackground;

        // 攻擊總位移從 BattleAttackAction.asset 推導(烘焙會改寫段資料,測試不寫死數值);
        // 基底旋轉不改長度,總位移 = Σ LocalOffset 的模長
        static float _DashDistance()
        {
#if UNITY_EDITOR
            var config = UnityEditor.AssetDatabase.LoadAssetAtPath<ActionConfig>(
                "Assets/Project/Configs/ActionConfigs/BattleAttackAction.asset");
            Assert.NotNull(config, "應存在 BattleAttackAction.asset");
            Assert.Greater(config.Segments.Length, 0, "BattleAttackAction 應有分段資料(先跑 PinionCore/Bake Action Motions)");
            var sum = Vector2.zero;
            foreach (var segment in config.Segments)
                sum += segment.LocalOffset;
            Assert.Greater(sum.magnitude, 0.1f, "BattleAttackAction 總位移過小,位移斷言無意義");
            return sum.magnitude;
#else
            Assert.Fail("此測試僅支援編輯器(需讀取 BattleAttackAction.asset)");
            return 0f;
#endif
        }

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

        // 等待帶指定 Current 動作的控制 soul 供應(狀態轉移 = 新 soul,SupplyEvent 有 replay 晚訂閱安全);
        // onAttempt 重送打在已收回的 soul 上會被靜默丟棄,無害
        ObservableYieldInstruction<IControllable> _PlayAndWait(IControllable sender, ActionType play, ActionType expectedCurrent)
        {
            return TestWait.FirstWithRetry(
                () => _PlayerGhost.Controllable.SupplyEvent()
                    .Where(c => c.Transition.Value.Current.Action == expectedCurrent),
                onAttempt: () => sender.Play(play, Vector2.zero).RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator AttackDisplacementTest()
        {
            yield return _EnterWorld("AttackTester");

            // 初始動作狀態:idle 已升格為顯式動作,進場後廣播 AdventureIdle(訂閱 replay 即可取得)
            var actionReplay = TestWait.First(
                TestWait.ActionEvents(_ActorGhost), a => a.Action == ActionType.AdventureIdle,
                System.TimeSpan.FromSeconds(10));
            yield return actionReplay;
            TestWait.AssertDone(actionReplay, "進場後 ActionEvent 應廣播 AdventureIdle(idle 即動作)");

            // 進戰鬥(冒險系狀態白名單沒有 BattleAttack,攻擊無從觸發)
            var battleIdle = _PlayAndWait(_ControllableGhost, ActionType.BattleIdle, ActionType.BattleIdle);
            yield return battleIdle;
            TestWait.AssertDone(battleIdle, "Play(BattleIdle) 後應供應 Current==BattleIdle 的新 soul");

            // 出招前記下殼的位置(攻擊位移的比較基準)
            var startPosition = _Shell.Target.position;
            var dashDistance = _DashDistance();

            // 攻擊:等 ActionEvent 廣播 BattleAttack = 伺服器已受理
            var attackEvent = TestWait.FirstWithRetry(
                () => TestWait.ActionEvents(_ActorGhost).Where(a => a.Action == ActionType.BattleAttack),
                onAttempt: () => battleIdle.Result.Play(ActionType.BattleAttack, Vector2.zero).RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return attackEvent;
            TestWait.AssertDone(attackEvent, "Play(BattleAttack) 後 ActionEvent 應廣播 BattleAttack");
            var attackStartTicks = attackEvent.Result.StartTicks;

            // 攻擊中無法移動(核心不變量):攻擊狀態 soul 的白名單為空、自然結束去向為 BattleIdle
            var attackSoul = TestWait.First(
                _PlayerGhost.Controllable.SupplyEvent()
                    .Where(c => c.Transition.Value.Current.Action == ActionType.BattleAttack),
                System.TimeSpan.FromSeconds(10));
            yield return attackSoul;
            TestWait.AssertDone(attackSoul, "攻擊中應供應 Current==BattleAttack 的控制 soul");
            Assert.AreEqual(0, attackSoul.Result.Transition.Value.Playables.Length, "攻擊中 Playables 應為空(無法移動/再出招)");
            Assert.AreEqual(ActionType.BattleIdle, attackSoul.Result.Transition.Value.Next.Action, "攻擊自然結束應回 BattleIdle");

            // 行為驗證:攻擊 soul 上的 Play(BattleWalk) 若有回應必為 false
            //(soul 已收回時 RPC 被靜默丟棄無回應,不能等待回應,改事後斷言)
            bool? walkDuringAttack = null;
            attackSoul.Result.Play(ActionType.BattleWalk, new Vector2(1f, 0f)).RemoteValue()
                .Subscribe(result => walkDuringAttack = result);

            // 殼跟著分段 MoveInfo 位移:超過半個前衝距離即算開始位移
            var displaced = TestWait.Until(
                () => Vector3.Distance(_Shell.Target.position, startPosition) > dashDistance * 0.5f,
                System.TimeSpan.FromSeconds(10));
            yield return displaced;
            TestWait.AssertDone(displaced, "攻擊期間殼應跟著伺服器分段 MoveInfo 位移");

            // 動作結束:不再廣播 None,攻擊播完直接接下一狀態的 BattleIdle
            //(StartTicks 晚於攻擊開始 = 不是訂閱 replay 撿到的舊事件)
            var idleEvent = TestWait.First(
                TestWait.ActionEvents(_ActorGhost),
                a => a.Action == ActionType.BattleIdle && a.StartTicks > attackStartTicks,
                System.TimeSpan.FromSeconds(10));
            yield return idleEvent;
            TestWait.AssertDone(idleEvent, "攻擊播完應直接廣播下一狀態的 BattleIdle");

            Assert.AreNotEqual(true, walkDuringAttack, "攻擊中 Play(BattleWalk) 不得被接受");

            // 終點:殼收斂到「起點 + 總位移」附近(等殼把終停 MoveInfo 取樣完);
            // 位移折線的向量和模長 ≤ 直線距離和,容差抓總位移的 15% 起跳
            var settleTolerance = Mathf.Max(0.3f, dashDistance * 0.15f);
            var settled = TestWait.Until(
                () => Mathf.Abs(Vector3.Distance(_Shell.Target.position, startPosition) - dashDistance) < settleTolerance,
                System.TimeSpan.FromSeconds(10));
            yield return settled;
            TestWait.AssertDone(settled, "動作結束後殼應停在前衝距離附近");

            // 攻擊播完自動回 BattleIdle:新 soul 供應後移動恢復可用,
            // Play(BattleWalk) 被接受會廣播 Speed > 0 的 MoveEvent
            var idleResupply = TestWait.First(
                _PlayerGhost.Controllable.SupplyEvent()
                    .Where(c => c.Transition.Value.Current.Action == ActionType.BattleIdle),
                System.TimeSpan.FromSeconds(10));
            yield return idleResupply;
            TestWait.AssertDone(idleResupply, "攻擊結束後應自動供應 Current==BattleIdle 的新 soul");

            var moveResumed = TestWait.FirstWithRetry(
                () => TestWait.MoveEvents(_ActorGhost).Where(m => m.Speed > 0f && m.StartTicks > attackStartTicks),
                onAttempt: () => idleResupply.Result.Play(ActionType.BattleWalk, new Vector2(1f, 0f)).RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return moveResumed;
            TestWait.AssertDone(moveResumed, "動作結束後 Play(BattleWalk) 應恢復可用並廣播移動");
        }

        /// <summary>
        /// 模型不得漂離殼(unitychan 回歸):FBX 匯入的 Animator 預設 applyRootMotion = true,
        /// 若 client 不強制關閉,帶位移的 clip 會把 root motion 疊進模型子物件的 localPosition —
        /// 伺服器推殼一次、模型自己再走一次,動作結束後永久錯位。
        /// </summary>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator AttackModelStaysOnShellTest()
        {
            yield return _EnterWorld("AttackTesterChan", ModelType.Unitychan);

            // 等模型(Addressables)載入完成:Animator 掛上即可斷言 root motion 已被關閉
            var modelLoaded = TestWait.Until(
                () => _Shell.Target.GetComponentInChildren<Animator>() != null,
                System.TimeSpan.FromSeconds(30));
            yield return modelLoaded;
            TestWait.AssertDone(modelLoaded, "unitychan 模型應載入完成");
            var animator = _Shell.Target.GetComponentInChildren<Animator>();
            Assert.IsFalse(animator.applyRootMotion, "client 模型不得啟用 root motion(位置權威在伺服器)");

            // 進戰鬥 → 出招 → 等動作結束
            var battleIdle = _PlayAndWait(_ControllableGhost, ActionType.BattleIdle, ActionType.BattleIdle);
            yield return battleIdle;
            TestWait.AssertDone(battleIdle, "Play(BattleIdle) 後應供應 Current==BattleIdle 的新 soul");

            var attackEvent = TestWait.FirstWithRetry(
                () => TestWait.ActionEvents(_ActorGhost).Where(a => a.Action == ActionType.BattleAttack),
                onAttempt: () => battleIdle.Result.Play(ActionType.BattleAttack, Vector2.zero).RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return attackEvent;
            TestWait.AssertDone(attackEvent, "unitychan 出招應被受理(ActorConfig2 需掛 BattleAttackAction)");

            var idleEvent = TestWait.First(
                TestWait.ActionEvents(_ActorGhost),
                a => a.Action == ActionType.BattleIdle && a.StartTicks > attackEvent.Result.StartTicks,
                System.TimeSpan.FromSeconds(10));
            yield return idleEvent;
            TestWait.AssertDone(idleEvent, "攻擊播完應直接廣播下一狀態的 BattleIdle");

            // 模型子物件必須貼著殼:root motion 不得疊進 localPosition(XZ)
            var local = animator.transform.localPosition;
            Assert.Less(new Vector2(local.x, local.z).magnitude, 0.05f,
                $"動作結束後模型不得漂離殼(localPosition={local})");
        }

        IPlayer _PlayerGhost;
        IControllable _ControllableGhost;
        IActor _ActorGhost;
        PinionCore.Project2.Client.ActorShell _Shell;

        // 統一入口:entry.Games 合約鏈(控制能力 IControllable 由 IPlayer.Controllable 供應)
        System.IObservable<IGame> _Games()
        {
            return _Client.Queryer.QueryNotifier<IUserEntry>().SupplyEvent()
                .SelectMany(entry => entry.Games.SupplyEvent());
        }

        // 共用進場流程:Verify → 取得 IPlayer / IControllable / IActor ghost → 等 ActorProvider 建出對應殼
        IEnumerator _EnterWorld(string playerName, ModelType modelType = ModelType.Cube)
        {
            var verifiableSupply = TestWait.First(
                _Client.Queryer.QueryNotifier<IUserEntry>().SupplyEvent()
                    .SelectMany(entry => entry.Verifiers.SupplyEvent()),
                System.TimeSpan.FromSeconds(10));
            yield return verifiableSupply;
            TestWait.AssertDone(verifiableSupply, "連線後 client 應從 User 服務收到 IVerifier");

            var verifyResult = TestWait.First(
                verifiableSupply.Result.Verify(playerName, modelType).RemoteValue(),
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
