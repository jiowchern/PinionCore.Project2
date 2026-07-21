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
    /// Play(UnarmedIdle) 進戰鬥 → Play(UnarmedAttack) → IActor.ActionEvent 廣播 UnarmedAttack
    /// → 攻擊中 Transition.Playables 為空(無法移動)→ 殼跟著分段 MoveInfo 位移
    /// → ActionEvent None(結束)→ 自動回 UnarmedIdle → 殼停在位移後位置、Play(UnarmedWalk) 恢復可用。
    /// 攻擊位移來自 Configs/ActionConfigs/UnarmedAttackAction.asset(烘焙自 AttackDash clip),
    /// 期望值由資產推導,烘焙改寫段資料不需改測試。
    /// </summary>
    public class ActorAttackTests
    {
        StandaloneSceneLoader _Scenes;
        PinionCore.NetSync.Standalone.Connector _Connector;
        PinionCore.NetSync.QueryerHost _Client;
        GameObject _BotObject;   // 命中測試的靶:headless bot client(不掛 BotsMove,站在原地)
        bool _PreviousRunInBackground;

        // 攻擊總位移從 UnarmedAttackAction.asset 推導(烘焙會改寫段資料,測試不寫死數值);
        // 基底旋轉不改長度,總位移 = Σ LocalOffset 的模長
        static float _DashDistance()
        {
#if UNITY_EDITOR
            var config = UnityEditor.AssetDatabase.LoadAssetAtPath<ActionConfig>(
                "Assets/Project/Configs/ActionConfigs/UnarmedAttackAction.asset");
            Assert.NotNull(config, "應存在 UnarmedAttackAction.asset");
            Assert.Greater(config.Segments.Length, 0, "UnarmedAttackAction 應有分段資料(先跑 PinionCore/Bake Action Motions)");
            var sum = Vector2.zero;
            foreach (var segment in config.Segments)
                sum += segment.LocalOffset;
            Assert.Greater(sum.magnitude, 0.1f, "UnarmedAttackAction 總位移過小,位移斷言無意義");
            return sum.magnitude;
#else
            Assert.Fail("此測試僅支援編輯器(需讀取 UnarmedAttackAction.asset)");
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

            if (_BotObject != null)
            {
                var botConnector = _BotObject.GetComponent<PinionCore.NetSync.Standalone.Connector>();
                if (botConnector != null && botConnector.IsConnect())
                    botConnector.Disconnect();
                Object.Destroy(_BotObject);
                _BotObject = null;
            }

            yield return null;

            yield return _Scenes.UnloadAll();
            _Scenes.Dispose();
            _Scenes = null;

            Application.runInBackground = _PreviousRunInBackground;
        }

        // 等待伺服器控制狀態轉移到指定 Current 動作(soul 恆存,轉移經 TransitionEvent 廣播;
        // 重訂閱即回放當前 Transition,晚訂閱安全);onAttempt 重送不在白名單只回 false,無害
        ObservableYieldInstruction<Transition> _PlayAndWait(IControllable sender, ActionType play, ActionType expectedCurrent)
        {
            return TestWait.FirstWithRetry(
                () => TestWait.TransitionEvents(sender)
                    .Where(t => t.Current.Action == expectedCurrent),
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

            // 進戰鬥(冒險系狀態白名單沒有 UnarmedAttack,攻擊無從觸發)
            var battleIdle = _PlayAndWait(_ControllableGhost, ActionType.UnarmedIdle, ActionType.UnarmedIdle);
            yield return battleIdle;
            TestWait.AssertDone(battleIdle, "Play(UnarmedIdle) 後控制狀態應切到 Current==UnarmedIdle");

            // 出招前記下殼的位置(攻擊位移的比較基準)
            var startPosition = _Shell.Target.position;
            var dashDistance = _DashDistance();

            // 攻擊:等 ActionEvent 廣播 UnarmedAttack = 伺服器已受理
            var attackEvent = TestWait.FirstWithRetry(
                () => TestWait.ActionEvents(_ActorGhost).Where(a => a.Action == ActionType.UnarmedAttack),
                onAttempt: () => _ControllableGhost.Play(ActionType.UnarmedAttack, Vector2.zero).RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return attackEvent;
            TestWait.AssertDone(attackEvent, "Play(UnarmedAttack) 後 ActionEvent 應廣播 UnarmedAttack");
            var attackStartTicks = attackEvent.Result.StartTicks;

            // 攻擊中無法移動(核心不變量):攻擊態 Transition 的白名單為空、自然結束去向為 UnarmedIdle
            var attackTransition = TestWait.First(
                TestWait.TransitionEvents(_ControllableGhost),
                t => t.Current.Action == ActionType.UnarmedAttack,
                System.TimeSpan.FromSeconds(10));
            yield return attackTransition;
            TestWait.AssertDone(attackTransition, "攻擊中控制狀態應為 Current==UnarmedAttack");
            Assert.AreEqual(0, attackTransition.Result.Playables.Length, "攻擊中 Playables 應為空(無法移動/再出招)");
            Assert.AreEqual(ActionType.UnarmedIdle, attackTransition.Result.Next.Action, "攻擊自然結束應回 UnarmedIdle");

            // 行為驗證:攻擊中送出的 Play(UnarmedWalk) 回應必為 false
            //(回應時機與攻擊結束存在競態,不等待、改事後斷言)
            bool? walkDuringAttack = null;
            _ControllableGhost.Play(ActionType.UnarmedWalk, new Vector2(1f, 0f)).RemoteValue()
                .Subscribe(result => walkDuringAttack = result);

            // 殼跟著分段 MoveInfo 位移:超過半個前衝距離即算開始位移
            var displaced = TestWait.Until(
                () => Vector3.Distance(_Shell.Target.position, startPosition) > dashDistance * 0.5f,
                System.TimeSpan.FromSeconds(10));
            yield return displaced;
            TestWait.AssertDone(displaced, "攻擊期間殼應跟著伺服器分段 MoveInfo 位移");

            // 動作結束:不再廣播 None,攻擊播完直接接下一狀態的 UnarmedIdle
            //(StartTicks 晚於攻擊開始 = 不是訂閱 replay 撿到的舊事件)
            var idleEvent = TestWait.First(
                TestWait.ActionEvents(_ActorGhost),
                a => a.Action == ActionType.UnarmedIdle && a.StartTicks > attackStartTicks,
                System.TimeSpan.FromSeconds(10));
            yield return idleEvent;
            TestWait.AssertDone(idleEvent, "攻擊播完應直接廣播下一狀態的 UnarmedIdle");

            Assert.AreNotEqual(true, walkDuringAttack, "攻擊中 Play(UnarmedWalk) 不得被接受");

            // 終點:殼收斂到「起點 + 總位移」附近(等殼把終停 MoveInfo 取樣完);
            // 位移折線的向量和模長 ≤ 直線距離和,容差抓總位移的 15% 起跳
            var settleTolerance = Mathf.Max(0.3f, dashDistance * 0.15f);
            var settled = TestWait.Until(
                () => Mathf.Abs(Vector3.Distance(_Shell.Target.position, startPosition) - dashDistance) < settleTolerance,
                System.TimeSpan.FromSeconds(10));
            yield return settled;
            TestWait.AssertDone(settled, "動作結束後殼應停在前衝距離附近");

            // 攻擊播完自動回 UnarmedIdle:轉移抵達後移動恢復可用,
            // Play(UnarmedWalk) 被接受會廣播 Speed > 0 的 MoveEvent
            var idleTransition = TestWait.First(
                TestWait.TransitionEvents(_ControllableGhost),
                t => t.Current.Action == ActionType.UnarmedIdle,
                System.TimeSpan.FromSeconds(10));
            yield return idleTransition;
            TestWait.AssertDone(idleTransition, "攻擊結束後控制狀態應自動回 Current==UnarmedIdle");

            var moveResumed = TestWait.FirstWithRetry(
                () => TestWait.MoveEvents(_ActorGhost).Where(m => m.Speed > 0f && m.StartTicks > attackStartTicks),
                onAttempt: () => _ControllableGhost.Play(ActionType.UnarmedWalk, new Vector2(1f, 0f)).RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return moveResumed;
            TestWait.AssertDone(moveResumed, "動作結束後 Play(UnarmedWalk) 應恢復可用並廣播移動");
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
            var battleIdle = _PlayAndWait(_ControllableGhost, ActionType.UnarmedIdle, ActionType.UnarmedIdle);
            yield return battleIdle;
            TestWait.AssertDone(battleIdle, "Play(UnarmedIdle) 後控制狀態應切到 Current==UnarmedIdle");

            var attackEvent = TestWait.FirstWithRetry(
                () => TestWait.ActionEvents(_ActorGhost).Where(a => a.Action == ActionType.UnarmedAttack),
                onAttempt: () => _ControllableGhost.Play(ActionType.UnarmedAttack, Vector2.zero).RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return attackEvent;
            TestWait.AssertDone(attackEvent, "unitychan 出招應被受理(ActorConfig2 需掛 UnarmedAttackAction)");

            var idleEvent = TestWait.First(
                TestWait.ActionEvents(_ActorGhost),
                a => a.Action == ActionType.UnarmedIdle && a.StartTicks > attackEvent.Result.StartTicks,
                System.TimeSpan.FromSeconds(10));
            yield return idleEvent;
            TestWait.AssertDone(idleEvent, "攻擊播完應直接廣播下一狀態的 UnarmedIdle");

            // 模型子物件必須貼著殼:root motion 不得疊進 localPosition(XZ)
            var local = animator.transform.localPosition;
            Assert.Less(new Vector2(local.x, local.z).magnitude, 0.05f,
                $"動作結束後模型不得漂離殼(localPosition={local})");
        }

        /// <summary>
        /// 命中端到端(全鏈路):A 出招 → 伺服器 HitResolver 以 UnarmedAttackAction 的
        /// HitSegments 判定命中站在原地的 bot → 對 bot 呼叫 ICharacter.Damage() →
        /// 硬直動作 StartAction → ActionInfo 廣播 → A 的 client 從 bot 的 IActor ghost
        /// 收到 AdventureDamage(client 端零新碼)。
        /// bot 是複製連線物件的 headless client(不掛 BotsMove,站在出生點當靶)。
        /// </summary>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator AttackHitPlaysDamageOnTargetTest()
        {
            yield return _EnterWorld("HitAttacker");

            // 生 bot:複製連線物件、掛 Bot 元件自行連線+Verify(名字 Bot_N)。
            // Bot 吃的是 Client.QueryerHost wrapper(場景複製體上只有底層 host),
            // 先取底層 host 再補掛 wrapper 指向它
            _BotObject = Object.Instantiate(_Client.gameObject);
            _BotObject.name = "TestBotClient";
            var botHost = _BotObject.GetComponent<PinionCore.NetSync.QueryerHost>();
            var botWrapper = _BotObject.AddComponent<PinionCore.Project2.Client.QueryerHost>();
            botWrapper.Host = botHost;
            var bot = _BotObject.AddComponent<PinionCore.Project2.Client.Bots.Bot>();
            bot.QueryerHost = botWrapper;
            bot.Connection = PinionCore.Project2.Client.Bots.Bot.ConnectionMode.Standalone;
            bot.ModelType = ModelType.Cube;

            // 等 bot 進入世界:A 的視野收到第二個 actor(名字 Bot_ 開頭)
            System.Guid selfId = _PlayerGhost.ActorId;
            var botSupply = TestWait.First(
                _PlayerGhost.Actors.SupplyEvent(),
                a => a.ActorId.Value != selfId && a.DisplayName.Value != null && a.DisplayName.Value.StartsWith("Bot_"),
                System.TimeSpan.FromSeconds(30));
            yield return botSupply;
            TestWait.AssertDone(botSupply, "bot 應進入世界並出現在 A 的視野");
            var botGhost = botSupply.Result;

            // 攻擊是前衝突進、命中窗在前衝段:同點出招時目標會落在攻擊者正後方(扇形不中)。
            // 比照實戰:先走開拉出距離,再以 Play 的 direction 指定攻擊朝向回身衝向 bot。
            var battleIdle = _PlayAndWait(_ControllableGhost, ActionType.UnarmedIdle, ActionType.UnarmedIdle);
            yield return battleIdle;
            TestWait.AssertDone(battleIdle, "Play(UnarmedIdle) 後控制狀態應切到 Current==UnarmedIdle");

            var standPosition = _Shell.Target.position;
            var walked = TestWait.FirstWithRetry(
                () => TestWait.TransitionEvents(_ControllableGhost).Where(t => t.Current.Action == ActionType.UnarmedWalk),
                onAttempt: () => _ControllableGhost.Play(ActionType.UnarmedWalk, new Vector2(0f, 1f)).RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return walked;
            TestWait.AssertDone(walked, "Play(UnarmedWalk) 應被接受(拉開與 bot 的距離)");

            var separated = TestWait.Until(
                () => Vector3.Distance(_Shell.Target.position, standPosition) > 1.0f,
                System.TimeSpan.FromSeconds(10));
            yield return separated;
            TestWait.AssertDone(separated, "攻擊者應走離出生點 1m 以上");

            var stopped = _PlayAndWait(_ControllableGhost, ActionType.UnarmedIdle, ActionType.UnarmedIdle);
            yield return stopped;
            TestWait.AssertDone(stopped, "走開後 Play(UnarmedIdle) 應停下");

            // 回身出招:direction 是動作朝向基底,前衝與扇形都朝 -Z(bot 方向)
            var attackEvent = TestWait.FirstWithRetry(
                () => TestWait.ActionEvents(_ActorGhost).Where(a => a.Action == ActionType.UnarmedAttack),
                onAttempt: () => _ControllableGhost.Play(ActionType.UnarmedAttack, new Vector2(0f, -1f)).RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return attackEvent;
            TestWait.AssertDone(attackEvent, "Play(UnarmedAttack) 後 ActionEvent 應廣播 UnarmedAttack");
            var attackStartTicks = attackEvent.Result.StartTicks;

            // 全鏈路驗證:bot ghost 廣播 AdventureDamage(StartTicks 晚於攻擊開始 = 非訂閱 replay)
            var damaged = TestWait.First(
                TestWait.ActionEvents(botGhost),
                a => a.Action == ActionType.AdventureDamage && a.StartTicks > attackStartTicks,
                System.TimeSpan.FromSeconds(15));
            yield return damaged;
            TestWait.AssertDone(damaged, "命中後 bot 的 IActor ghost 應廣播 AdventureDamage(受擊硬直)");
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
