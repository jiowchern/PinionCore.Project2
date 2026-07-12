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
    /// WASD 攝影機相對移動端到端測試:
    /// 比照 ActorMoveTests 的四場景 Standalone 流程,
    /// 透過 PlayerInputHandler.InputSource 測試接縫模擬按鍵
    /// (WASD→Vector2 的 binding 是 Input System 內建 composite,不在驗證範圍),
    /// 驗證「輸入 → 相機相對換算成世界方向 → 瞬轉直走 → 放開 Stop」這段自家邏輯。
    /// </summary>
    public class PlayerInputMoveTests
    {
        // 與 Assets/Configs/ActorConfigs/ActorConfig.asset 一致:
        // 該 asset 未序列化 MoveSpeed,執行期使用 script 預設 1.0
        const float MoveSpeed = 1.0f;

        StandaloneSceneLoader _Scenes;
        PinionCore.NetSync.Standalone.Connector _Connector;
        PinionCore.NetSync.Client _Client;
        bool _PreviousRunInBackground;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            // 編輯器失焦時 player loop 會停住,連線流程會卡住
            _PreviousRunInBackground = Application.runInBackground;
            Application.runInBackground = true;

            _Scenes = new StandaloneSceneLoader();

            yield return _Scenes.Load("Gateway");
            yield return _Scenes.Load("World");
            yield return _Scenes.Load("User");
            yield return _Scenes.Load("Client");

            PinionCore.NetSync.Standalone.Listener listener = null;
            while (listener == null || _Connector == null)
            {
                if (listener == null)
                    listener = _Scenes.FindComponent<PinionCore.NetSync.Standalone.Listener>("User", "GatewayService");
                if (_Connector == null)
                    _Connector = _Scenes.FindComponent<PinionCore.NetSync.Standalone.Connector>("Client", "GatewayClient");
                yield return null;
            }
            _Client = _Connector.GetComponent<PinionCore.NetSync.Client>();

            yield return null;

            _Connector.Connect(listener);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            // 歸零輸入讓 handler 送出 Stop 並停止重送,
            // 避免 session 收尾期間仍在送 Move(Soul not found 競態)
            if (_InputHandler != null)
            {
                _InputHandler.InputSource = () => Vector2.zero;
                yield return null;
                _InputHandler.InputSource = null;
            }

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

        /// <summary>
        /// 按住 A(相機左):首發 MoveInfo 的朝向即為相機左方的世界方向(瞬轉、ω==0),
        /// 沿直線前進;殼位置應與 MoveInfo 取樣預測一致(扣除時鐘前進量 slack)。
        /// </summary>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator HoldLeftMovesCameraLeftTest()
        {
            yield return _EnterWorld("WasdLeftTester");

            // 等 Cinemachine 綁定殼且相機位置穩定(brain 於 LateUpdate 吸附),
            // 讓 handler 讀到的相機朝向就是玩家實際所見
            var camera = _InputHandler.CameraTransform;
            var deadline = Time.realtimeSinceStartup + 10f;
            var prevCamPos = camera.position;
            var stableFrames = 0;
            while (Time.realtimeSinceStartup < deadline && stableFrames < 3)
            {
                yield return null;
                var near = (camera.position - _Shell.Target.position).magnitude < 30f;
                var still = (camera.position - prevCamPos).magnitude < 0.01f;
                prevCamPos = camera.position;
                stableFrames = near && still ? stableFrames + 1 : 0;
            }

            MoveInfo? lastMove = null;
            MoveInfo? firstMoving = null;
            var moveSub = UniRx.Observable.FromEvent<MoveInfo>(
                    h => _PlayerGhost.MoveEvent += h, h => _PlayerGhost.MoveEvent -= h)
                .Subscribe(info =>
                {
                    lastMove = info;
                    if (!firstMoving.HasValue && info.Speed > 0f)
                        firstMoving = info;
                });

            // 依 handler 同一公式預先算「按 A 的世界方向」(相機左):
            // 輸入 (-1,0) → worldDir = -PerpRight(camFwd)
            var expectedDir = _CameraLeft(camera);

            _InputHandler.InputSource = () => new Vector2(-1f, 0f); // 按住 A

            // 等首個移動中的 MoveInfo;指令掉失由 handler 的低頻重檢(RecheckInterval)自癒,
            // 事件註冊可能與 session/soul 綁定流程競態掉失,逾時重訂閱(replay 取回當下狀態)
            deadline = Time.realtimeSinceStartup + 15f;
            var nextRetry = Time.realtimeSinceStartup + 3f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (firstMoving.HasValue)
                    break;
                if (Time.realtimeSinceStartup >= nextRetry)
                {
                    moveSub.Dispose();
                    moveSub = UniRx.Observable.FromEvent<MoveInfo>(
                            h => _PlayerGhost.MoveEvent += h, h => _PlayerGhost.MoveEvent -= h)
                        .Subscribe(info =>
                        {
                            lastMove = info;
                            if (!firstMoving.HasValue && info.Speed > 0f)
                                firstMoving = info;
                        });
                    nextRetry = Time.realtimeSinceStartup + 3f;
                }
                yield return null;
            }
            Assert.IsTrue(firstMoving.HasValue, "按住 A 後 ghost 應收到移動中的 MoveInfo");
            Assert.AreEqual(MoveSpeed, firstMoving.Value.Speed, 0.01f, "MoveInfo.Speed 應為 ActorConfig.MoveSpeed");
            Assert.Greater(Vector2.Dot(firstMoving.Value.Facing.normalized, expectedDir), 0.99f,
                "首發朝向應瞬轉為相機左方的世界方向(相機相對換算)");

            // 觀察 ~2 秒:每幀以「當下最新 MoveInfo」取樣預測位置,與殼實際位置比對。
            // 方向不變 handler 靜默,MoveInfo 通常不再更新;若有修復重送,每筆起點連續,逐幀跟最新一筆比即可;
            // 幀長與對時往前跳的合法落差以「兩次量測間 world time 前進量 × 速度」作 slack 扣除
            var maxDeviation = 0f;
            var prevTicks = _Shell.WorldTime.CurrentTime.Ticks;
            yield return null;
            var observeUntil = Time.realtimeSinceStartup + 2f;
            while (Time.realtimeSinceStartup < observeUntil)
            {
                var info = lastMove.Value;
                var nowTicks = _Shell.WorldTime.CurrentTime.Ticks;
                var elapsed = (nowTicks - info.StartTicks) / (double)System.TimeSpan.TicksPerSecond;
                if (elapsed < 0)
                    elapsed = 0;
                MoveSampler.Sample(info, elapsed, out var predicted, out _);

                var actual = new Vector2(_Shell.Target.position.x, _Shell.Target.position.z);
                var slack = info.Speed * (float)((nowTicks - prevTicks) / (double)System.TimeSpan.TicksPerSecond);
                maxDeviation = Mathf.Max(maxDeviation, Vector2.Distance(predicted, actual) - slack);
                prevTicks = nowTicks;
                yield return null;
            }
            Assert.Less(maxDeviation, 0.25f, "殼位置應與最新 MoveInfo 取樣預測一致(已扣除時鐘前進量 slack)");

            _InputHandler.InputSource = () => Vector2.zero;
            moveSub.Dispose();
        }

        /// <summary>
        /// 放開按鍵:handler 邊緣觸發送一次 Stop → ghost 收到 Speed==0 的駐留 MoveInfo,
        /// 之後不再有事件(驗 Stop 只送一次、deadzone 下不再送 Move),殼吸附後不動。
        /// </summary>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator ReleaseSendsStopOnceTest()
        {
            yield return _EnterWorld("WasdStopTester");

            MoveInfo? lastMove = null;
            var eventCount = 0;
            var moveSub = UniRx.Observable.FromEvent<MoveInfo>(
                    h => _PlayerGhost.MoveEvent += h, h => _PlayerGhost.MoveEvent -= h)
                .Subscribe(info =>
                {
                    lastMove = info;
                    eventCount++;
                });

            var startPos = _Shell.Target.position;
            _InputHandler.InputSource = () => new Vector2(0f, 1f); // 按住 W

            // 等殼走出約 0.5 單位再放開
            var deadline = Time.realtimeSinceStartup + 0.5f / MoveSpeed + 15f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if ((_Shell.Target.position - startPos).magnitude >= 0.5f)
                    break;
                yield return null;
            }
            Assert.GreaterOrEqual((_Shell.Target.position - startPos).magnitude, 0.5f, "按住 W 殼應已移動約 0.5 單位");

            // 清空擷取值再放開,等 Stop 產生的駐留 MoveInfo(Speed==0)
            lastMove = null;
            _InputHandler.InputSource = () => Vector2.zero;

            deadline = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (lastMove.HasValue && lastMove.Value.Speed == 0f)
                    break;
                yield return null;
            }
            Assert.IsTrue(lastMove.HasValue && lastMove.Value.Speed == 0f, "放開後 ghost 應收到駐留 MoveInfo(Speed==0)");
            var standInfo = lastMove.Value;

            // 駐留後 1 秒不應再有任何 MoveEvent:Stop 只送一次、deadzone 下不再送 Move
            var countAfterStop = eventCount;
            var holdUntil = Time.realtimeSinceStartup + 1f;
            while (Time.realtimeSinceStartup < holdUntil)
                yield return null;
            Assert.AreEqual(countAfterStop, eventCount, "駐留後不應再收到 MoveEvent(Stop 應只送一次)");

            // 殼吸附到 server 停點後不再移動(比照 MoveStopTest)
            deadline = Time.realtimeSinceStartup + 5f;
            while (Time.realtimeSinceStartup < deadline)
            {
                var pos = _Shell.Target.position;
                if (Vector2.Distance(new Vector2(pos.x, pos.z), standInfo.Position) < 0.1f)
                    break;
                yield return null;
            }
            var stopPos = _Shell.Target.position;
            holdUntil = Time.realtimeSinceStartup + 1f;
            while (Time.realtimeSinceStartup < holdUntil)
                yield return null;
            Assert.Less((_Shell.Target.position - stopPos).magnitude, 0.02f, "放開後殼不應再移動");

            moveSub.Dispose();
        }

        ICharactor _PlayerGhost;
        PinionCore.Project2.Client.Actor _Shell;
        PinionCore.Project2.Client.PlayerInputHandler _InputHandler;

        // 輸入 (-1,0)(A)經 handler 公式的世界目標方向:相機 forward 投影 XZ 的左方
        static Vector2 _CameraLeft(Transform camera)
        {
            var f = camera.forward;
            var fwd = new Vector2(f.x, f.z).normalized;
            return new Vector2(-fwd.y, fwd.x);
        }

        // 共用進場流程:Verify → 取得 IPlayer → 以 ActorId 找到殼 → 取得 PlayerInputHandler
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

            _InputHandler = _Scenes.FindComponent<PinionCore.Project2.Client.PlayerInputHandler>("Client", "Handlers");
            Assert.NotNull(_InputHandler, "Client 場景的 Handlers 應掛有 PlayerInputHandler");
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
