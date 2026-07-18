using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using PinionCore.Project2.Shared;
using System.Threading;

namespace PinionCore.Project2.Server
{
    /// <summary>
    /// 統一伺服器啟動器:additive 載入 World/User/Bot 三場景於同一行程,
    /// 場景間以序列化的 Standalone AutoConnector 在 in-process 串接(無 Gateway 拓撲)。
    /// sceneLoaded 於 Awake/OnEnable 之後、Start 之前觸發,在回呼內停用指向 Gateway
    /// 場景的 AutoConnector(registry 註冊),可保證其 Start 不會啟動重試迴圈。
    /// headless 環境需鎖幀率,否則跑滿 CPU。
    /// </summary>
    public class ServersEntry : MonoBehaviour
    {
        [Tooltip("依序 additive 載入的伺服器場景。")]
        public string[] SceneNames = { "World", "User", "Bot" };

        [Tooltip("headless 幀率上限,同時是模擬 tick 頻率。")]
        public int TargetFrameRate = 600;

        
        readonly Utility.AutoPowerRegulator _PowerRegulator = new PinionCore.Utility.AutoPowerRegulator(new Utility.PowerRegulator(60));
        private readonly CancellationTokenSource _CancellationToken = new CancellationTokenSource();

        void Awake()
        {
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = TargetFrameRate;
            SceneManager.sceneLoaded += _DisableGatewayConnectors;
        }
        private void Update()
        {
            //_PowerRegulator.Operate(_CancellationToken);
        }
        IEnumerator Start()
        {
            foreach (var name in SceneNames)
            {
                // 場景未加入全域 Build Settings,editor 播放以資產路徑載入(同 StandaloneSceneLoader)
#if UNITY_EDITOR
                yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(
                    $"Assets/Project/Scenes/{name}.unity",
                    new LoadSceneParameters(LoadSceneMode.Additive));
#else
                yield return SceneManager.LoadSceneAsync(name, LoadSceneMode.Additive);
#endif
            }
            Debug.Log($"[ServersEntry] 伺服器場景載入完成:{string.Join(", ", SceneNames)}");
        }

        void OnDestroy()
        {
            SceneManager.sceneLoaded -= _DisableGatewayConnectors;
            _CancellationToken.Cancel();
        }

        void _DisableGatewayConnectors(Scene scene, LoadSceneMode mode)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var connector in root.GetComponentsInChildren<AutoConnector>(true))
                {
                    if (connector.StandaloneSceneName == "Gateway")
                    {
                        connector.enabled = false;
                        Debug.Log($"[ServersEntry] 停用 Gateway AutoConnector:{scene.name}/{connector.gameObject.name}");
                    }
                }
            }
        }
    }
}
