using System;
using PinionCore.Project2.Shared;
using PinionCore.Project2.Worlds.Statuses;
using PinionCore.Remote;
using UnityEngine;

namespace PinionCore.Project2.Worlds
{
    /// <summary>
    /// 單一 Player 的協議曝光面 + 控制狀態機:
    /// 以 ICharacter(IPlayer/IActor)對外供應,成員全數委派給 Player(純模擬核心);
    /// 控制能力(IControllable)由自身承載(與 Player 同生命週期的單一 soul):
    /// Play 委派給當前 ControllerStatus(依轉移表換狀態)、狀態轉移經 TransitionEvent 廣播,
    /// 轉換為 world 內部直接呼叫、不過傳輸協議,於下一次 Update 生效。
    /// </summary>
    internal class PlayerController : ICharacter, IControllable 
    {
        public readonly Player Player;

        readonly PinionCore.Utility.StatusMachine _StatusMachine;
        readonly StandardTransitionProvider _TransitionProvider;

        // 視野內角色(含自己);由 World 的 Sight 依「距離 + 地形遮蔽」判定增刪,
        // Enter/Leave 時由 World 直接增刪(self 成員資格也由 World 管)。
        readonly Depot<PlayerController> _VisibleActors;
        readonly Notifier<IActor> _ActorsNotifier;
        Notifier<IActor> IPlayer.Actors => _ActorsNotifier;

        // 供 World 增刪可見角色
        internal Depot<PlayerController> VisibleActors => _VisibleActors;

        // 控制能力:自身即唯一一顆 soul,進世界供應、Shutdown 收回
        //(client 以 Unsupply 得知能力收回)。
        readonly Depot<IControllable> _Controllers;
        readonly Notifier<IControllable> _ControllableNotifier;
        Notifier<IControllable> IPlayer.Controllable => _ControllableNotifier;

        // IIdentity / IActor 屬性:委派給 Player
        public Property<Guid> ActorId => Player.ActorId;
        public Property<string> DisplayName => Player.DisplayName;
        public Property<string> ModelName => Player.ModelName;

        // IActor 事件:轉發 Player 的公開事件,「訂閱即 replay」由 Player 的存取子觸發
        event Action<MoveInfo> IActor.MoveEvent
        {
            add { Player.MoveEvent += value; }
            remove { Player.MoveEvent -= value; }
        }

        event Action<ActionInfo> IActor.ActionEvent
        {
            add { Player.ActionEvent += value; }
            remove { Player.ActionEvent -= value; }
        }

        // 當前控制狀態(Play 的委派對象;_ToController 同步切換,建構後恆非 null)
        Statuses.ControllerStatus _ControllerStatus;

        // 狀態轉移廣播:soul 端每個 client 訂閱都會走一次 add,
        // add 內立即回放當前 Transition,晚訂閱也能取得當下狀態
        event Action<Transition> _TransitionEvent;
        event Action<Transition> IControllable.TransitionEvent
        {
            add
            {
                _TransitionEvent += value;
                value(_ControllerStatus.Transition);
            }

            remove
            {
                _TransitionEvent -= value;
            }
        }

        public PlayerController(Player player, StandardTransitionProvider transitionProvider)
        {
            Player = player;
            _TransitionProvider = transitionProvider;
            _StatusMachine = new PinionCore.Utility.StatusMachine();
            _VisibleActors = new Depot<PlayerController>();
            _ActorsNotifier = _VisibleActors.ToNotifier<IActor>();
            _Controllers = new Depot<IControllable>();
            _ControllableNotifier = _Controllers.ToNotifier<IControllable>();

            // 進世界即有意識;先建立初始控制狀態(_ControllerStatus 就緒)再供應自己
            //(Notifier 有 replay,晚訂閱安全;狀態於首次 Update 進入)
            ToConscious(ActionType.AdventureIdle);
            _Controllers.Items.Add(this);
        }

        /// <summary>
        /// world 內部的轉移通知(無 replay、不過線):每次 _ToController 同步 raise。
        /// GrabResolver 據此觀察配對雙方的節點變化;不可改用 IControllable.TransitionEvent ——
        /// 其 add 即回放當前 Transition,訂閱當下會誤觸「離開 grab 家族」判定。
        /// </summary>
        internal event Action<Transition> TransitionChangedEvent;

        // 當前控制節點(_ToController 同步 swap,讀到的恆為最新意圖;該狀態的 Enter 可能尚未執行)
        internal Transition CurrentTransition => _ControllerStatus.Transition;

        void _ToController(Transition transition, UnityEngine.Vector2 direction)
        {
            var status = new Statuses.ControllerStatus(Player, transition, direction);
            status.NextEvent += _OnNext;
            _ControllerStatus = status;
            _StatusMachine.Push(status);
            TransitionChangedEvent?.Invoke(transition);
            _TransitionEvent?.Invoke(transition);
        }

        void _OnNext(ActionType type, UnityEngine.Vector2 direction)
        {
            _ToController(_TransitionProvider.Transitions[type], direction);
        }

        /// <summary>回到有意識:進入該表現狀態的 idle 控制狀態。</summary>
        internal void ToConscious(ActionType type)
        {
            _ToController(_TransitionProvider.Transitions[type], UnityEngine.Vector2.zero);
        }

        /// <summary>
        /// 伺服器內部強制轉移到指定節點(GrabResolver 的配對建立/鏡射/釋放等跨角色編排用);
        /// direction 傳入該節點動作的朝向基底(零向量 = 沿用既有規則)。
        /// </summary>
        internal void ForceTransition(ActionType type, UnityEngine.Vector2 direction)
        {
            _ToController(_TransitionProvider.Transitions[type], direction);
        }


        /// <summary>由 World.Update 每幀驅動:先推進狀態機(能力供應開關),再讓 Player 投影權威狀態。</summary>
        internal void Update()
        {
            _StatusMachine.Update();
            Player.Update();
        }

        /// <summary>離開世界:先收回能力供應(讓 Unsupply 先於根解綁送達),再結束當前狀態。</summary>
        internal void Shutdown()
        {
            _Controllers.Items.Clear();
            _StatusMachine.Termination();
        }

        Value<bool> IControllable.Play(ActionType name, Vector2 direction)
        {
            // _ToController 同步切換 _ControllerStatus:指令一律以最新 Transition 的白名單驗證,
            // 不再有舊「每狀態一顆 soul」時代打在已收回 soul 上被靜默丟棄的空窗
            return _ControllerStatus.Play(name, direction);
        }

        void ICharacter.Damage()
        {
            _ToDamage();
        }

        private void _ToDamage()
        {
            if (_ControllerStatus.Transition.Damage.Action == ActionType.None)
                return;
            _ToController(_TransitionProvider.Transitions[_ControllerStatus.Transition.Damage.Action], UnityEngine.Vector2.zero);
        }
    }
}
