using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace PinionCore.Project2.Standalone
{
    public class UserConnectWorld : MonoBehaviour
    {
        PinionCore.NetSync.Standalone.Connector _Connector;
        PinionCore.NetSync.Standalone.Listener _Listener;

        // Start is called once before the first execution of Update after the MonoBehaviour is created
        void Start()
        {
            StartCoroutine(_Link());
            
        }

        // User/World scene 是非同步載入, 持續等待直到兩邊的元件都取得為止
        IEnumerator _Link()
        {
            while (_Connector == null || _Listener == null)
            {
                if (_Connector == null)
                    _Connector = _FindComponent<PinionCore.NetSync.Standalone.Connector>("User", "WorldAgent");
                if (_Listener == null)
                    _Listener = _FindComponent<PinionCore.NetSync.Standalone.Listener>("World", "WorldService");
                yield return null;
            }

            // 跨 scene 引用無法序列化, 只能在 runtime 指定
            _Connector.Listener = _Listener;
            _Connector.Connect();
        }

        // 從指定 scene 中名為 gameObjectName 的物件上取得元件
        // (User scene 的 UserService 也有 Listener, 所以必須用名稱區分)
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

        // Update is called once per frame
        void Update()
        {

        }
    }

}
