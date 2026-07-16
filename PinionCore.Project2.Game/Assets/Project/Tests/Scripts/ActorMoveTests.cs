using System.Collections;
using NUnit.Framework;
using UniRx;                       // First/Timeout/ToYieldInstruction 等 UniRx 擴充
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using PinionCore.NetSync.UniRx;    // SupplyEvent()/RemoteValue():把 INotifier<T>/Value<T> 轉成 IObservable
using PinionCore.Project2.Shared;
using PinionCore.Project2.Shared.Users;

namespace PinionCore.Project2.Tests
{
    /// <summary>
    /// Actor 瞬轉直走移動端到端測試:
    /// 比照 ActorDisplayNameTests 的四場景 Standalone 流程,
    /// Verify 進入遊戲後取得 IActor / IPlayer,
    /// 由 Client.PlayerRemote.Move 送出世界座標 XZ 方向,
    /// World 即刻把朝向設為該方向並沿直線前進(ω 恆為 0),發回 MoveInfo,
    /// 驗證殼沿直線移動、改向連續、Move 節流(MoveAcceptInterval)、Stop 駐留。
    /// </summary>
    public class ActorMoveTests
    {
        // 僅供逾時計算的保守速度下限:實際速度由 WalkAction 烘焙的 root motion 決定(≈1.7 m/s)
        const float MoveSpeed = 1.0f;

        StandaloneSceneLoader _Scenes;
        PinionCore.NetSync.Standalone.Connector _Connector;
        PinionCore.NetSync.QueryerHost _Client;
        bool _PreviousRunInBackground;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            // 編輯器失焦時 player loop 會停住,連線流程會卡住
            _PreviousRunInBackground = Application.runInBackground;
            Application.runInBackground = true;

            _Scenes = new StandaloneSceneLoader();

            // 先起 Gateway 與 World 的 Listener,
            // 再載 User 讓 GatewayService / WorldAgent 的 AutoConnector(已覆寫為 Standalone)連上
            yield return _Scenes.Load("Gateway");
            yield return _Scenes.Load("World");
            yield return _Scenes.Load("User");
            yield return _Scenes.Load("Client");

            // 從 QueryHost wrapper 解析目前拓撲的連線物件;Connector 與連線目標(ListenerLocator)都在其上
            // UnitySetUp 不受 [Timeout] 保護,找元件必須有界限,否則會掛死整輪
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

            // 等一個 frame:StandaloneStartToBind 綁定 Listener、User 場景的 GatewayService 註冊進 Router
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

            // 讓斷線的 session leave 流程跑完再卸場景
            yield return null;

            yield return _Scenes.UnloadAll();
            _Scenes.Dispose();
            _Scenes = null;

            Application.runInBackground = _PreviousRunInBackground;
        }

        /// <summary>
        /// 直線前進:輸入 (0,1)(世界 +Z)→ 沿 +Z 直線移動(與出生朝向相同,無轉向)。
        /// 移動沒有終點,量測方向與 MoveInfo 欄位後 Stop 收尾。
        /// </summary>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator MoveStraightTest()
        {
            yield return _EnterWorld("StraightTester");

            // 先建等待(建構即訂閱)再送指令;訂閱當下的 replay 是駐留態,被 predicate 濾掉
            var moving = TestWait.First(
                TestWait.MoveEvents(_ActorGhost), i => i.Speed > 0f, System.TimeSpan.FromSeconds(15));

            var startPos = _Shell.Target.position;
            _ClientPlayer.Move(new Vector2(0f, 1f));

            yield return moving;
            TestWait.AssertDone(moving, "ghost 應收到移動中的 MoveInfo");
            Assert.Greater(moving.Result.Speed, 0f, "移動中的 MoveInfo 速度應大於 0(走路段速度由 root motion 烘焙決定)");

            // 等殼走出約 1.5 單位
            var moved = TestWait.Until(
                () => (_Shell.Target.position - startPos).magnitude >= 1.5f,
                System.TimeSpan.FromSeconds(1.5f / MoveSpeed + 15f));
            yield return moved;
            TestWait.AssertDone(moved, "殼應已沿直線移動約 1.5 單位");

            // 位移方向 ≈ 世界 +Z
            var displacement = _Shell.Target.position - startPos;
            var directionXZ = new Vector2(displacement.x, displacement.z).normalized;
            Assert.Greater(Vector2.Dot(directionXZ, new Vector2(0f, 1f)), 0.99f, "位移方向應沿世界 +Z");

            _ClientPlayer.Stop();
        }

