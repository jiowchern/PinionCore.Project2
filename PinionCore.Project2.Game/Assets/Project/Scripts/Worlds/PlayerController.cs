using System;
using PinionCore.Project2.Shared;
using PinionCore.Project2.Worlds.Statuses;
using PinionCore.Remote;

namespace PinionCore.Project2.Worlds
{
    /// <summary>
    /// 單一 Player 的協議曝光面 + 控制狀態機:
    /// 以 ICharacter(IPlayer/IActor)對外供應,成員全數委派給 Player(純模擬核心);
    /// 控制能力(IControllable)由 ControllerStatus 依轉移表逐狀態供應(每狀態一顆 soul),
    /// 轉換為 world 內部直接呼叫、不過傳輸協議,於下一次 Update 生效。
    /// </summary>
    internal class PlayerController : ICharacter
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

        // 控制能力:控制狀態機供應,同一時間至多一顆(有意識時恆有一顆;
        // 無意識 = 不供應,client 以 Unsupply 得知能力收回)。
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

        public PlayerController(Player player, StandardTransitionProvider transitionProvider)
        {
            Player = player;
            _TransitionProvider = transitionProvider;
            _StatusMachine = new PinionCore.Utility.StatusMachine();
            _VisibleActors = new Depot<PlayerController>();
            _ActorsNotifier = _VisibleActors.ToNotifier<IActor>();
            _Controllers = new Depot<IControllable>();
            _ControllableNotifier = _Controllers.ToNotifier<IControllable>();

            // 進世界即有意識;首次 Update 進入狀態並供應 IControllable(Notifier 有 replay,晚訂閱安全)
            ToConscious(ActionType.AdventureIdle);
        }

        void _ToController(Transition transition, UnityEngine.Vector2 direction)
        {
            var status = new Statuses.ControllerStatus(Player, transition, _Controllers, direction);
            status.NextEvent += _OnNext;
            _StatusMachine.Push(status);
        }

        void _OnNext(ActionType type, UnityEngine.Vector2 direction)
        {
            _ToController(_TransitionProvider.Transitions[type], direction);
        }

        /// <summary>回到有意識:進入該表現狀態的 idle 控制狀態(恢復供應 IControllable)。</summary>
        internal void ToConscious(ActionType type)
        {
            
            _ToController(_TransitionProvider.Transitions[type], UnityEngine.Vector2.zero);
        }

        

        /// <summary>由 World.Update 每幀驅動:先推進狀態機(能力供應開關),再讓 Player 投影權威狀態。</summary>
        internal void Update()
        {
            _StatusMachine.Update();
            Player.Update();
        }

        /// <summary>離開世界:結束當前狀態(Leave 收回能力供應),讓 Unsupply 先於根解綁送達。</summary>
        internal void Shutdown()
        {
            _StatusMachine.Termination();
        }
    }
}
