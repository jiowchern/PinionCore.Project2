using System.Collections;
using NUnit.Framework;
using UniRx;                       // First/Timeout/ToYieldInstruction 等 UniRx 擴充
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using PinionCore.NetSync.UniRx;    // SupplyEvent():把 INotifier<T> 轉成 IObservable<T>
using PinionCore.Project2.Shared;

namespace PinionCore.Project2.Tests
{
    public class UniverseTests
    {
        PinionCore.NetSync.Standalone.Connector _Connector;
        PinionCore.NetSync.Standalone.Listener _Listener;
        PinionCore.NetSync.Client _Client;

        [UnitySetUp]
        public IEnumerator SetUp() {
            // load world scene
            yield return _LoadScene("World");
            // load user scene
            yield return _LoadScene("User");

            // get listener from world scene
            // get client from user scene
            // (User scene 的 UserService 也有 Listener, 所以必須用名稱區分)
            // UnitySetUp 不受 [Timeout] 保護,找元件必須有界限,否則會掛死整輪
            var found = TestWait.Until(() =>
            {
                if (_Listener == null)
                    _Listener = _FindComponent<PinionCore.NetSync.Standalone.Listener>("World", "WorldService");
                if (_Connector == null)
                    _Connector = _FindComponent<PinionCore.NetSync.Standalone.Connector>("User", "WorldAgent");
                return _Listener != null && _Connector != null;
            }, System.TimeSpan.FromSeconds(30));
            yield return found;
            TestWait.AssertDone(found, "SetUp:應在時限內找到 WorldService Listener 與 WorldAgent Connector");
            _Client = _Connector.GetComponent<PinionCore.NetSync.Client>();

            // 等一個 frame 讓 StandaloneStartToBind.Start 先把 Listener 綁上 Server
            yield return null;

            // bind listener to client            
            _Connector.Connect(_Listener);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_Connector != null && _Connector.IsConnect())
                _Connector.Disconnect();
            _Connector = null;
            _Listener = null;
            _Client = null;

            yield return _UnloadScene("User");
            yield return _UnloadScene("World");
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator ClientSupplyUniverseTest()
        {
            var supply = TestWait.First(
                _Client.Queryer.QueryNotifier<IUniverse>().SupplyEvent(),
                System.TimeSpan.FromSeconds(5));

            yield return supply;

            TestWait.AssertDone(supply, "連線後 client 應從 world 收到 IUniverse");
            Assert.NotNull(supply.Result, "連線後 client 應從 world 收到 IUniverse");
        }

        // 場景未加入 Build Settings, editor 測試以資產路徑載入
        static IEnumerator _LoadScene(string name)
        {
#if UNITY_EDITOR
            yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(
                $"Assets/Project/Scenes/{name}.unity",
                new LoadSceneParameters(LoadSceneMode.Additive));
#else
            yield return SceneManager.LoadSceneAsync(name, LoadSceneMode.Additive);
#endif
        }

        static IEnumerator _UnloadScene(string name)
        {
            var scene = SceneManager.GetSceneByName(name);
            if (scene.isLoaded)
                yield return SceneManager.UnloadSceneAsync(scene);
        }

        // 從指定 scene 中名為 gameObjectName 的物件上取得元件
        static T _FindComponent<T>(string sceneName, string gameObjectName) where T : Component
        {
            var scene = SceneManager.GetSceneByName(sceneName);
            if (!scene.isLoaded)
                return null;

            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var component in root.GetComponentsInChildren<T>(true))
                {
                    if (component.gameObject.name == gameObjectName)
                        return component;
                }
            }
            return null;
        }
    }

}