        /// <summary>
        /// 世界方向直走:輸入 (√2/2,√2/2)(世界右前 45°)→ 朝向瞬轉為該方向、ω==0,
        /// 沿直線前進。以共用取樣器對 ghost 收到的 MoveInfo 預測位置,
        /// 與殼實際位置比對,驗證兩端公式一致。
        /// </summary>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator MoveWorldDirectionTest()
        {
            yield return _EnterWorld("WorldDirTester");

            var startPos = _Shell.Target.position;
            var halfSqrt2 = Mathf.Sqrt(2f) / 2f;

            // 等移動中的 MoveInfo 抵達;RPC/事件註冊可能與 session/soul 綁定流程競態而掉失
            //(曾觀測到 "Soul not found" 的 Advance Error),單次嘗試逾時就重訂閱
            //(觸發 replay 取回當下狀態)並重送 Move,兩種掉失模式都能恢復
            var moving = TestWait.FirstWithRetry(
                () => TestWait.MoveEvents(_ActorGhost).Where(i => i.Speed > 0f),
                onAttempt: () => _ClientPlayer.Move(new Vector2(halfSqrt2, halfSqrt2)), // 世界右前 45°
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return moving;
            TestWait.AssertDone(moving, "ghost 應收到移動中的 MoveInfo");

            // 走路是分段 root motion,段邊界/循環 wrap 會持續發 MoveInfo(不會靜默);
            // 持久訂閱追最新一筆作為預測依據(比照 PlayerInputMoveTests 模式)
            Assert.Greater(moving.Result.Speed, 0f, "移動中的 MoveInfo 速度應大於 0(走路段速度由 root motion 烘焙決定)");
            Assert.Greater(Vector2.Dot(moving.Result.Facing.normalized, new Vector2(halfSqrt2, halfSqrt2)), 0.95f,
                "朝向應約為指令的世界方向(走路段的速度方向可能帶少量側向分量)");
            var lastMove = moving.Result;
            var moveSub = TestWait.MoveEvents(_ActorGhost).Subscribe(info => lastMove = info);

            // 殼訂閱的是 IActor ghost、測試訂閱的是 IPlayer ghost,MoveEvent 抵達幀序不同;
            // 等殼實際起步(已套用移動中的 MoveInfo)再開始量測,避免量到殼還停在出生點的空窗
            var started = TestWait.Until(
                () => (_Shell.Target.position - startPos).magnitude > 0.02f,
                System.TimeSpan.FromSeconds(15));
            yield return started;
            TestWait.AssertDone(started, "殼應已開始移動");

            // 觀察 ~2 秒:每幀以取樣器預測位置,與殼實際位置比對(違規即刻結束)。
            // 殼由 UniRx EveryUpdate 定位,它取樣所用的時鐘值必落在
            // 「上次讀 CurrentTime」與「本次讀 CurrentTime」之間(時鐘單調、殼每幀重取樣);
            // 幀長與對時往前跳(卡頓後封包補送重新錨定)都會造成合法落差,
            // 因此以兩次量測間 world time 的前進量 × 速度作為該幀可扣除的時序落差上界。
            var prevTicks = _Shell.WorldTime.CurrentTime.Ticks;
            yield return null;
            var held = TestWait.HoldFrames(() =>
            {
                var info = lastMove;
                var nowTicks = _Shell.WorldTime.CurrentTime.Ticks;
                var elapsed = (nowTicks - info.StartTicks) / (double)System.TimeSpan.TicksPerSecond;
                if (elapsed < 0)
                    elapsed = 0;
                MoveSampler.Sample(info, elapsed, out var predicted, out _);

                var actual = new Vector2(_Shell.Target.position.x, _Shell.Target.position.z);
                var slack = info.Speed * (float)((nowTicks - prevTicks) / (double)System.TimeSpan.TicksPerSecond);
                var deviation = Vector2.Distance(predicted, actual) - slack;
                prevTicks = nowTicks;
                return deviation < 0.25f;
            }, System.TimeSpan.FromSeconds(2));
            yield return held;
            Assert.IsFalse(held.Result, "殼位置應與最新 MoveInfo 取樣預測一致(已扣除兩次量測間時鐘前進量的時序落差)");
            moveSub.Dispose();

            // 位移方向 ≈ 指令的世界方向(走路每循環淨位移沿指令方向,部分循環的側向殘差很小)
            var displacement = _Shell.Target.position - startPos;
            var directionXZ = new Vector2(displacement.x, displacement.z).normalized;
            Assert.Greater(Vector2.Dot(directionXZ, new Vector2(halfSqrt2, halfSqrt2)), 0.99f, "位移方向應沿指令的世界方向");

            _ClientPlayer.Stop();
        }

        /// <summary>
        /// 改向:世界右前改左前 → 新 MoveInfo 朝向瞬轉為新方向,
        /// 且新起點與「舊 MoveInfo 取樣至新 StartTicks 的位置」連續。
        /// </summary>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator MoveRedirectTest()
        {
            yield return _EnterWorld("RedirectTester");

            var startPos = _Shell.Target.position;
            var halfSqrt2 = Mathf.Sqrt(2f) / 2f;

            // 走路是分段 root motion,改向前可能已有段邊界/wrap 事件;
            // 持久收集全部 MoveInfo,改向事件的「前一筆」才是位置連續性的取樣基準
            var received = new System.Collections.Generic.List<MoveInfo>();
            var collectSub = TestWait.MoveEvents(_ActorGhost).Subscribe(received.Add);

            // 先建等待再送指令;訂閱當下的 replay 是駐留態,被 predicate 濾掉
            var firstMove = TestWait.First(
                TestWait.MoveEvents(_ActorGhost),
                i => i.Speed > 0f && i.Facing.x > 0.5f,
                System.TimeSpan.FromSeconds(15));
            _ClientPlayer.Move(new Vector2(halfSqrt2, halfSqrt2)); // 世界右前 45°

            yield return firstMove;
            TestWait.AssertDone(firstMove, "第一段應朝世界右前方直走");

            // 等殼實際走出 0.5 單位(≈ 0.5 秒,遠超過 MoveAcceptInterval,第二發不會被節流)
            var traveled = TestWait.Until(
                () => (_Shell.Target.position - startPos).sqrMagnitude > 0.25f,
                System.TimeSpan.FromSeconds(15));
            yield return traveled;
            TestWait.AssertDone(traveled, "第一段應已走出約 0.5 單位");

            // 改向:先建等待(replay 是右前態,被 predicate 濾掉)再送指令
            var secondMove = TestWait.First(
                TestWait.MoveEvents(_ActorGhost),
                i => i.Facing.x < -0.5f,
                System.TimeSpan.FromSeconds(15));
            _ClientPlayer.Move(new Vector2(-halfSqrt2, halfSqrt2)); // 途中改世界左前 45°

            yield return secondMove;
            TestWait.AssertDone(secondMove, "改向後 ghost 應收到朝向翻轉的 MoveInfo");
            var secondInfo = secondMove.Result;
            Assert.Greater(Vector2.Dot(secondInfo.Facing.normalized, new Vector2(-halfSqrt2, halfSqrt2)), 0.95f,
                "朝向應約為新的世界方向(走路段的速度方向可能帶少量側向分量)");

            // 位置連續:改向事件的新起點 = 前一筆 MoveInfo 取樣至新 StartTicks(伺服器端純數學,容差取小)
            var idx = received.FindIndex(i => i.StartTicks == secondInfo.StartTicks && i.Facing.x < -0.5f);
            Assert.Greater(idx, 0, "收集器應包含改向事件與其前一筆");
            var prevInfo = received[idx - 1];
            var handover = (secondInfo.StartTicks - prevInfo.StartTicks) / (double)System.TimeSpan.TicksPerSecond;
            MoveSampler.Sample(prevInfo, handover, out var predicted, out _);
            Assert.Less(Vector2.Distance(predicted, secondInfo.Position), 0.01f, "改向瞬間位置應連續,不得跳點");
            collectSub.Dispose();

            _ClientPlayer.Stop();
        }

        /// <summary>
        /// 停止:移動中 Stop → ghost 收到 Speed==0 的駐留 MoveInfo,殼吸附後不再移動。
        /// </summary>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator MoveStopTest()
        {
            yield return _EnterWorld("StopTester");

            var startPos = _Shell.Target.position;
            _ClientPlayer.Move(new Vector2(0f, 1f));

            // 等殼走出約 1 單位再喊停
            var moved = TestWait.Until(
                () => (_Shell.Target.position - startPos).magnitude >= 1f,
                System.TimeSpan.FromSeconds(1f / MoveSpeed + 15f));
            yield return moved;
            TestWait.AssertDone(moved, "移動應已進行約 1 單位");

            // 先建等待再喊停:訂閱當下的 replay 是移動態,被 predicate 濾掉,
            // 只會等到 Stop 產生的駐留 MoveInfo(Speed==0)
            var stand = TestWait.First(
                TestWait.MoveEvents(_ActorGhost), i => i.Speed == 0f, System.TimeSpan.FromSeconds(10));
            _ClientPlayer.Stop();

            yield return stand;
            TestWait.AssertDone(stand, "Stop 後應收到駐留 MoveInfo(Speed==0)");
            var standInfo = stand.Result;

            // 殼的取樣位置可能落後 server(時鐘偏差),且殼的 IActor ghost 與測試訂閱的
            // IPlayer ghost 事件抵達幀序不同;駐留一到殼會先瞬移吸附到 server 停點,
            // 這段吸附不屬於「Stop 後的移動」——先等殼停到駐留點再開始量
            var snapped = TestWait.Until(() =>
            {
                var pos = _Shell.Target.position;
                return Vector2.Distance(new Vector2(pos.x, pos.z), standInfo.Position) < 0.1f;
            }, System.TimeSpan.FromSeconds(5));
            yield return snapped;
            TestWait.AssertDone(snapped, "殼應吸附到伺服器駐留點");

            // 確認殼真的停住:1 秒內位置不再變化(違規即刻結束)
            var stopPos = _Shell.Target.position;
            var held = TestWait.HoldFrames(
                () => (_Shell.Target.position - stopPos).magnitude < 0.02f,
                System.TimeSpan.FromSeconds(1));
            yield return held;
            Assert.IsFalse(held.Result, "Stop 後殼不應再移動");
            Assert.Less(Vector2.Distance(new Vector2(_Shell.Target.position.x, _Shell.Target.position.z), standInfo.Position), 0.1f,
                "殼的停止位置應與伺服器駐留點一致(容忍延遲取樣差)");
        }

        /// <summary>
        /// 重定向節流:走路中同幀連發多個 Play(AdventureWalk, 新方向)(= 重定向),
        /// 驗證節流的不變量 — 任兩筆「被接受的方向變更」(含起走)的 StartTicks 差 ≥ MoveAcceptInterval、
        /// 被拒的指令不產生朝向切換;Play(AdventureIdle)(= 停止)為狀態轉移不受間隔限制;
        /// 停止後重新起走恢復接受。
        /// (不斷言「第二發必被拒」:兩發指令在 server 端的處理時點受編輯器幀時序影響,
        /// 可能被拉開超過間隔,屆時接受是合法行為)
        /// </summary>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator MoveAcceptIntervalTest()
        {
            // 與 ActorConfig.asset 一致:asset 序列化 MoveAcceptInterval = 0.2
            const float MoveAcceptInterval = 0.2f;
            var intervalTicks = (long)(MoveAcceptInterval * System.TimeSpan.TicksPerSecond);

            yield return _EnterWorld("IntervalTester");

            // 收集器(事件驅動):訂閱當下的 replay 是駐留態(Speed==0),不會進 movingInfos
            var movingInfos = new System.Collections.Generic.List<MoveInfo>();
            var moveSub = TestWait.MoveEvents(_ActorGhost)
                .Subscribe(info =>
                {
                    if (info.Speed > 0f)
                        movingInfos.Add(info);
                });

            // 先起走(朝向 0°),等走路狀態的新 soul 供應(重定向要打在走路 soul 上)
            var walkSoul = TestWait.FirstWithRetry(
                () => _PlayerGhost.Controllable.SupplyEvent()
                    .Where(c => c.Transition.Value.Current.Action == ActionType.AdventureWalk),
                onAttempt: () => _ControllableGhost.Play(ActionType.AdventureWalk, new Vector2(0f, 1f)).RemoteValue().Subscribe(),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return walkSoul;
            TestWait.AssertDone(walkSoul, "起走後應供應 Current==AdventureWalk 的控制 soul");

            // 同一幀連發 6 個相異方向的重定向(與起走方向皆相距 ≥60°),
            // 再緊接一發 Play(AdventureIdle)(有序管道保證 server 依序處理)
            var walker = walkSoul.Result;
            var degrees = new[] { 90f, 150f, 210f, 270f, 330f, 30f };
            var results = new System.Collections.Generic.List<UniRx.ObservableYieldInstruction<bool>>();
            foreach (var deg in degrees)
            {
                var dir = new Vector2(Mathf.Sin(deg * Mathf.Deg2Rad), Mathf.Cos(deg * Mathf.Deg2Rad));
                results.Add(walker.Play(ActionType.AdventureWalk, dir).RemoteValue()
                    .First().Timeout(System.TimeSpan.FromSeconds(10)).ToYieldInstruction(throwOnError: false));
            }
            var stopResult = walker.Play(ActionType.AdventureIdle, Vector2.zero).RemoteValue()
                .First().Timeout(System.TimeSpan.FromSeconds(10)).ToYieldInstruction(throwOnError: false);

            foreach (var r in results)
                yield return r;
            yield return stopResult;

            Assert.IsFalse(stopResult.HasError, "Play(AdventureIdle) 未收到回傳值");
            Assert.IsTrue(stopResult.Result, "停止是狀態轉移,不受重定向間隔限制,緊跟在重定向後仍應被接受");

            // 等事件靜默,確保被接受指令的 MoveEvent 都已抵達
            //(柵欄自己的訂閱會收到一筆 replay,只進柵欄的流,不污染收集器)
            var fence = TestWait.Quiet(
                TestWait.MoveEvents(_ActorGhost),
                seed: default(MoveInfo),
                quiet: System.TimeSpan.FromSeconds(0.5),
                timeout: System.TimeSpan.FromSeconds(10));
            yield return fence;
            TestWait.AssertDone(fence, "被接受指令的 MoveEvent 應在靜默窗內全部抵達");

            // 走路是分段 root motion:段邊界/循環 wrap 也會發 Speed>0 的 MoveInfo,
            // 但只有起走與「被接受的重定向」會把朝向切到新的指令方向(各方向相距 ≥60°);
            // 以朝向相對前一組首筆變化 >30° 分組,各組首筆即被接受指令的生效事件
            var acceptedInfos = new System.Collections.Generic.List<MoveInfo>();
            foreach (var info in movingInfos)
            {
                if (acceptedInfos.Count == 0 ||
                    Vector2.Angle(acceptedInfos[acceptedInfos.Count - 1].Facing, info.Facing) > 30f)
                    acceptedInfos.Add(info);
            }

            // 節流不變量:起走與任兩筆被接受的方向變更的伺服器時間差 ≥ 間隔(容忍 1ms 取樣誤差)
            for (var i = 1; i < acceptedInfos.Count; i++)
            {
                Assert.GreaterOrEqual(
                    acceptedInfos[i].StartTicks - acceptedInfos[i - 1].StartTicks,
                    intervalTicks - System.TimeSpan.TicksPerMillisecond,
                    "被接受的相鄰方向變更時間差不得小於 MoveAcceptInterval");
            }

            // 被拒的指令不生效:重定向回傳 true 的數量 == 朝向切換的組數 - 1(第一組是起走)
            var acceptedCount = 0;
            foreach (var r in results)
            {
                if (!r.HasError && r.Result)
                    acceptedCount++;
            }
            Assert.AreEqual(acceptedCount, acceptedInfos.Count - 1,
                "被接受的重定向數應等於朝向切換的組數 - 1(第一組是起走;被拒不產生朝向切換)");

            // 停止已生效:收到駐留 MoveInfo(晚訂閱安全:若已駐留,replay 即滿足條件)
            var stand = TestWait.First(
                TestWait.MoveEvents(_ActorGhost), i => i.Speed == 0f, System.TimeSpan.FromSeconds(10));
            yield return stand;
            TestWait.AssertDone(stand, "停止後應收到駐留 MoveInfo");

            // 停止後重新起走恢復接受(用連發未包含的方向 -X 區別);
            // 走 PlayerRemote(自動依當前 Transition 選走路型別與停止目標)
            var resumeMove = TestWait.FirstWithRetry(
                () => TestWait.MoveEvents(_ActorGhost).Where(i => i.Speed > 0f && i.Facing.x < -0.9f),
                onAttempt: () => _ClientPlayer.Move(new Vector2(-1f, 0f)),
                perAttempt: System.TimeSpan.FromSeconds(3),
                attempts: 5);
            yield return resumeMove;
            TestWait.AssertDone(resumeMove, "停止後重新起走應生效(朝向 -X)");

            _ClientPlayer.Stop();
            moveSub.Dispose();
        }

        IPlayer _PlayerGhost;
        IControllable _ControllableGhost;
        IActor _ActorGhost;
        PinionCore.Project2.Client.ActorShell _Shell;
        PinionCore.Project2.Client.PlayerRemote _ClientPlayer;

        // 共用進場流程:Verify → 取得 IPlayer → 等 ActorProvider 建出對應殼 → 取得 Client.PlayerRemote
        IEnumerator _EnterWorld(string playerName)
        {
            var verifiableSupply = TestWait.First(
                _Client.Queryer.QueryNotifier<IUserEntry>().SupplyEvent()
                    .SelectMany(entry => entry.Verifiers.SupplyEvent()),
                System.TimeSpan.FromSeconds(10));
            yield return verifiableSupply;
            TestWait.AssertDone(verifiableSupply, "連線後 client 應從 User 服務收到 IVerifier");

            var verifyResult = TestWait.First(
                verifiableSupply.Result.Verify(playerName, ModelType.Cube).RemoteValue(),
                System.TimeSpan.FromSeconds(10));
            yield return verifyResult;
            TestWait.AssertDone(verifyResult, "Verify 未收到回傳值");
            Assert.IsTrue(verifyResult.Result, "首次註冊的名字 Verify 應回傳 true");

            var playerSupply = TestWait.First(
                _Client.Queryer.QueryNotifier<IUserEntry>().SupplyEvent()
                    .SelectMany(entry => entry.Games.SupplyEvent())
                    .SelectMany(game => game.Player.SupplyEvent()),
                System.TimeSpan.FromSeconds(15));
            yield return playerSupply;
            TestWait.AssertDone(playerSupply, "Verify 通過後 client 應收到 IPlayer");
            _PlayerGhost = playerSupply.Result;
            System.Guid actorId = _PlayerGhost.ActorId;

            // 控制介面由 IPlayer.Controllable 供應(world 端控制狀態機開關;只供應給擁有者)
            var controllableSupply = TestWait.First(
                _PlayerGhost.Controllable.SupplyEvent(),
                System.TimeSpan.FromSeconds(15));
            yield return controllableSupply;
            TestWait.AssertDone(controllableSupply, "client 應收到自身的 IControllable ghost");
            _ControllableGhost = controllableSupply.Result;

            // 自身的 IActor ghost(經 IPlayer.Actors 供應):MoveEvent 的權威狀態來源
            var actorSupply = TestWait.First(
                _PlayerGhost.Actors.SupplyEvent(), a => a.ActorId == actorId,
                System.TimeSpan.FromSeconds(15));
            yield return actorSupply;
            TestWait.AssertDone(actorSupply, "client 應收到自身的 IActor ghost");
            _ActorGhost = actorSupply.Result;

            // ActorProvider.SupplyEvent 會 replay 既有殼,晚訂閱安全
            var provider = _Scenes.FindComponent<PinionCore.Project2.Client.ActorProvider>("Client", "Handlers");
            Assert.NotNull(provider, "Client 場景應有 ActorProvider");
            var shellWait = TestWait.First(provider.SupplyEvent(), a => a.ActorId == actorId, System.TimeSpan.FromSeconds(15));
            yield return shellWait;
            TestWait.AssertDone(shellWait, "ActorProvider 應在 Client 場景實例化出對應 ActorId 的 Client.ActorShell");
            _Shell = shellWait.Result;

            _ClientPlayer = _Scenes.FindComponent<PinionCore.Project2.Client.PlayerRemote>("Client", "Handlers");
            Assert.NotNull(_ClientPlayer, "Client 場景的 Handlers 應掛有 Client.PlayerRemote");
        }
    }
}
