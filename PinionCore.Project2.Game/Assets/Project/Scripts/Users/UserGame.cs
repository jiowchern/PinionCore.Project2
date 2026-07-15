using PinionCore.NetSync.UniRx;
using PinionCore.Project2.Shared;
using PinionCore.Remote;
using PinionCore.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UniRx;
namespace PinionCore.Project2.Users
{
    
    internal class UserGame : PinionCore.Utility.IStatus ,IGame
    {
        readonly System.Collections.Generic.ICollection<IGame> _Games;
        private readonly INotifierQueryable _WorldNotifer;
        private readonly ActorInfo _ActorInfo;

        readonly System.Collections.Generic.List< System.Action > _DisposeHandlers;

        readonly Property<string> _WorldName;
        Property<string> IGame.WorldName => _WorldName;

        readonly PinionCore.Remote.Depot<PinionCore.Project2.Shared.ICharactor> _Charactors;
        readonly Remote.Notifier<IPlayer> _PlayersNotifier;
        Remote.Notifier<IPlayer> IGame.Player => _PlayersNotifier;

        // IMoveable/IAdventure/IBattle 供應已下沉 world:由 IPlayer 的對應 Notifier 承載,
        // world 端角色狀態機(PlayerController)控制開關,user 零經手

        readonly Depot<IView> _Views;
        readonly Remote.Notifier<IView> _ViewsNotifier;
        Remote.Notifier<IView> IGame.View => _ViewsNotifier;

        public event System.Action DoneEvent;

        
        public UserGame(ICollection<IGame> games, INotifierQueryable worldNotifer, ActorInfo actor)
        {
            _Games = games;            
            _Charactors = new PinionCore.Remote.Depot<PinionCore.Project2.Shared.ICharactor>();
            _PlayersNotifier = _Charactors.ToNotifier<IPlayer>();

            _WorldName = new Property<string>(string.Empty);
            _DisposeHandlers = new System.Collections.Generic.List<System.Action>();

            this._WorldNotifer = worldNotifer;
            this._ActorInfo = actor;
            _Views = new PinionCore.Remote.Depot<IView>();
            _ViewsNotifier = new PinionCore.Remote.Notifier<IView>(_Views);
        }

        void IStatus.Enter()
        {
            PinionCore.Utility.Log.Instance.WriteInfo("UserGame.Enter");
            var actorId = Guid.NewGuid();
            var obs = from uni in _WorldNotifer.QueryNotifier<IUniverse>().SupplyEvent()
                      from worldId in uni.QueryWorld("Test1").RemoteValue()
                      from world in uni.WorldNotifier.SupplyEvent().Where(w => w.Id == worldId).Take(1)
                      select world;

            IDisposable disposable = obs.Subscribe(world => _EnterWorld(world, actorId));

            _DisposeHandlers.Add(() => {
                disposable.Dispose();
            });

        }

        private void _EnterWorld(IWorld world, Guid actorId)
        {
            // actorId 由 user 端先產生:Leave 在送出 Enter 的同一刻註冊進 _DisposeHandlers,
            // 即使 Enter 回應未消化前 session 就收尾,也保證送出 world.Leave(補償退場)。
            // Enter RPC 先送、Leave RPC 收尾才送,伺服器依序處理不會漏清。
            _DisposeHandlers.Add(() =>
            {
                PinionCore.Utility.Log.Instance.WriteInfo($"UserGame world.Leave send actor:{actorId}");
                world.Leave(actorId).RemoteValue().Subscribe(
                    r => PinionCore.Utility.Log.Instance.WriteInfo($"UserGame world.Leave result:{r}"),
                    e => PinionCore.Utility.Log.Instance.WriteInfo($"UserGame world.Leave error:{e.Message}"));
            });

            // 回應訂閱掛進 _DisposeHandlers:session 收尾後 _Join 不可能再跑。
            IDisposable disposable = world.Enter(actorId, _ActorInfo).RemoteValue().Subscribe(ok =>
            {
                if (ok)
                    _Join(world, actorId);
                else
                    PinionCore.Utility.Log.Instance.WriteInfo($"UserGame world.Enter rejected actor:{actorId}");
            });
            _DisposeHandlers.Add(() => disposable.Dispose());
        }

        private void _Join(IWorld world,Guid actorId)
        {
            PinionCore.Utility.Log.Instance.WriteInfo($"UserGame.Join actor:{actorId}");
            // IGame.Players(Notifier<IPlayer>)經框架遞迴綁定供應 IPlayer ghost 給 client;
            // 沒有這條 Bind,client 端不會收到任何 IPlayer。
            _Games.Add(this);
            _DisposeHandlers.Add(() => { _Games.Remove(this); });

            _Views.Items.Add(world);
            _DisposeHandlers.Add(() => _Views.Items.Remove(world));

            var playersAddObs = world.Players.SupplyEvent().Where(p => p.ActorId == actorId).Take(1);
            var disposablePlayersAddObs = playersAddObs.Subscribe(_Start);
            _DisposeHandlers.Add(() => disposablePlayersAddObs.Dispose());

            var playersRemoveObs = world.Players.UnsupplyEvent().Where(p => p.ActorId == actorId).Take(1);
            var disposablePlayersRemoveObs = playersRemoveObs.Subscribe(_End);
            _DisposeHandlers.Add(() => disposablePlayersRemoveObs.Dispose());

            // IActor 供應由 IPlayer.Actors 承載:綁給 client 的 IPlayer ghost
            // 其 Actors 屬性由框架遞迴綁定自動轉發,User 端不需再手動搬運。
            // world.Leave 的補償退場已在 _EnterWorld 註冊,這裡不再重複。
        }

        private void _End(ICharactor charactor)
        {
            _Charactors.Items.Remove(charactor);
            // 玩家被 world 移除(Unsupply)→ 通知 User 回到 verify + Roster.Unregister。
            // 斷線收尾不會走到這:dispose 時 UnsupplyEvent 訂閱已先解除。
            DoneEvent?.Invoke();
        }



        private void _Start(ICharactor charactor)
        {
            // 角色流程(意識/冒險/戰鬥)已全數下沉 world 端狀態機;
            // user 只負責把 ICharactor ghost 供應給 client(IPlayer)
            _Charactors.Items.Add(charactor);
        }

        void IStatus.Leave()
        {
            
            PinionCore.Utility.Log.Instance.WriteInfo($"UserGame.Leave handlers:{_DisposeHandlers.Count}");
            foreach (var handler in _DisposeHandlers)
            {
                handler();
            }
            _DisposeHandlers.Clear();
        }

        void IStatus.Update()
        {
            
        }
    }
}