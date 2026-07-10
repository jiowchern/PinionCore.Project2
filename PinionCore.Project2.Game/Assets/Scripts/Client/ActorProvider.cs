using PinionCore.NetSync.Gateways;
using PinionCore.NetSync.UniRx;
using PinionCore.Project2.Shared;
using UnityEngine;
using UniRx;
using System.Linq;
using System;

namespace PinionCore.Project2.Client
{
    public class ActorProvider : MonoBehaviour
    {
        public ActorConfig[] ActorConfigs;
        public GameObject ActorRoot;

        // 殼 prefab:直接序列化引用(不走 Addressables),保證 Supply 時同步建立成功
        public Actor ShellPrefab;

        public GatewayClient Client;

        // 殼取樣位置用的同步時間來源
        public WorldTimeHandler WorldTime;

        readonly CompositeDisposable _disposables;

        // Supply 時同步插入,配合事件有序(Supply 必先於 Unsupply),
        // Unsupply 時 entry 必定存在;模型資源的載入與釋放由 Actor 自行負責
        readonly System.Collections.Generic.Dictionary<Guid, Actor> _actors;

        // 殼的 Supply/Unsupply,語意比照 NetSync 的 SupplyEvent:訂閱時 replay 既有殼,再接未來建立
        readonly Subject<Actor> _supplied;
        readonly Subject<Actor> _unsupplied;

        public ActorProvider()
        {
            _disposables = new CompositeDisposable();
            _actors = new System.Collections.Generic.Dictionary<Guid, Actor>();
            _supplied = new Subject<Actor>();
            _unsupplied = new Subject<Actor>();
        }

        // 全程主執行緒且 replay 快照同步完成,Concat 不會漏事件
        public IObservable<Actor> SupplyEvent()
        {
            return Observable.Defer(() => _actors.Values.ToArray().ToObservable()).Concat(_supplied);
        }

        public IObservable<Actor> UnsupplyEvent()
        {
            return _unsupplied;
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
            _supplied.OnCompleted();
            _unsupplied.OnCompleted();
            // 殼是 ActorRoot 子物件,場景卸載時各自的 Actor.OnDestroy 釋放模型資源
            _actors.Clear();
        }

        private void _Create(IActor actor)
        {
            Guid actorId = actor.ActorId;
            if (_actors.ContainsKey(actorId))
            {
                Debug.LogWarning($"重複的 Supply, ActorId={actorId}");
                return;
            }

            var shell = Instantiate(ShellPrefab, ActorRoot.transform);
            _actors.Add(actorId, shell);
            shell.Setup(actor, WorldTime);

            var config = ActorConfigs.FirstOrDefault(c => c.Name == actor.ModelName);
            if (config == null)
            {
                Debug.LogError($"找不到對應的 ActorConfig, ModelName={actor.ModelName}");
                // 模型載入失敗殼仍存在且會移動,照樣對外通知
                _supplied.OnNext(shell);
                return;
            }
            shell.LoadModel(config.ModelPrefab);
            _supplied.OnNext(shell);
        }

        private void _Destroy(IActor actor)
        {
            Guid actorId = actor.ActorId;
            if (!_actors.TryGetValue(actorId, out var shell))
                return;
            _actors.Remove(actorId);
            // 在 Destroy 前通知,讓消費端(鏡頭跟隨)能在 transform 失效前解除引用
            _unsupplied.OnNext(shell);
            Destroy(shell.gameObject);
        }
    }
}
