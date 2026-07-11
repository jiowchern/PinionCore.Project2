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
    /// 由 Client.Player.Move 送出世界座標 XZ 方向,
    /// World 即刻把朝向設為該方向並沿直線前進(ω 恆為 0),發回 MoveInfo,
    /// 驗證殼沿直線移動、改向連續、Move 節流(MoveAcceptInterval)、Stop 駐留。
    /// </summary>
    public class ActorMoveTests
    {
        // 與 Assets/Configs/ActorConfigs/ActorConfig.asset 一致:
        // 該 asset 未序列化 MoveSpeed,執行期使用 script 預設 1.0
        const float MoveSpeed = 1.0f;

        StandaloneSceneLoader _Scenes;
        PinionCore.NetSync.Standalone.Connector _Connector;
        PinionCore.NetSync.Gateways.GatewayClient _Client;
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

            // Gateway 場景有兩個 Listener(SessionEndpoint / RegistryEndpoint),必須用物件名區分
            PinionCore.NetSync.Standalone.Listener listener = null;
            while (listener == null || _Connector == null)
            {
                if (listener == null)
                    listener = _Scenes.FindComponent<PinionCore.NetSync.Standalone.Listener>("Gateway", "SessionEndpoint");
                if (_Connector == null)
                    _Connector = _Scenes.FindComponent<PinionCore.NetSync.Standalone.Connector>("Client", "GatewayClient");
                yield return null;
            }
            _Client = _Connector.GetComponent<PinionCore.NetSync.Gateways.GatewayClient>();

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

            MoveInfo? lastMove = null;
            var moveSub = UniRx.Observable.FromEvent<MoveInfo>(
                    h => _PlayerGhost.MoveEvent += h, h => _PlayerGhost.MoveEvent -= h)
                .Subscribe(info => lastMove = info);

            var startPos = _Shell.Target.position;
            _ClientPlayer.Move(new Vector2(0f, 1f));

            // 等殼走出約 1.5 單位
            var deadline = Time.realtimeSinceStartup + 1.5f / MoveSpeed + 15f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if ((_Shell.Target.position - startPos).magnitude >= 1.5f)
                    break;
                yield return null;
            }
            Assert.GreaterOrEqual((_Shell.Target.position - startPos).magnitude, 1.5f, "殼應已沿直線移動約 1.5 單位");

            // MoveInfo 欄位:等速直線
            Assert.IsTrue(lastMove.HasValue && lastMove.Value.Speed > 0f, "ghost 應收到移動中的 MoveInfo");
            Assert.AreEqual(MoveSpeed, lastMove.Value.Speed, 0.01f, "MoveInfo.Speed 應為 ActorConfig.MoveSpeed");

            // 位移方向 ≈ 世界 +Z
            var displacement = _Shell.Target.position - startPos;
            var directionXZ = new Vector2(displacement.x, displacement.z).normalized;
            Assert.Greater(Vector2.Dot(directionXZ, new Vector2(0f, 1f)), 0.99f, "位移方向應沿世界 +Z");

            _ClientPlayer.Stop();
            moveSub.Dispose();
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

            MoveInfo? lastMove = null;
            var moveSub = UniRx.Observable.FromEvent<MoveInfo>(
                    h => _PlayerGhost.MoveEvent += h, h => _PlayerGhost.MoveEvent -= h)
                .Subscribe(info => lastMove = info);

            var startPos = _Shell.Target.position;
            var halfSqrt2 = Mathf.Sqrt(2f) / 2f;
            _ClientPlayer.Move(new Vector2(halfSqrt2, halfSqrt2)); // 世界右前 45°

            // 等移動中的 MoveInfo 抵達;RPC/事件註冊可能與 session/soul 綁定流程競態而掉失
            //(曾觀測到 "Soul not found" 的 Advance Error),逾時就重訂閱(觸發 replay
            // 取回當下狀態)並重送 Move,兩種掉失模式都能恢復
            var deadline = Time.realtimeSinceStartup + 15f;
            var nextRetry = Time.realtimeSinceStartup + 3f;
            var retried = false;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (lastMove.HasValue && lastMove.Value.Speed > 0f)
                    break;
                if (Time.realtimeSinceStartup >= nextRetry)
                {
                    moveSub.Dispose();
                    moveSub = UniRx.Observable.FromEvent<MoveInfo>(
                            h => _PlayerGhost.MoveEvent += h, h => _PlayerGhost.MoveEvent -= h)
                        .Subscribe(info => lastMove = info);
                    _ClientPlayer.Move(new Vector2(halfSqrt2, halfSqrt2));
                    nextRetry = Time.realtimeSinceStartup + 3f;
                    retried = true;
                }
                yield return null;
            }
            Assert.IsTrue(lastMove.HasValue && lastMove.Value.Speed > 0f, "ghost 應收到移動中的 MoveInfo");

            // 有重試時可能仍有較新的 MoveInfo 在途,等事件靜默 0.5 秒再取最後一筆作為預測依據
            if (retried)
            {
                var quietStart = lastMove.Value.StartTicks;
                var quietUntil = Time.realtimeSinceStartup + 0.5f;
                while (Time.realtimeSinceStartup < quietUntil)
                {
                    if (lastMove.Value.StartTicks != quietStart)
                    {
                        quietStart = lastMove.Value.StartTicks;
                        quietUntil = Time.realtimeSinceStartup + 0.5f;
                    }
                    yield return null;
                }
            }
            var moveInfo = lastMove.Value;
            Assert.AreEqual(MoveSpeed, moveInfo.Speed, 0.01f, "MoveInfo.Speed 應為 ActorConfig.MoveSpeed");
            Assert.Greater(Vector2.Dot(moveInfo.Facing.normalized, new Vector2(halfSqrt2, halfSqrt2)), 0.999f,
                "朝向應瞬轉為指令的世界方向");

            // 殼訂閱的是 IActor ghost、測試訂閱的是 IPlayer ghost,MoveEvent 抵達幀序不同;
            // 等殼實際起步(已套用移動中的 MoveInfo)再開始量測,避免量到殼還停在出生點的空窗
            deadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if ((_Shell.Target.position - startPos).magnitude > 0.02f)
                    break;
                yield return null;
            }
            Assert.Greater((_Shell.Target.position - startPos).magnitude, 0.02f, "殼應已開始移動");

            // 觀察 ~2 秒:每幀以取樣器預測位置,與殼實際位置比對。
            // 殼由 UniRx EveryUpdate 定位,它取樣所用的時鐘值必落在
            // 「本 coroutine 上次讀 CurrentTime」與「本次讀 CurrentTime」之間(時鐘單調、殼每幀重取樣);
            // 幀長與對時往前跳(卡頓後封包補送重新錨定)都會造成合法落差,
            // 因此以兩次量測間 world time 的前進量 × 速度作為該幀可扣除的時序落差上界。
            var maxDeviation = 0f;
            var prevTicks = _Shell.WorldTime.CurrentTime.Ticks;
            yield return null;
            var observeUntil = Time.realtimeSinceStartup + 2f;
            while (Time.realtimeSinceStartup < observeUntil)
            {
                var nowTicks = _Shell.WorldTime.CurrentTime.Ticks;
                var elapsed = (nowTicks - moveInfo.StartTicks) / (double)System.TimeSpan.TicksPerSecond;
                if (elapsed < 0)
                    elapsed = 0;
                MoveSampler.Sample(moveInfo, elapsed, out var predicted, out _);

                var actual = new Vector2(_Shell.Target.position.x, _Shell.Target.position.z);
                var slack = moveInfo.Speed * (float)((nowTicks - prevTicks) / (double)System.TimeSpan.TicksPerSecond);
                maxDeviation = Mathf.Max(maxDeviation, Vector2.Distance(predicted, actual) - slack);
                prevTicks = nowTicks;
                yield return null;
            }
            Assert.Less(maxDeviation, 0.25f, "殼位置應與 MoveInfo 取樣預測一致(已扣除兩次量測間時鐘前進量的時序落差)");

            // 位移方向 ≈ 指令的世界方向
            var displacement = _Shell.Target.position - startPos;
            var directionXZ = new Vector2(displacement.x, displacement.z).normalized;
            Assert.Greater(Vector2.Dot(directionXZ, new Vector2(halfSqrt2, halfSqrt2)), 0.99f, "位移方向應沿指令的世界方向");

            _ClientPlayer.Stop();
            moveSub.Dispose();
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

            MoveInfo? lastMove = null;
            var moveSub = UniRx.Observable.FromEvent<MoveInfo>(
                    h => _PlayerGhost.MoveEvent += h, h => _PlayerGhost.MoveEvent -= h)
                .Subscribe(info => lastMove = info);

            var startPos = _Shell.Target.position;
            var halfSqrt2 = Mathf.Sqrt(2f) / 2f;
            _ClientPlayer.Move(new Vector2(halfSqrt2, halfSqrt2)); // 世界右前 45°

            // 等第一段移動的 MoveInfo 與殼實際起步
            //(移動 0.5 單位 ≈ 0.5 秒,遠超過 MoveAcceptInterval,第二發不會被節流)
            var deadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (lastMove.HasValue && lastMove.Value.Speed > 0f && lastMove.Value.Facing.x > 0.5f
                    && (_Shell.Target.position - startPos).sqrMagnitude > 0.25f)
                    break;
                yield return null;
            }
            Assert.IsTrue(lastMove.HasValue && lastMove.Value.Speed > 0f && lastMove.Value.Facing.x > 0.5f,
                "第一段應朝世界右前方直走");
            var firstInfo = lastMove.Value;

            _ClientPlayer.Move(new Vector2(-halfSqrt2, halfSqrt2)); // 途中改世界左前 45°

            deadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (lastMove.HasValue && lastMove.Value.Facing.x < -0.5f)
                    break;
                yield return null;
            }
            Assert.IsTrue(lastMove.HasValue && lastMove.Value.Facing.x < -0.5f, "改向後 ghost 應收到朝向翻轉的 MoveInfo");
            var secondInfo = lastMove.Value;
            Assert.Greater(Vector2.Dot(secondInfo.Facing.normalized, new Vector2(-halfSqrt2, halfSqrt2)), 0.999f,
                "朝向應瞬轉為新的世界方向");

            // 位置連續:新起點 = 舊軌跡取樣至新 StartTicks(伺服器端純數學,容差取小)
            var handover = (secondInfo.StartTicks - firstInfo.StartTicks) / (double)System.TimeSpan.TicksPerSecond;
            MoveSampler.Sample(firstInfo, handover, out var predicted, out _);
            Assert.Less(Vector2.Distance(predicted, secondInfo.Position), 0.01f, "改向瞬間位置應連續,不得跳點");

            _ClientPlayer.Stop();
            moveSub.Dispose();
        }

        /// <summary>
        /// 停止:移動中 Stop → ghost 收到 Speed==0 的駐留 MoveInfo,殼吸附後不再移動。
        /// </summary>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator MoveStopTest()
        {
            yield return _EnterWorld("StopTester");

            MoveInfo? lastMove = null;
            var moveSub = UniRx.Observable.FromEvent<MoveInfo>(
                    h => _PlayerGhost.MoveEvent += h, h => _PlayerGhost.MoveEvent -= h)
                .Subscribe(info => lastMove = info);

            var startPos = _Shell.Target.position;
            _ClientPlayer.Move(new Vector2(0f, 1f));

            // 等殼走出約 1 單位再喊停
            var deadline = Time.realtimeSinceStartup + 1f / MoveSpeed + 15f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if ((_Shell.Target.position - startPos).magnitude >= 1f)
                    break;
                yield return null;
            }
            Assert.GreaterOrEqual((_Shell.Target.position - startPos).magnitude, 1f, "移動應已進行約 1 單位");

            // 清空擷取值,等 Stop 產生的駐留 MoveInfo(Speed==0),
            // 以免跟訂閱時 replay 的初始駐留混淆
            lastMove = null;
            _ClientPlayer.Stop();

            deadline = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (lastMove.HasValue && lastMove.Value.Speed == 0f)
                    break;
                yield return null;
            }
            Assert.IsTrue(lastMove.HasValue, "Stop 後 ghost 應收到 MoveEvent");
            var standInfo = lastMove.Value;
            Assert.AreEqual(0f, standInfo.Speed, "Stop 後應收到駐留 MoveInfo(Speed==0)");

            // 殼的取樣位置可能落後 server(時鐘偏差),且殼的 IActor ghost 與測試訂閱的
            // IPlayer ghost 事件抵達幀序不同;駐留一到殼會先瞬移吸附到 server 停點,
            // 這段吸附不屬於「Stop 後的移動」——先等殼停到駐留點再開始量
            deadline = Time.realtimeSinceStartup + 5f;
            while (Time.realtimeSinceStartup < deadline)
            {
                var pos = _Shell.Target.position;
                if (Vector2.Distance(new Vector2(pos.x, pos.z), standInfo.Position) < 0.1f)
                    break;
                yield return null;
            }

            // 確認殼真的停住:1 秒內位置不再變化
            var stopPos = _Shell.Target.position;
            var holdUntil = Time.realtimeSinceStartup + 1f;
            while (Time.realtimeSinceStartup < holdUntil)
                yield return null;

            Assert.Less((_Shell.Target.position - stopPos).magnitude, 0.02f, "Stop 後殼不應再移動");
            Assert.Less(Vector2.Distance(new Vector2(_Shell.Target.position.x, _Shell.Target.position.z), standInfo.Position), 0.1f,
                "殼的停止位置應與伺服器駐留點一致(容忍延遲取樣差)");

            moveSub.Dispose();
        }

        /// <summary>
        /// Move 節流:同幀連發多個 Move,驗證節流的不變量 —
        /// 任兩筆「被接受的 Move」的 StartTicks 差 ≥ MoveAcceptInterval、
        /// 被拒的指令不產生 MoveEvent;Stop 不受間隔限制隨時接受;之後的 Move 恢復接受。
        /// (不斷言「第二發必被拒」:兩發指令在 server 端的處理時點受編輯器幀時序影響,
        /// 可能被拉開超過間隔,屆時接受是合法行為)
        /// </summary>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator MoveAcceptIntervalTest()
        {
            // 與 ActorConfig.asset 一致:該 asset 未序列化 MoveAcceptInterval,執行期使用 script 預設 0.1
            const float MoveAcceptInterval = 0.1f;
            var intervalTicks = (long)(MoveAcceptInterval * System.TimeSpan.TicksPerSecond);

            yield return _EnterWorld("IntervalTester");

            MoveInfo? lastMove = null;
            var movingInfos = new System.Collections.Generic.List<MoveInfo>();
            var moveSub = UniRx.Observable.FromEvent<MoveInfo>(
                    h => _PlayerGhost.MoveEvent += h, h => _PlayerGhost.MoveEvent -= h)
                .Subscribe(info =>
                {
                    lastMove = info;
                    if (info.Speed > 0f)
                        movingInfos.Add(info);
                });

            // 同一幀連發 6 個相異方向,再緊接一發 Stop(有序管道保證 server 依序處理)
            var degrees = new[] { 0f, 60f, 120f, 180f, 240f, 300f };
            var results = new System.Collections.Generic.List<UniRx.ObservableYieldInstruction<bool>>();
            foreach (var deg in degrees)
            {
                var dir = new Vector2(Mathf.Sin(deg * Mathf.Deg2Rad), Mathf.Cos(deg * Mathf.Deg2Rad));
                results.Add(_PlayerGhost.Move(dir).RemoteValue()
                    .First().Timeout(System.TimeSpan.FromSeconds(10)).ToYieldInstruction(throwOnError: false));
            }
            var stopResult = _PlayerGhost.Stop().RemoteValue()
                .First().Timeout(System.TimeSpan.FromSeconds(10)).ToYieldInstruction(throwOnError: false);

            foreach (var r in results)
                yield return r;
            yield return stopResult;

            Assert.IsFalse(results[0].HasError, "首發 Move 未收到回傳值");
            Assert.IsTrue(results[0].Result, "首發 Move 應被接受");
            Assert.IsFalse(stopResult.HasError, "Stop 未收到回傳值");
            Assert.IsTrue(stopResult.Result, "Stop 不受間隔限制,緊跟在 Move 後仍應被接受");

            // 等事件靜默,確保被接受指令的 MoveEvent 都已抵達
            var quietUntil = Time.realtimeSinceStartup + 0.5f;
            var quietCount = movingInfos.Count;
            while (Time.realtimeSinceStartup < quietUntil)
            {
                if (movingInfos.Count != quietCount)
                {
                    quietCount = movingInfos.Count;
                    quietUntil = Time.realtimeSinceStartup + 0.5f;
                }
                yield return null;
            }

            // 節流不變量:任兩筆被接受的 Move 的伺服器時間差 ≥ 間隔(容忍 1ms 取樣誤差)
            for (var i = 1; i < movingInfos.Count; i++)
            {
                Assert.GreaterOrEqual(
                    movingInfos[i].StartTicks - movingInfos[i - 1].StartTicks,
                    intervalTicks - System.TimeSpan.TicksPerMillisecond,
                    "被接受的相鄰 Move 時間差不得小於 MoveAcceptInterval");
            }

            // 被拒的指令不生效:回傳 true 的數量 == ghost 收到的移動 MoveInfo 數
            var acceptedCount = 0;
            foreach (var r in results)
            {
                if (!r.HasError && r.Result)
                    acceptedCount++;
            }
            Assert.AreEqual(acceptedCount, movingInfos.Count,
                "被接受的 Move 數應等於 ghost 收到的移動 MoveInfo 數(被拒不產生事件)");

            // Stop 已生效:收到駐留 MoveInfo
            var deadline = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (lastMove.HasValue && lastMove.Value.Speed == 0f)
                    break;
                yield return null;
            }
            Assert.IsTrue(lastMove.HasValue && lastMove.Value.Speed == 0f, "Stop 後應收到駐留 MoveInfo");

            // 間隔過後的 Move 恢復接受(用連發未包含的方向 -X 區別)
            var waitUntil = Time.realtimeSinceStartup + 0.5f;
            while (Time.realtimeSinceStartup < waitUntil)
                yield return null;
            var resumeResult = _PlayerGhost.Move(new Vector2(-1f, 0f)).RemoteValue()
                .First().Timeout(System.TimeSpan.FromSeconds(10)).ToYieldInstruction(throwOnError: false);
            yield return resumeResult;
            Assert.IsFalse(resumeResult.HasError, "間隔後的 Move 未收到回傳值");
            Assert.IsTrue(resumeResult.Result, "間隔過後的 Move 應恢復接受");

            deadline = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (lastMove.HasValue && lastMove.Value.Speed > 0f && lastMove.Value.Facing.x < -0.9f)
                    break;
                yield return null;
            }
            Assert.IsTrue(lastMove.HasValue && lastMove.Value.Speed > 0f && lastMove.Value.Facing.x < -0.9f,
                "間隔後的 Move 應生效(朝向 -X)");

            _ClientPlayer.Stop();
            moveSub.Dispose();
        }

        ICharactor _PlayerGhost;
        PinionCore.Project2.Client.Actor _Shell;
        PinionCore.Project2.Client.Player _ClientPlayer;

        // 共用進場流程:Verify → 取得 IPlayer → 以 ActorId 找到殼 → 取得 Client.Player
        IEnumerator _EnterWorld(string playerName)
        {
            var verifiableSupply = _Client.Queryer.QueryNotifier<IVerifiable>().SupplyEvent()
                .First()
                .Timeout(System.TimeSpan.FromSeconds(10))
                .ToYieldInstruction(throwOnError: false);
            yield return verifiableSupply;
            Assert.IsFalse(verifiableSupply.HasError, "連線後 client 應從 User 服務收到 IVerifiable");

            var verifyResult = verifiableSupply.Result.Verify(playerName).RemoteValue()
                .First()
                .Timeout(System.TimeSpan.FromSeconds(10))
                .ToYieldInstruction(throwOnError: false);
            yield return verifyResult;
            Assert.IsFalse(verifyResult.HasError, "Verify 未收到回傳值");
            Assert.IsTrue(verifyResult.Result, "首次註冊的名字 Verify 應回傳 true");

            var playerSupply = _Client.Queryer.QueryNotifier<ICharactor>().SupplyEvent()
                .First()
                .Timeout(System.TimeSpan.FromSeconds(15))
                .ToYieldInstruction(throwOnError: false);
            yield return playerSupply;
            Assert.IsFalse(playerSupply.HasError, "Verify 通過後 client 應收到 IPlayer");
            _PlayerGhost = playerSupply.Result;
            System.Guid actorId = _PlayerGhost.ActorId;

            _Shell = null;
            var deadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < deadline)
            {
                _Shell = _FindActor(actorId);
                if (_Shell != null)
                    break;
                yield return null;
            }
            Assert.NotNull(_Shell, "ActorProvider 應在 Client 場景實例化出對應 ActorId 的 Client.Actor");

            _ClientPlayer = _Scenes.FindComponent<PinionCore.Project2.Client.Player>("Client", "Handlers");
            Assert.NotNull(_ClientPlayer, "Client 場景的 Handlers 應掛有 Client.Player");
        }

        // 以 ActorId 從 Client 場景找出對應的殼;實例名稱是 "(Clone)" 結尾,不能靠物件名找
        PinionCore.Project2.Client.Actor _FindActor(System.Guid actorId)
        {
            var scene = SceneManager.GetSceneByName("Client");
            if (!scene.isLoaded)
                return null;

            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var actor in root.GetComponentsInChildren<PinionCore.Project2.Client.Actor>(true))
                {
                    if (actor.ActorId == actorId)
                        return actor;
                }
            }
            return null;
        }
    }
}
