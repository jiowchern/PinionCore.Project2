
using PinionCore.NetSync.UniRx;
using PinionCore.Project2.Shared;
using System;
using System.Linq;

using UniRx;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace PinionCore.Project2.Client
{
    public class Actor : MonoBehaviour
    {
        public TMPro.TextMeshPro DisplayName;
        public Transform Target;

        // 對應伺服器端的身分,讓外部(鏡頭跟隨、測試)能辨識這個殼屬於哪個 actor
        public System.Guid ActorId { get; private set; }

        // 取樣時間來源:與伺服器同步的 world time,由 ActorProvider 於 Setup 傳入
        public WorldTimeHandler WorldTime { get; private set; }

        // 模型是表現層,由 Actor 自行透過 Addressables 載入;
        // 載入失敗或載入中被銷毀都不影響殼(邏輯層)的生命週期
        AsyncOperationHandle<GameObject> _modelHandle;
        bool _destroyed;

        // 位置唯一來源:伺服器的時間戳路徑,每幀以 world time 取樣;
        // 收到第一個 MoveEvent(訂閱時必有 replay)前殼保持隱藏。
        MoveInfo? _MoveInfo;

        // 冒險/戰鬥狀態:來自 IActor.StatusEvent(訂閱時必有 replay),驅動 Animator 的 status 參數
        public StatusType Status { get; private set; }

        // 模型上的 Animator(TestActor1 等模型 prefab 自帶,controller 指向 Configs/AnimatorController);
        // 模型非同步載入,參數由 _Step 每幀套用,晚到也會在下一幀接上
        Animator _animator;
        static readonly int _AnimSpeed = Animator.StringToHash("speed");
        static readonly int _AnimStatus = Animator.StringToHash("status");

        IActor _Actor;

        void Awake()
        {
            // 全域 dispatcher:GameObject inactive 時仍會執行,隱藏期間照常取樣定位
            Observable.EveryUpdate().Subscribe(_ => _Step()).AddTo(this);
        }

        // 快照 ghost 資料;Unsupply 後 ghost 失效,之後不再讀取
        public void Setup(IActor actor, WorldTimeHandler worldTime)
        {
            _Actor = actor;
            ActorId = actor.ActorId;
            DisplayName.text = actor.DisplayName;
            WorldTime = worldTime;

            // 事件 replay 需一個網路往返才到,期間沒有位置資訊,先隱藏避免閃現在原點
            gameObject.SetActive(false);
            _MoveFirst(actor);

            var statusObs = UniRx.Observable.FromEvent<StatusType>(h => actor.StatusEvent += h, h => actor.StatusEvent -= h);
            statusObs.Subscribe(s => Status = s).AddTo(this);
        }

        private void _MoveFirst(IActor actor)
        {
            var obs = from moveInfo in UniRx.Observable.FromEvent<MoveInfo>(h => actor.MoveEvent += h, h => actor.MoveEvent -= h).Take(1)
                      select moveInfo;
            obs.Subscribe(_OnFirstMoveEvent).AddTo(this);
        }

        private void _OnFirstMoveEvent(MoveInfo info)
        {
            // StartTicks 是伺服器時間戳,先讓時鐘有機會往前校正再取樣
            WorldTime.ObserveServerTicks(info.StartTicks);
            _MoveInfo = info;
            _Step();
            gameObject.SetActive(true);

            _Move(_Actor);
        }

        private void _Move(IActor actor)
        {
            var obs = 
                        from moveInfo in UniRx.Observable.FromEvent<MoveInfo>(h=> actor.MoveEvent += h, h => actor.MoveEvent -= h)
                      select moveInfo;
            obs.Subscribe(_OnMoveEvent).AddTo(this);
        }

        private void _OnMoveEvent(MoveInfo moveInfo)
        {
            WorldTime.ObserveServerTicks(moveInfo.StartTicks);
            _MoveInfo = moveInfo;
        }

        // 以 world time 對 MoveInfo 取樣位置與朝向(MoveSampler 與伺服器共用同一份公式);
        // 座標為 XZ 平面,Y 維持現值。駐留(Speed==0)取樣恆為原點,停止與移動共用同一條邏輯。
        private void _Step()
        {
            if (!_MoveInfo.HasValue || WorldTime == null)
                return;

            var info = _MoveInfo.Value;

            // client 時間可能落後 StartTicks(延遲/尚未收到 tick),clamp 為 0 站在起點等時間追上
            var elapsed = (WorldTime.CurrentTime.Ticks - info.StartTicks) / (double)TimeSpan.TicksPerSecond;
            if (elapsed < 0)
                elapsed = 0;

            MoveSampler.Sample(info, elapsed, out var position, out var facing);
            Target.position = new Vector3(position.x, Target.position.y, position.y);
            Target.rotation = Quaternion.LookRotation(new Vector3(facing.x, 0f, facing.y), Vector3.up);

            // 動作驅動:speed 直接用伺服器線速度(>0 走路、0 駐留),status 切換冒險/戰鬥動作組
            if (_animator != null)
            {
                _animator.SetFloat(_AnimSpeed, info.Speed);
                _animator.SetInteger(_AnimStatus, (int)Status);
            }
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
                    return;
                }
                _animator = op.Result.GetComponentInChildren<Animator>();
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
