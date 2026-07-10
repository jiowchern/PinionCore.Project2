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
    /// Actor 轉向式移動端到端測試:
    /// 比照 ActorDisplayNameTests 的四場景 Standalone 流程,
    /// Verify 進入遊戲後取得 IActor / IPlayer,
    /// 由 Client.Player.Move 送出「以角色前方為基準」的相對方向,
    /// World 換算成 (Speed, AngularSpeed) 的弧線 MoveInfo 發回,
    /// 驗證 Client.Actor 殼沿直線/弧線移動、改向連續、Stop 駐留。
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
        /// 直線前進:輸入 (0,1)(正前方)→ 無偏轉,沿出生朝向 +Z 直線移動。
        /// 轉向式移動沒有終點,量測方向與 MoveInfo 欄位後 Stop 收尾。
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
            Assert.AreEqual(0f, lastMove.Value.AngularSpeed, 0.01f, "正前方輸入不應產生角速度");

            // 位移方向 ≈ 出生朝向 +Z
            var displacement = _Shell.Target.position - startPos;
            var directionXZ = new Vector2(displacement.x, displacement.z).normalized;
            Assert.Greater(Vector2.Dot(directionXZ, new Vector2(0f, 1f)), 0.99f, "位移方向應沿出生朝向 +Z");

            _ClientPlayer.Stop();
            moveSub.Dispose();
        }

        /// <summary>
        /// 弧線偏轉:輸入 45°(右前)→ ω = π/4 rad/s 的等速圓周,持續向右偏轉。
        /// 以共用取樣器對 ghost 收到的 MoveInfo 預測位置,與殼實際位置比對,
        /// 同時驗證 server 端換算與兩端公式一致。
        /// </summary>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator MoveArcTest()
        {
            yield return _EnterWorld("ArcTester");

            MoveInfo? lastMove = null;
            var moveSub = UniRx.Observable.FromEvent<MoveInfo>(
                    h => _PlayerGhost.MoveEvent += h, h => _PlayerGhost.MoveEvent -= h)
                .Subscribe(info => lastMove = info);

            var startPos = _Shell.Target.position;
            var halfSqrt2 = Mathf.Sqrt(2f) / 2f;
            _ClientPlayer.Move(new Vector2(halfSqrt2, halfSqrt2)); // 相對前方 45°(右)

            // 等移動中的 MoveInfo 抵達
            var deadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (lastMove.HasValue && lastMove.Value.Speed > 0f)
                    break;
                yield return null;
            }
            Assert.IsTrue(lastMove.HasValue && lastMove.Value.Speed > 0f, "ghost 應收到移動中的 MoveInfo");
            var arcInfo = lastMove.Value;
            Assert.AreEqual(MoveSpeed, arcInfo.Speed, 0.01f, "MoveInfo.Speed 應為 ActorConfig.MoveSpeed");
            Assert.AreEqual(Mathf.PI / 4f, arcInfo.AngularSpeed, 0.01f, "45° 輸入應換算為 π/4 rad/s(比例式:偏移角/秒)");

            // 觀察 ~2 秒:每幀以取樣器預測位置,與殼實際位置比對
            var maxDeviation = 0f;
            var observeUntil = Time.realtimeSinceStartup + 2f;
            while (Time.realtimeSinceStartup < observeUntil)
            {
                var elapsed = (_Shell.WorldTime.CurrentTime.Ticks - arcInfo.StartTicks) / (double)System.TimeSpan.TicksPerSecond;
                if (elapsed < 0)
                    elapsed = 0;
                MoveSampler.Sample(arcInfo, elapsed, out var predicted, out _);

                var actual = new Vector2(_Shell.Target.position.x, _Shell.Target.position.z);
                maxDeviation = Mathf.Max(maxDeviation, Vector2.Distance(predicted, actual));
                yield return null;
            }
            Assert.Less(maxDeviation, 0.25f, "殼位置應與 MoveInfo 取樣預測一致(容忍幀時序差)");

            // 向右偏轉:出生面向 +Z、ω>0 → 軌跡彎向 +X
            Assert.Greater(_Shell.Target.position.x - startPos.x, 0.5f, "弧線應持續向右(+X)偏轉");

            _ClientPlayer.Stop();
            moveSub.Dispose();
        }

        /// <summary>
        /// 改向:45° 改 -45° → 新 MoveInfo 的 ω 變號,
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
            _ClientPlayer.Move(new Vector2(halfSqrt2, halfSqrt2)); // 右前 45°

            // 等第一段移動的 MoveInfo 與殼實際起步
            var deadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (lastMove.HasValue && lastMove.Value.AngularSpeed > 0f
                    && (_Shell.Target.position - startPos).sqrMagnitude > 0.25f)
                    break;
                yield return null;
            }
            Assert.IsTrue(lastMove.HasValue && lastMove.Value.AngularSpeed > 0f, "第一段應為右轉弧線");
            var firstInfo = lastMove.Value;

            _ClientPlayer.Move(new Vector2(-halfSqrt2, halfSqrt2)); // 途中改左前 45°

            deadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (lastMove.HasValue && lastMove.Value.AngularSpeed < 0f)
                    break;
                yield return null;
            }
            Assert.IsTrue(lastMove.HasValue && lastMove.Value.AngularSpeed < 0f, "改向後 ghost 應收到 ω 變號的 MoveInfo");
            var secondInfo = lastMove.Value;
            Assert.AreEqual(-Mathf.PI / 4f, secondInfo.AngularSpeed, 0.01f, "-45° 輸入應換算為 -π/4 rad/s");

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
            Assert.AreEqual(0f, standInfo.AngularSpeed, "駐留 MoveInfo 不應帶角速度");

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

        IPlayer _PlayerGhost;
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

            var playerSupply = _Client.Queryer.QueryNotifier<IPlayer>().SupplyEvent()
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
