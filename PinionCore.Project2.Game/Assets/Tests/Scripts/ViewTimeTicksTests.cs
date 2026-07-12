using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    /// IView.TimeTicksEvent 端到端測試:
    /// Gateway/World/User/Client 四場景全部載入(AutoConnector 一律覆寫為 Standalone),
    /// Client 以 Standalone.Connector 連上 Gateway 的 SessionEndpoint,
    /// 走 IVerifiable.Verify 進入遊戲取得 IView,驗證時間戳數值與更新間隔。
    /// </summary>
    public class ViewTimeTicksTests
    {
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
            // UnitySetUp 不受 [Timeout] 保護,找元件必須有界限,否則會掛死整輪
            PinionCore.NetSync.Standalone.Listener listener = null;
            var found = TestWait.Until(() =>
            {
                if (listener == null)
                    listener = _Scenes.FindComponent<PinionCore.NetSync.Standalone.Listener>("Gateway", "SessionEndpoint");
                if (_Connector == null)
                    _Connector = _Scenes.FindComponent<PinionCore.NetSync.Standalone.Connector>("Client", "GatewayClient");
                return listener != null && _Connector != null;
            }, System.TimeSpan.FromSeconds(30));
            yield return found;
            TestWait.AssertDone(found, "SetUp:應在時限內找到 SessionEndpoint Listener 與 GatewayClient Connector");
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

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator VerifyThenReceiveTimeTicksTest()
        {
            // 1. 連上 Gateway 後,Router 把 session 路由到 User 服務,收到 IVerifiable
            var verifiableSupply = TestWait.First(
                _Client.Queryer.QueryNotifier<IVerifiable>().SupplyEvent(),
                System.TimeSpan.FromSeconds(10));
            yield return verifiableSupply;
            TestWait.AssertDone(verifiableSupply, "連線後 client 應從 User 服務收到 IVerifiable");
            var verifiable = verifiableSupply.Result;

            // 2. Verify 通過
            var verifyResult = TestWait.First(
                verifiable.Verify("TimeTicksTester").RemoteValue(),
                System.TimeSpan.FromSeconds(10));
            yield return verifyResult;
            TestWait.AssertDone(verifyResult, "Verify 未收到回傳值");
            Assert.IsTrue(verifyResult.Result, "首次註冊的名字 Verify 應回傳 true");

            // 3. Verify 後 UserGame 會進入世界,把 World 以 IView 綁回 client session
            var viewSupply = TestWait.First(
                _Client.Queryer.QueryNotifier<IView>().SupplyEvent(),
                System.TimeSpan.FromSeconds(15));
            yield return viewSupply;
            TestWait.AssertDone(viewSupply, "Verify 通過後 client 應收到 IView");
            var view = viewSupply.Result;

            // 4+5. 事件註冊當下 World 先送一次目前時間,之後每 TimeUpdateInterval 秒推送一次。
            //    間隔以 World 場景 Universe 上的設定為準(Roster 固定把玩家送進 Test1 世界)。
            //    先讀 interval 再訂閱;單一訂閱一次收滿 3 筆——
            //    拆成兩次訂閱會多觸發一次 server replay,tick 索引語意就變了。
            var universe = _Scenes.FindComponent<PinionCore.Project2.Worlds.Universe>("World", "Universe");
            Assert.NotNull(universe, "World 場景應有 Universe 物件");
            var interval = universe.WorldConfigs.First(c => c.Name == "Test1").TimeUpdateInterval;

            // per-gap 逾時 interval+10s:同時界定「註冊→立即 replay」與「每個週期 tick 的間距」
            var ticksWait = TestWait.Count(
                PinionCore.NetSync.UniRx.Extensions.EventObservable<long>(
                    h => view.TimeTicksEvent += h, h => view.TimeTicksEvent -= h),
                3,
                System.TimeSpan.FromSeconds(interval + 10),
                perGap: true);
            yield return ticksWait;
            TestWait.AssertDone(ticksWait, $"註冊 TimeTicksEvent 後應立即收到一次時間戳,並持續收到週期性時間戳(每 {interval} 秒一次)");
            var ticks = ticksWait.Result;

            Assert.Greater(ticks[0], 0L, "時間戳應為正值(世界建立起算的 stopwatch ticks)");

            // 兩個週期性 tick 的差值直接來自伺服器 stopwatch,不受網路/frame 延遲影響
            // (ticks[0] 是註冊時的立即推送,與週期無關)
            var delta = (ticks[2] - ticks[1]) / (double)System.TimeSpan.TicksPerSecond;
            Assert.GreaterOrEqual(delta, interval - 0.05, $"更新間隔不應小於設定值 {interval} 秒(實測 {delta:F2} 秒)");
            Assert.LessOrEqual(delta, interval + 2.0, $"更新間隔應接近設定值 {interval} 秒(實測 {delta:F2} 秒,容忍編輯器 frame 延遲)");

            // 時間戳必須單調遞增
            for (var i = 1; i < ticks.Length; ++i)
                Assert.Greater(ticks[i], ticks[i - 1], "時間戳應單調遞增");
        }
    }
}
