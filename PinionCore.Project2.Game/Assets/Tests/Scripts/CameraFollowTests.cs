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
    /// 攝影機追蹤主角端到端測試:
    /// 比照 ActorMoveTests 的四場景 Standalone 流程,
    /// 進入遊戲後驗證 PlayerCameraHandler 把 CinemachineCamera 綁到本地玩家的殼,
    /// 移動時鏡頭保持跟隨距離,斷線後解除綁定。
    /// </summary>
    public class CameraFollowTests
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

            yield return _Scenes.Load("Gateway");
            yield return _Scenes.Load("World");
            yield return _Scenes.Load("User");
            yield return _Scenes.Load("Client");

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

        /// <summary>
        /// 進場後 PlayerCameraHandler 應把 vcam 的 Follow/LookAt 綁到本地玩家殼的 Target,
        /// 且 Main Camera 帶有 CinemachineBrain、位置收斂到 OrbitalFollow 的跟隨距離。
        /// </summary>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator CameraBindsAndTracksTest()
        {
            yield return _EnterWorld("CameraTester");

            // 綁定要等殼 activeSelf(首個 MoveEvent);Cinemachine 元件狀態沒有事件來源,逐幀條件等待
            Unity.Cinemachine.CinemachineCamera vcam = null;
            var bound = TestWait.Until(() =>
            {
                if (vcam == null)
                    vcam = _FindClientComponent<Unity.Cinemachine.CinemachineCamera>();
                return vcam != null && vcam.Follow == _Shell.Target;
            }, System.TimeSpan.FromSeconds(15));
            yield return bound;
            TestWait.AssertDone(bound, "vcam.Follow 應綁定本地玩家殼的 Target");
            Assert.AreEqual(_Shell.Target, vcam.LookAt, "vcam.LookAt 應綁定本地玩家殼的 Target");

            var brain = _FindClientComponent<Unity.Cinemachine.CinemachineBrain>();
            Assert.NotNull(brain, "Main Camera 應掛有 CinemachineBrain");
            var mainCam = brain.GetComponent<Camera>();

            var orbital = vcam.GetComponent<Unity.Cinemachine.CinemachineOrbitalFollow>();
            Assert.NotNull(orbital, "vcam 應使用 CinemachineOrbitalFollow");

            // 等 Brain 的 LateUpdate 把鏡頭移到位(Cut blend,應該很快)
            var expected = orbital.Radius * orbital.RadialAxis.Value;
            var positioned = TestWait.Until(() =>
            {
                var point = _Shell.Target.position + orbital.TargetOffset;
                return Mathf.Abs(Vector3.Distance(mainCam.transform.position, point) - expected) < 1f;
            }, System.TimeSpan.FromSeconds(5));
            yield return positioned;
            TestWait.AssertDone(positioned, "鏡頭與跟隨點的距離應為 OrbitalFollow 的有效半徑");
            var followPoint = _Shell.Target.position + orbital.TargetOffset;
            var d0 = Vector3.Distance(mainCam.transform.position, followPoint);

            // 移動主角 ~1.5 單位,鏡頭應保持跟隨距離並朝相同方向位移
            var camStart = mainCam.transform.position;
            var shellStart = _Shell.Target.position;
            _ClientPlayer.Move(new Vector2(0f, 1f));

            var moved = TestWait.Until(
                () => (_Shell.Target.position - shellStart).magnitude >= 1.5f,
                System.TimeSpan.FromSeconds(20));
            yield return moved;
            TestWait.AssertDone(moved, "殼應已移動約 1.5 單位");
            _ClientPlayer.Stop();

            // 等位置阻尼收斂:盲等改為條件等(鏡頭回到跟隨距離)
            var settled = TestWait.Until(() =>
            {
                var point = _Shell.Target.position + orbital.TargetOffset;
                return Mathf.Abs(Vector3.Distance(mainCam.transform.position, point) - d0) < 1f;
            }, System.TimeSpan.FromSeconds(5));
            yield return settled;
            TestWait.AssertDone(settled, "移動後鏡頭應維持跟隨距離");

            var camDelta = mainCam.transform.position - camStart;
            var shellDelta = _Shell.Target.position - shellStart;
            Assert.Greater(Vector3.Dot(camDelta.normalized, shellDelta.normalized), 0.9f,
                "鏡頭位移方向應與殼一致");
        }

        // 注意:沒有「斷線解綁」測試——主動斷線時 Gateway.Agent.Disable 只停用路由層 agent,
        // 承載 IPlayer/IActor 的 sub-agent 不會發 Unsupply(AgentPool.OnConnectionUnsupply 不會觸發),
        // 斷線後整個世界(殼、ghost)凍結,鏡頭停在凍結的殼上與整體行為一致;
        // PlayerCameraHandler 的解綁路徑只在連線中的正常 unsupply(離開世界、actor 銷毀)生效。

        ICharactor _PlayerGhost;
        PinionCore.Project2.Client.Actor _Shell;
        PinionCore.Project2.Client.Player _ClientPlayer;

        // 共用進場流程:Verify → 取得 IPlayer → 等 ActorProvider 建出對應殼 → 取得 Client.Player
        IEnumerator _EnterWorld(string playerName)
        {
            var verifiableSupply = TestWait.First(
                _Client.Queryer.QueryNotifier<IVerifiable>().SupplyEvent(),
                System.TimeSpan.FromSeconds(10));
            yield return verifiableSupply;
            TestWait.AssertDone(verifiableSupply, "連線後 client 應從 User 服務收到 IVerifiable");

            var verifyResult = TestWait.First(
                verifiableSupply.Result.Verify(playerName).RemoteValue(),
                System.TimeSpan.FromSeconds(10));
            yield return verifyResult;
            TestWait.AssertDone(verifyResult, "Verify 未收到回傳值");
            Assert.IsTrue(verifyResult.Result, "首次註冊的名字 Verify 應回傳 true");

            var playerSupply = TestWait.First(
                _Client.Queryer.QueryNotifier<ICharactor>().SupplyEvent(),
                System.TimeSpan.FromSeconds(15));
            yield return playerSupply;
            TestWait.AssertDone(playerSupply, "Verify 通過後 client 應收到 IPlayer");
            _PlayerGhost = playerSupply.Result;
            System.Guid actorId = _PlayerGhost.ActorId;

            // ActorProvider.SupplyEvent 會 replay 既有殼,晚訂閱安全
            var provider = _Scenes.FindComponent<PinionCore.Project2.Client.ActorProvider>("Client", "Handlers");
            Assert.NotNull(provider, "Client 場景應有 ActorProvider");
            var shellWait = TestWait.First(provider.SupplyEvent(), a => a.ActorId == actorId, System.TimeSpan.FromSeconds(15));
            yield return shellWait;
            TestWait.AssertDone(shellWait, "ActorProvider 應在 Client 場景實例化出對應 ActorId 的 Client.Actor");
            _Shell = shellWait.Result;

            _ClientPlayer = _Scenes.FindComponent<PinionCore.Project2.Client.Player>("Client", "Handlers");
            Assert.NotNull(_ClientPlayer, "Client 場景的 Handlers 應掛有 Client.Player");
        }

        // 從 Client 場景根物件找元件(含 inactive)
        T _FindClientComponent<T>() where T : Component
        {
            var scene = SceneManager.GetSceneByName("Client");
            if (!scene.isLoaded)
                return null;

            foreach (var root in scene.GetRootGameObjects())
            {
                var found = root.GetComponentInChildren<T>(true);
                if (found != null)
                    return found;
            }
            return null;
        }
    }
}
