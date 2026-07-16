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
    /// 進戰鬥 → IBattle.Attack → IActor.ActionEvent 廣播 Attack → 殼跟著分段 MoveInfo 位移
    /// → ActionEvent None(結束)→ 殼停在位移後位置、Move 恢復可用。
    /// 攻擊位移來自 Configs/ActionConfigs/AttackAction.asset(烘焙自 AttackDash clip),
    /// 期望值由資產推導,烘焙改寫段資料不需改測試。
    /// </summary>
    public class ActorAttackTests
    {
        StandaloneSceneLoader _Scenes;
        PinionCore.NetSync.Standalone.Connector _Connector;
        PinionCore.NetSync.QueryerHost _Client;
        bool _PreviousRunInBackground;

        // 攻擊總位移從 AttackAction.asset 推導(烘焙會改寫段資料,測試不寫死數值);
        // 基底旋轉不改長度,總位移 = Σ LocalOffset 的模長
        static float _DashDistance()
        {
#if UNITY_EDITOR
            var config = UnityEditor.AssetDatabase.LoadAssetAtPath<ActionConfig>(
                "Assets/Project/Configs/ActionConfigs/AttackAction.asset");
            Assert.NotNull(config, "應存在 AttackAction.asset");
            Assert.Greater(config.Segments.Length, 0, "AttackAction 應有分段資料(先跑 PinionCore/Bake Action Motions)");
            var sum = Vector2.zero;
            foreach (var segment in config.Segments)
                sum += segment.LocalOffset;
            Assert.Greater(sum.magnitude, 0.1f, "AttackAction 總位移過小,位移斷言無意義");
            return sum.magnitude;
#else
            Assert.Fail("此測試僅支援編輯器(需讀取 AttackAction.asset)");
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

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator AttackDisplacementTest()
        {
            yield return _EnterWorld("AttackTester");

            // 初始動作狀態:訂閱即 replay None
            var actionReplay = TestWait.First(TestWait.ActionEvents(_ActorGhost), System.TimeSpan.FromSeconds(10));
            yield return actionReplay;
            TestWait.AssertDone(actionReplay, "ActionEvent 訂閱後應 replay 當前狀態");
            Assert.AreEqual(ActionType.None, actionReplay.Result.Action, "進場初始應無動作進行中");

            // 進戰鬥(冒險態沒有 IBattle,攻擊無從觸發);IAdventure/IBattle 由 IPlayer 供應(world 端子狀態互斥開關)
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

            // 出招前記下殼的位置(攻擊位移的比較基準)
            var startPosition = _Shell.Target.position;
            var dashDistance = _DashDistance();

            // 攻擊:RPC 可能與 soul 綁定競態掉失,逾時重訂閱+重送;
            // 重送打在進行中的動作上只會回 false,無害。等 ActionEvent 廣播 Attack = 伺服器已受理
            var battle = battleSupply.Result;
            var attackEvent = TestWait.FirstWithRetry(
                () => TestWait.ActionEvents(_ActorGhost).Where(a => a.Action == ActionType.Attack),
                onAttempt: () => battle.Attack(ActionType.Attack).RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return attackEvent;
            TestWait.AssertDone(attackEvent, "Attack 後 ActionEvent 應廣播 Attack");
            var attackStartTicks = attackEvent.Result.StartTicks;

            // 殼跟著分段 MoveInfo 位移:超過半個前衝距離即算開始位移
            var displaced = TestWait.Until(
                () => Vector3.Distance(_Shell.Target.position, startPosition) > dashDistance * 0.5f,
                System.TimeSpan.FromSeconds(10));
            yield return displaced;
            TestWait.AssertDone(displaced, "攻擊期間殼應跟著伺服器分段 MoveInfo 位移");

            // 動作結束:None 且 StartTicks 晚於攻擊開始(不是訂閱 replay 撿到的舊 None)
            var noneEvent = TestWait.First(
                TestWait.ActionEvents(_ActorGhost),
                a => a.Action == ActionType.None && a.StartTicks > attackStartTicks,
                System.TimeSpan.FromSeconds(10));
            yield return noneEvent;
            TestWait.AssertDone(noneEvent, "動作應以 ActionEvent None 結束");

            // 終點:殼收斂到「起點 + 總位移」附近(等殼把終停 MoveInfo 取樣完);
            // 位移折線的向量和模長 ≤ 直線距離和,容差抓總位移的 15% 起跳
            var settleTolerance = Mathf.Max(0.3f, dashDistance * 0.15f);
            var settled = TestWait.Until(
                () => Mathf.Abs(Vector3.Distance(_Shell.Target.position, startPosition) - dashDistance) < settleTolerance,
                System.TimeSpan.FromSeconds(10));
            yield return settled;
            TestWait.AssertDone(settled, "動作結束後殼應停在前衝距離附近");

            // 動作結束後移動恢復可用:Move 被接受會廣播 Speed > 0 的 MoveEvent
            //(IMoveable 由 IPlayer.Moveable 供應,world 端狀態機控制開關)
            var moveableSupply = TestWait.First(
                _PlayerGhost.Moveable.SupplyEvent(),
                System.TimeSpan.FromSeconds(10));
            yield return moveableSupply;
            TestWait.AssertDone(moveableSupply, "應供應 IMoveable");

            var moveResumed = TestWait.FirstWithRetry(
                () => TestWait.MoveEvents(_ActorGhost).Where(m => m.Speed > 0f && m.StartTicks > attackStartTicks),
                onAttempt: () => moveableSupply.Result.Move(new Vector2(1f, 0f)).RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return moveResumed;
            TestWait.AssertDone(moveResumed, "動作結束後 Move 應恢復可用並廣播移動");
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

            // 進戰鬥 → 出招 → 等動作結束(IAdventure/IBattle 由 IPlayer 供應)
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

            var battle = battleSupply.Result;
            var attackEvent = TestWait.FirstWithRetry(
                () => TestWait.ActionEvents(_ActorGhost).Where(a => a.Action == ActionType.Attack),
                onAttempt: () => battle.Attack(ActionType.Attack).RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return attackEvent;
            TestWait.AssertDone(attackEvent, "unitychan 出招應被受理(ActorConfig2 需掛 AttackAction)");

            var noneEvent = TestWait.First(
                TestWait.ActionEvents(_ActorGhost),
                a => a.Action == ActionType.None && a.StartTicks > attackEvent.Result.StartTicks,
                System.TimeSpan.FromSeconds(10));
            yield return noneEvent;
            TestWait.AssertDone(noneEvent, "動作應以 ActionEvent None 結束");

            // 模型子物件必須貼著殼:root motion 不得疊進 localPosition(XZ)
            var local = animator.transform.localPosition;
            Assert.Less(new Vector2(local.x, local.z).magnitude, 0.05f,
                $"動作結束後模型不得漂離殼(localPosition={local})");
        }

        IPlayer _PlayerGhost;
        IActor _ActorGhost;
        PinionCore.Project2.Client.ActorShell _Shell;

        // 統一入口:entry.Games 合約鏈(能力介面 IMoveable/IAdventure/IBattle 均由 IPlayer 供應)
        System.IObservable<IGame> _Games()
        {
            return _Client.Queryer.QueryNotifier<IUserEntry>().SupplyEvent()
                .SelectMany(entry => entry.Games.SupplyEvent());
        }

        // 共用進場流程:Verify → 取得 IPlayer / IActor ghost → 等 ActorProvider 建出對應殼
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
