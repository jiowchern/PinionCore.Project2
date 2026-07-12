using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using PinionCore.Project2.Shared;

namespace PinionCore.Project2.Tests
{
    /// <summary>
    /// 可復用的測試場景載入器:
    /// 載入場景時把場景內所有 AutoConnector 的 Type 覆寫為 Standalone,
    /// 讓測試不依賴場景序列化的連線方式(Tcp/Web),全程走 in-process 連線。
    /// SceneManager.sceneLoaded 於 Awake/OnEnable 之後、Start 之前觸發,
    /// 所以在回呼內改 Type 可保證 AutoConnector.Start 讀到的是 Standalone。
    /// </summary>
    public class StandaloneSceneLoader : System.IDisposable
    {
        readonly List<string> _LoadedSceneNames = new List<string>();

        public StandaloneSceneLoader()
        {
            SceneManager.sceneLoaded += _OverrideAutoConnectors;
        }

        public void Dispose()
        {
            SceneManager.sceneLoaded -= _OverrideAutoConnectors;
        }

        // 場景未加入 Build Settings, editor 測試以資產路徑載入
        public IEnumerator Load(string name)
        {
#if UNITY_EDITOR
            yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(
                $"Assets/Scenes/{name}.unity",
                new LoadSceneParameters(LoadSceneMode.Additive));
#else
            yield return SceneManager.LoadSceneAsync(name, LoadSceneMode.Additive);
#endif
            _LoadedSceneNames.Add(name);
        }

        // 以載入的相反順序卸載所有場景
        public IEnumerator UnloadAll()
        {
            for (var i = _LoadedSceneNames.Count - 1; i >= 0; --i)
            {
                var scene = SceneManager.GetSceneByName(_LoadedSceneNames[i]);
                if (scene.isLoaded)
                    yield return SceneManager.UnloadSceneAsync(scene);
            }
            _LoadedSceneNames.Clear();
        }

        // 從 Client 場景的 QueryHost wrapper 解析目前拓撲的連線 host(GatewayClient 或直連 Client);
        // 場景未載入或 wrapper 的 Host 未指派時回傳 null。
        // Connector 與 Standalone.ListenerLocator(連線目標)都掛在回傳 host 的 GameObject 上。
        public PinionCore.NetSync.QueryerHost FindClientHost()
        {
            var wrapper = FindComponent<PinionCore.NetSync.QueryerHost>("Client", "QueryHost");
            if (wrapper == null)
                return null;

            var host = wrapper.Resolve();
            return host != wrapper ? host : null;
        }

        // 從指定 scene 中名為 gameObjectName 的物件上取得元件;場景未載入或找不到時回傳 null
        public T FindComponent<T>(string sceneName, string gameObjectName) where T : Component
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

        void _OverrideAutoConnectors(Scene scene, LoadSceneMode mode)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var connector in root.GetComponentsInChildren<AutoConnector>(true))
                {
                    connector.Type = AutoConnector.ConnectorType.Standalone;
                }
            }
        }
    }
}
