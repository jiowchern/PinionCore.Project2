
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

        // 自帶位移動作:來自 IActor.ActionEvent(訂閱時必有 replay)。位移仍由 MoveEvent 驅動
        //(伺服器把動作播成分段 MoveInfo,殼照常取樣就描出 root 路徑);這裡只負責表現:
        // 動作期間凍結旋轉(段 Facing 是速度方向,側移/滑行不該讓角色轉身)、以時間偏移播動畫。
        ActionInfo _actionInfo;
        Quaternion _heldRotation;
        bool _rotationHeld;
        // 已套用動畫的 ActionInfo(Action + StartTicks):伺服器覆蓋動作(僵直/死亡)時
        // StartTicks 必變,據此重新 CrossFade;不能用單一 bool
        ActionType _appliedAction = ActionType.None;
        long _appliedActionTicks = long.MinValue;

        // ActionType → Animator state 名稱(動作 state 只靠 code CrossFade 進入,不掛參數轉換)
        static readonly System.Collections.Generic.Dictionary<ActionType, string> _ActionStates =
            new System.Collections.Generic.Dictionary<ActionType, string>
            {
                { ActionType.Attack, "Battle Attack" },
            };

        // 供輸入層(PlayerAttackHandler)觀察動作進行狀態;None = 結束(解鎖補送 Move 的訊號)
        public event Action<ActionInfo> ActionEvent;

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

            var actionObs = UniRx.Observable.FromEvent<ActionInfo>(h => actor.ActionEvent += h, h => actor.ActionEvent -= h);
            actionObs.Subscribe(_OnActionEvent).AddTo(this);
        }

        private void _OnActionEvent(ActionInfo info)
        {
            // StartTicks 也是對時來源(與 MoveEvent 同款):讓晚加入者的動畫偏移正確
            WorldTime.ObserveServerTicks(info.StartTicks);
            _actionInfo = info;
            if (info.Action == ActionType.None)
            {
                // None 是解除旋轉凍結的唯一權威訊號(不靠本地計時);
                // 下一幀 _Step 會以終停 MoveInfo 的 Facing(伺服器恢復的視覺朝向)重新定向
                _rotationHeld = false;
                _appliedAction = ActionType.None;
                _appliedActionTicks = long.MinValue;
            }
            ActionEvent?.Invoke(info);
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

            var actionActive = _actionInfo.Action != ActionType.None;
            if (actionActive)
            {
                // 凍結旋轉:動作段的 Facing 是速度方向(側移/撞牆滑行),不是視覺朝向;
                // 首次進入動作時捕捉當下旋轉(動作開始前最後一次套用的朝向)並保持到 None
                if (!_rotationHeld)
                {
                    _heldRotation = Target.rotation;
                    _rotationHeld = true;
                }
                Target.rotation = _heldRotation;
            }
            else
            {
                Target.rotation = Quaternion.LookRotation(new Vector3(facing.x, 0f, facing.y), Vector3.up);
            }

            // 動作驅動:speed 直接用伺服器線速度(>0 走路、0 駐留),status 切換冒險/戰鬥動作組
            if (_animator != null)
            {
                _animator.SetFloat(_AnimSpeed, info.Speed);
                _animator.SetInteger(_AnimStatus, (int)Status);

                // 動作動畫:每顆 ActionInfo 只 CrossFade 一次,以 world time 差當起播偏移
                //(晚加入者/模型晚載入都在此收斂到正確的動畫時間點)
                if (actionActive &&
                    (_appliedAction != _actionInfo.Action || _appliedActionTicks != _actionInfo.StartTicks))
                {
                    if (_ActionStates.TryGetValue(_actionInfo.Action, out var stateName))
                    {
                        var offset = (float)((WorldTime.CurrentTime.Ticks - _actionInfo.StartTicks) / (double)TimeSpan.TicksPerSecond);
                        if (offset < 0f)
                            offset = 0f;
                        _animator.CrossFadeInFixedTime(stateName, 0.1f, 0, offset);
                    }
                    _appliedAction = _actionInfo.Action;
                    _appliedActionTicks = _actionInfo.StartTicks;
                }
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
                // 位置權威在伺服器(Target 由 MoveInfo 取樣驅動),模型只是 Target 的子物件;
                // FBX 匯入的 Animator 預設 applyRootMotion = true(unitychan 等),
                // 不關掉的話帶位移的 clip 會把 root motion 疊進子物件 localPosition,
                // 模型永久漂離殼(伺服器推一次、模型自己再走一次)。一律強制關閉。
                if (_animator != null)
                    _animator.applyRootMotion = false;
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
