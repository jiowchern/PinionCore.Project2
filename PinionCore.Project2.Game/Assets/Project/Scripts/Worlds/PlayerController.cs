using System;
using PinionCore.Project2.Shared;
using PinionCore.Remote;
using UnityEngine;

namespace PinionCore.Project2.Worlds
{
    /// <summary>
    /// 單一 Player 的協議曝光面 + 角色狀態機(GameProject1 Play 模式):
    /// 以 ICharactor(IPlayer/IActor/IMoveable)對外供應,成員全數委派給 Player(純模擬核心);
    /// 狀態決定曝光給 client 的能力介面,轉換為 world 內部直接呼叫、不過傳輸協議,於下一次 Update 生效。
    /// </summary>
    internal class PlayerController : ICharactor
    {
        public readonly Player Player;

        readonly PinionCore.Utility.StatusMachine _StatusMachine;

        // 視野內角色(含自己);由 World 的 Sight 依「距離 + 地形遮蔽」判定增刪,
        // Enter/Leave 時由 World 直接增刪(self 成員資格也由 World 管)。
        readonly Depot<PlayerController> _VisibleActors;
        readonly Notifier<IActor> _ActorsNotifier;
        Notifier<IActor> IPlayer.Actors => _ActorsNotifier;

        // 供 World 增刪可見角色
        internal Depot<PlayerController> VisibleActors => _VisibleActors;

        // 可移動能力:由角色狀態機(Conscious/Unconscious)控制供應,
        // supply = client 可移動,unsupply = 能力收回(秒級,如無意識)。
        // 動作進行中的拒收(毫秒級)仍走 Move/Stop 的動作閘,兩者是不同時間尺度的閘。
        readonly Depot<PlayerController> _Moveables;
        readonly Notifier<IMoveable> _MoveablesNotifier;
        Notifier<IMoveable> IPlayer.Moveable => _MoveablesNotifier;

        // 供狀態增刪供應(同 VisibleActors 模式)
        internal Depot<PlayerController> Moveables => _Moveables;

        // 冒險/戰鬥能力:由 Conscious 內的 Adventure/Battle 子狀態互斥供應,
        // 狀態類(AdventureStatus/BattleStatus)自身即協議實作,Enter 供應自己、Leave 收回。
        readonly Depot<IAdventure> _Adventures;
        readonly Notifier<IAdventure> _AdventuresNotifier;
        Notifier<IAdventure> IPlayer.Adventure => _AdventuresNotifier;
        internal Depot<IAdventure> Adventures => _Adventures;

        readonly Depot<IBattle> _Battles;
        readonly Notifier<IBattle> _BattlesNotifier;
        Notifier<IBattle> IPlayer.Battle => _BattlesNotifier;
        internal Depot<IBattle> Battles => _Battles;

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

        event Action<StatusType> IActor.StatusEvent
        {
            add { Player.StatusEvent += value; }
            remove { Player.StatusEvent -= value; }
        }

        event Action<ActionInfo> IActor.ActionEvent
        {
            add { Player.ActionEvent += value; }
            remove { Player.ActionEvent -= value; }
        }

        Value<bool> IMoveable.Move(Vector2 direction) => Player.Move(direction);

        Value<bool> IMoveable.Stop() => Player.Stop();

        public PlayerController(Player player)
        {
            Player = player;
            _StatusMachine = new PinionCore.Utility.StatusMachine();
            _VisibleActors = new Depot<PlayerController>();
            _ActorsNotifier = _VisibleActors.ToNotifier<IActor>();
            _Moveables = new Depot<PlayerController>();
            _MoveablesNotifier = _Moveables.ToNotifier<IMoveable>();
            _Adventures = new Depot<IAdventure>();
            _AdventuresNotifier = _Adventures.ToNotifier<IAdventure>();
            _Battles = new Depot<IBattle>();
            _BattlesNotifier = _Battles.ToNotifier<IBattle>();

            // 進世界即有意識;首次 Update 進入狀態並供應 IMoveable(Notifier 有 replay,晚訂閱安全)
            ToConscious(Statuses.ConsciousStatus.State.Adventure);
        }

        /// <summary>回到有意識:恢復供應 IMoveable 與 Adventure/Battle 子狀態(進入即冒險態)。</summary>
        internal void ToConscious(Statuses.ConsciousStatus.State state)
        {
            var status = new Statuses.ConsciousStatus(this, state);
            status.CastEvent += _ToCast;
            _StatusMachine.Push(status);
        }

        private void _ToCast(ActionType type)
        {
            var status = new Statuses.CastStatus(this, type);
            status.DoneEvent += _ToBattle;
            _StatusMachine.Push(status);
        }

        private void _ToBattle(bool result)
        {
            ToConscious(Statuses.ConsciousStatus.State.Battle);
        }

        /// <summary>進入無意識(僵直/死亡等,未來由戰鬥管線觸發):收回全部能力供應。</summary>
        internal void ToUnconscious()
        {
            _StatusMachine.Push(new Statuses.UnconsciousStatus());
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
