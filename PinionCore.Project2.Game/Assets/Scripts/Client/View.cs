using UnityEngine;
using System.Linq;
using System.Collections.Generic;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using PinionCore.Project2.Shared;
using PinionCore.NetSync.Gateways;
using UniRx;
using System.Linq;
using PinionCore.NetSync.UniRx;
namespace PinionCore.Project2.Client
{
    public class View : MonoBehaviour
    {
        // 抽象為 QueryerHost:可掛 Client(直連)或 GatewayClient(經 Router)
        public PinionCore.NetSync.QueryerHost Gateway;
        public WorldConfigSet WorldInfos;

        // 追蹤已實例化的 Addressable handle,離場時釋放,避免 WebGL 記憶體洩漏。
        private readonly List<AsyncOperationHandle<GameObject>> _terrainHandles = new List<AsyncOperationHandle<GameObject>>();

        public View()
        {

        }

        public void Start()
        {
            var obs = from view in Gateway.Queryer.QueryNotifier<IView>().SupplyEvent()                      
                      select view;
            obs.Subscribe(_Setup).AddTo(this);
        }

        void _Setup(IView view)
        {
            var info = WorldInfos.Find(view.Name.Value);
            if (info == null)
            {
                Debug.LogError($"[View] 找不到對應的 WorldInfo: {view.Name.Value}");
                return;
            }

            if (info.TerrainPrefab == null || !info.TerrainPrefab.RuntimeKeyIsValid())
            {
                Debug.LogError($"[View] WorldConfig '{info.Name}' 未設定有效的 TerrainPrefab (Addressable)。");
                return;
            }

            // 非同步載入並實例化地形。WebGL 無多執行緒,務必走 async API,避免單幀卡頓。
            var handle = info.TerrainPrefab.InstantiateAsync(transform);
            handle.Completed += op =>
            {
                if (op.Status != AsyncOperationStatus.Succeeded)
                {
                    Debug.LogError($"[View] 地形資源載入失敗: {info.Name} (key={info.TerrainPrefab.RuntimeKey})");
                }
            };
            _terrainHandles.Add(handle);
        }

        private void OnDestroy()
        {
            foreach (var handle in _terrainHandles)
            {
                if (handle.IsValid())
                {
                    Addressables.ReleaseInstance(handle);
                }
            }
            _terrainHandles.Clear();
        }
    }
}
