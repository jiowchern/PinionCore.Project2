using PinionCore.NetSync.Gateways;
using PinionCore.NetSync.UniRx;
using PinionCore.Project2.Shared;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UniRx;
using System.Linq;
using System;

namespace PinionCore.Project2.Client
{
    public class ActorProvider : MonoBehaviour
    {
        public ActorConfig[] ActorConfigs;
        public GameObject ActorRoot;

        public GatewayClient Client;

        readonly CompositeDisposable _disposables;

        // 以 ActorId 追蹤 InstantiateAsync 的 handle:
        // - Unsupply 時據此釋放實例(走 Addressables 引用計數,不用 Destroy)
        // - 載入完成前就 Unsupply 的情況,由 Completed callback 收尾,避免殭屍物件
        readonly System.Collections.Generic.Dictionary<Guid, AsyncOperationHandle<GameObject>> _actors;

        public ActorProvider()
        {
            _disposables = new CompositeDisposable();
            _actors = new System.Collections.Generic.Dictionary<Guid, AsyncOperationHandle<GameObject>>();
        }

        void Start()
        {
            var createObs = from actor in Client.Queryer.QueryNotifier<IActor>().SupplyEvent()
                            select actor;
            var createDispose = createObs.Subscribe(_Create);
            _disposables.Add(createDispose);

            var destroyObs = from actor in Client.Queryer.QueryNotifier<IActor>().UnsupplyEvent()
                             select actor;
            var destroyDispose = destroyObs.Subscribe(_Destroy);
            _disposables.Add(destroyDispose);
        }

        private void OnDestroy()
        {
            _disposables.Dispose();
            foreach (var handle in _actors.Values)
            {
                if (handle.IsValid())
                    Addressables.ReleaseInstance(handle);
            }
            _actors.Clear();
        }

        private void _Create(IActor actor)
        {
            Guid actorId = actor.ActorId;
            var config = ActorConfigs.FirstOrDefault(c => c.Name == actor.ModelName);
            if (config == null)
            {
                Debug.LogError($"找不到對應的 ActorConfig, ModelName={actor.ModelName}");
                return;
            }
            if (config.ModelPrefab == null || !config.ModelPrefab.RuntimeKeyIsValid())
            {
                Debug.LogError($"ActorConfig '{config.Name}' 未設定有效的 ModelPrefab (Addressable)");
                return;
            }
            if (_actors.ContainsKey(actorId))
            {
                Debug.LogWarning($"重複的 Supply, ActorId={actorId}");
                return;
            }

            var handle = config.ModelPrefab.InstantiateAsync(ActorRoot.transform);
            _actors.Add(actorId, handle);
            handle.Completed += op =>
            {
                if (op.Status != AsyncOperationStatus.Succeeded)
                {
                    Debug.LogError($"Actor 資源載入失敗, ModelName={actor.ModelName} (key={config.ModelPrefab.RuntimeKey})");
                    _actors.Remove(actorId);
                    return;
                }
                // 載入期間已 Unsupply:直接釋放實例
                if (!_actors.ContainsKey(actorId))
                {
                    Addressables.ReleaseInstance(op.Result);
                    return;
                }
                var actorComponent = op.Result.GetComponent<Actor>();
                if (actorComponent == null)
                {
                    Debug.LogError($"找不到對應的 Actor component, ModelName={actor.ModelName}");
                    return;
                }
                actorComponent.Setup(actor);
            };
        }

        private void _Destroy(IActor actor)
        {
            Guid actorId = actor.ActorId;
            if (!_actors.TryGetValue(actorId, out var handle))
                return;
            _actors.Remove(actorId);

            // 尚未完成的載入由 Completed callback 收尾;已完成的直接釋放實例
            if (handle.IsValid() && handle.IsDone && handle.Status == AsyncOperationStatus.Succeeded)
                Addressables.ReleaseInstance(handle.Result);
        }
    }
}
