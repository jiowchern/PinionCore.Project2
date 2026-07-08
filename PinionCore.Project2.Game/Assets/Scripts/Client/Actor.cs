using PinionCore.Project2.Shared;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace PinionCore.Project2.Client
{
    public class Actor : MonoBehaviour
    {
        public TMPro.TextMeshPro DisplayName;
        public Transform Target;

        // 模型是表現層,由 Actor 自行透過 Addressables 載入;
        // 載入失敗或載入中被銷毀都不影響殼(邏輯層)的生命週期
        AsyncOperationHandle<GameObject> _modelHandle;
        bool _destroyed;

        // 快照 ghost 資料;Unsupply 後 ghost 失效,之後不再讀取
        public void Setup(IActor actor)
        {
            DisplayName.text = actor.DisplayName;
            Target.position = actor.Position;
        }

        public void LoadModel(AssetReferenceGameObject modelPrefab)
        {
            if (modelPrefab == null || !modelPrefab.RuntimeKeyIsValid())
            {
                Debug.LogError($"未設定有效的 ModelPrefab (Addressable), Actor={name}");
                return;
            }

            _modelHandle = modelPrefab.InstantiateAsync(Target);
            _modelHandle.Completed += op =>
            {
                if (op.Status != AsyncOperationStatus.Succeeded)
                {
                    Debug.LogError($"Actor 模型載入失敗 (key={modelPrefab.RuntimeKey})");
                    return;
                }
                // 載入期間殼已被銷毀(Unsupply):直接釋放實例
                if (_destroyed)
                {
                    Addressables.ReleaseInstance(op.Result);
                }
            };
        }

        void OnDestroy()
        {
            _destroyed = true;
            // 尚未完成的載入由 Completed callback 收尾;已完成的直接釋放實例
            if (_modelHandle.IsValid() && _modelHandle.IsDone && _modelHandle.Status == AsyncOperationStatus.Succeeded)
                Addressables.ReleaseInstance(_modelHandle.Result);
        }
    }
}
