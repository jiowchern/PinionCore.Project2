
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
    public class ActorShell : MonoBehaviour
    {
        public TMPro.TextMeshPro DisplayName;
        public Transform Target;

        // 對應伺服器端的身分,讓外部(鏡頭跟隨、測試)能辨識這個殼屬於哪個 actor
        public System.Guid ActorId { get; private set; }

        // 取樣時間來源:與伺服器同步的 world time,由 ActorProvider 於 Setup 傳入
        public WorldTimeHandler WorldTime { get; private set; }

        // 模型是表現層,由 ActorShell 自行透過 Addressables 載入;
        // 載入失敗或載入中被銷毀都不影響殼(邏輯層)的生命週期
        AsyncOperationHandle<GameObject> _modelHandle;
        bool _destroyed;

        // 位置唯一來源:伺服器的時間戳路徑,每幀以 world time 取樣;
        // 收到第一個 MoveEvent(訂閱時必有 replay)前殼保持隱藏。
        MoveInfo? _MoveInfo;

        // 冒險/戰鬥表現狀態:由最新非 None 動作的 ActionConfig.Stance 查表(StanceEvent 已拆除),
        // 驅動 Animator 的 status 參數;None 或查無 config 時沿用上一個值不閃切
        public StanceType Stance { get; private set; }

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

        // ActionType → Animator state 名:約定 state 名 = enum 名,與 ActionAnimatorGenerator 同一約定
        //(controller 由產生器從 ActorConfig.Actions 重生,不再維護手寫映射表)。
        // 是否凍結旋轉由 ActionConfig.HoldRotation 決定(_HoldRotation):
        // 攻擊類凍結:段 Facing 是速度方向(側移/滑行)不是視覺朝向;
        // 走路/idle 不凍結:Facing 即移動方向,角色照常面向前進方向。
        static string _StateName(ActionType action) => action.ToString();

        // 動作切換唯一入口:寫入 Animator 的 int 參數 ActionType(值 = enum 值,與產生器的
        // AnyState 轉換圖同一約定,編輯器時期改參數即可測試各動作 state),
        // 再 CrossFade 帶起播偏移收斂(晚加入者/本地預測)。仍需 CrossFade 的原因:
        // 參數轉換無法帶起播偏移,且同值重播(連續受擊等 StartTicks 變、Action 不變)參數不變不會觸發;
        // 產生器的 AnyState 轉換 canTransitionToSelf=false,code 已切入的 state 不會被參數轉換重複觸發
        void _SwitchAction(ActionType action, float offsetSeconds)
        {
            _animator.SetInteger(ActionAnimatorParameter.Name, (int)action);
            _animator.CrossFadeInFixedTime(_StateName(action), 0.1f, 0, offsetSeconds);
        }

        // 此角色可播放的動作 config(與伺服器同一份資產,由 ActorProvider 於 Setup 傳入);
        // client 據此以 ActionType 查表取得表現資訊(HoldRotation/Stance/Loop 等),不過線
        ActionConfig[] _actionConfigs;

        // 供輸入層(PlayerActionMenuHandler/PlayerStanceHandler)觀察動作進行狀態;
        // 循環動作(下一狀態的 idle/走路)抵達 = 前一個一次性動作已結束(解鎖補送 Move 的訊號)
        public event Action<ActionInfo> ActionEvent;

        // 模型上的 Animator(TestActor1 等模型 prefab 自帶,controller 指向 Configs/AnimatorController);
        // 模型非同步載入,動畫由 _Step 每幀收斂(CrossFade 去重),晚到也會在下一幀接上。
        // runtime 動作切換走 _SwitchAction(參數 + CrossFade,含本地預測)
        Animator _animator;

        // 預設轉移圖(與伺服器同一份 Shared 類):非循環動作播到 Duration 時
        // 本地先行切到 Next,不等伺服器的下一顆 ActionInfo(省一個 RTT 的表現延遲)
        readonly StandardTransitionProvider _transitions = new StandardTransitionProvider();
        // 已被預測消化的 ActionInfo(Action + StartTicks):權威事件抵達前,
        // 過期的現行動作不得被去重塊當成新事件重播回舊動畫
        ActionType _predictedFromAction = ActionType.None;
        long _predictedFromTicks = long.MinValue;
        // 預測時戳容忍窗:權威 ActionInfo 與預測起點(排程邊界)的可接受差距,
        // 窗內只收養權威時戳不重播;窗外(force 覆蓋/預測錯)照常 CrossFade 糾正
        static readonly long _PredictionToleranceTicks = TimeSpan.TicksPerSecond / 4;

        IActor _Actor;

        void Awake()
        {
            // 全域 dispatcher:GameObject inactive 時仍會執行,隱藏期間照常取樣定位
            Observable.EveryUpdate().Subscribe(_ => _Step()).AddTo(this);
        }

        // 快照 ghost 資料;Unsupply 後 ghost 失效,之後不再讀取。
        // actions 可為 null(找不到 ActorConfig 的降級路徑):表現規則走保守 fallback
        public void Setup(IActor actor, WorldTimeHandler worldTime, ActionConfig[] actions)
        {
            _actionConfigs = actions;
            _Actor = actor;
            ActorId = actor.ActorId;
            DisplayName.text = actor.DisplayName;
            WorldTime = worldTime;

            // 事件 replay 需一個網路往返才到,期間沒有位置資訊,先隱藏避免閃現在原點
            gameObject.SetActive(false);
            _MoveFirst(actor);

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
                // 哨兵防禦:伺服器不再廣播 None(動作播完直接接下一狀態的 ActionInfo),
                // 只剩交棒空窗/首動作前晚訂閱的 replay 會是 None。解除旋轉凍結不依賴此分支:
                // 下一顆 ActionInfo 的 HoldRotation=false 會讓 _Step 的 else 路徑自然解凍
                _rotationHeld = false;
                _appliedAction = ActionType.None;
                _appliedActionTicks = long.MinValue;
            }
            else
            {
                var config = FindAction(info.Action);
                if (config != null)
                    Stance = config.Stance;
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
            // 現行動作已播放的秒數(world time - StartTicks;可為負 = 時間還沒追上)
            var actionElapsed = actionActive
                ? (WorldTime.CurrentTime.Ticks - _actionInfo.StartTicks) / (double)TimeSpan.TicksPerSecond
                : 0.0;

            // 凍結旋轉:讀 ActionConfig.HoldRotation;非循環動作播到 Duration 即預測解凍
            //(伺服器終停 MoveInfo 在邊界 tick 恢復視覺朝向,解凍後立即以它定向,銜接自然)
            if (actionActive && _HoldRotation(_actionInfo.Action, actionElapsed))
            {
                // 首次進入動作時捕捉當下旋轉(動作開始前最後一次套用的朝向)並保持到動作結束
                if (!_rotationHeld)
                {
                    _heldRotation = Target.rotation;
                    _rotationHeld = true;
                }
                Target.rotation = _heldRotation;
            }
            else
            {
                _rotationHeld = false;
                Target.rotation = Quaternion.LookRotation(new Vector3(facing.x, 0f, facing.y), Vector3.up);
            }

            if (_animator != null)
            {
                // 動作動畫:每顆 ActionInfo 只 CrossFade 一次,以 world time 差當起播偏移
                //(晚加入者/模型晚載入都在此收斂到正確的動畫時間點)。
                // 走路是 loop state:fixedTime 偏移超過 clip 長度由 Animator 自行取模
                //(normalizedTime 含圈數、視覺取小數),晚加入者也收斂;float 精度在
                // 小時級連續走路後誤差約毫秒級,可接受(每次重新起走 StartTicks 都會刷新)。
                // 已被預測消化的那顆不重播(過期動作不得蓋掉預測切出去的 Next)
                var consumedByPrediction = _predictedFromAction == _actionInfo.Action &&
                    _predictedFromTicks == _actionInfo.StartTicks;
                if (actionActive && !consumedByPrediction &&
                    (_appliedAction != _actionInfo.Action || _appliedActionTicks != _actionInfo.StartTicks))
                {
                    // 預測命中:權威 ActionInfo 與預測只差時戳(排程邊界 vs 下一次 Update 取樣),
                    // 收養權威時戳即可,不重播避免自己疊自己
                    var predicted = _appliedAction == _actionInfo.Action &&
                        Math.Abs(_actionInfo.StartTicks - _appliedActionTicks) < _PredictionToleranceTicks;
                    if (!predicted)
                    {
                        var offset = (float)actionElapsed;
                        if (offset < 0f)
                            offset = 0f;
                        _SwitchAction(_actionInfo.Action, offset);
                    }
                    _appliedAction = _actionInfo.Action;
                    _appliedActionTicks = _actionInfo.StartTicks;
                }

                // 本地預測:非循環動作播到 Duration 即先行 CrossFade 到轉移圖的 Next,
                // 不等伺服器(權威 ActionInfo 約一個 RTT 後抵達,由上方時戳收養/糾正)
                if (actionActive)
                {
                    var config = FindAction(_actionInfo.Action);
                    if (config != null && !config.Loop && actionElapsed >= config.Duration &&
                        _transitions.Transitions.TryGetValue(_actionInfo.Action, out var transition) &&
                        _appliedAction != transition.Next.Action)
                    {
                        _SwitchAction(transition.Next.Action, (float)(actionElapsed - config.Duration));
                        _appliedAction = transition.Next.Action;
                        // 預測起點 = 排程邊界(StartTicks + Duration)+ 接招窗:
                        // 伺服器窗內續留原狀態,無人接招時 Next 的權威 ActionInfo 於窗到期才發,
                        // 收養時戳須含窗才對得上(窗內接招是不同動作,不走收養、照常 CrossFade)
                        _appliedActionTicks = _actionInfo.StartTicks + (long)((config.Duration + config.ChainWindow) * TimeSpan.TicksPerSecond);
                        _predictedFromAction = _actionInfo.Action;
                        _predictedFromTicks = _actionInfo.StartTicks;
                    }
                }
            }
        }

        /// <summary>以 ActionType 查此角色的 ActionConfig(與伺服器同一份資產);查無回 null。</summary>
        public ActionConfig FindAction(ActionType action)
        {
            if (_actionConfigs == null)
                return null;
            foreach (var config in _actionConfigs)
            {
                if (config != null && config.Action == action)
                    return config;
            }
            return null;
        }

        // 動作是否凍結旋轉:讀 ActionConfig.HoldRotation;查無 config(降級/未知動作)保守凍結
        //(降級角色無模型無 animator,只剩空殼 Target 的旋轉,凍結無視覺影響)。
        // 非循環動作播到 Duration 即預測解凍,與動畫預測同步,不等權威事件
        bool _HoldRotation(ActionType action, double elapsedSeconds)
        {
            var config = FindAction(action);
            if (config == null)
                return true;
            if (!config.Loop && elapsedSeconds >= config.Duration)
                return false;
            return config.HoldRotation;
        }

        public void LoadModel(AssetReferenceGameObject modelPrefab)
        {
            if (modelPrefab == null || !modelPrefab.RuntimeKeyIsValid())
            {
                Debug.LogError($"未設定有效的 ModelPrefab (Addressable), ActorShell={name}");
                return;
            }

            _modelHandle = modelPrefab.InstantiateAsync(Target);
            _modelHandle.Completed += op =>
            {
                if (op.Status != AsyncOperationStatus.Succeeded)
                {
                    Debug.LogError($"ActorShell 模型載入失敗 (key={modelPrefab.RuntimeKey})");
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
