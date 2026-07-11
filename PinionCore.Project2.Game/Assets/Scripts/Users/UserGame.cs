using PinionCore.NetSync.UniRx;
using PinionCore.Project2.Shared;
using PinionCore.Remote;
using PinionCore.Utility;
using System;
using System.Linq;
using System.Reflection;
using UniRx;
namespace PinionCore.Project2.Users
{
    internal class UserGame : PinionCore.Utility.IStatus ,IGame
    {
        private readonly ISessionBinder _Binder;
        private readonly INotifierQueryable _WorldNotifer;
        private readonly ActorInfo _ActorInfo;

        readonly System.Collections.Generic.List< System.Action > _DisposeHandlers;

        Property<string> _WorldName;
        Property<string> IGame.WorldName => _WorldName;

        PinionCore.Remote.Depot<PinionCore.Project2.Shared.ICharactor> _Players;
        Remote.Notifier<ICharactor> _PlayersNotifier;        
        Remote.Notifier<ICharactor> IGame.Players => _PlayersNotifier;

        public event System.Action DoneEvent;
        public UserGame(ISessionBinder binder, INotifierQueryable worldNotifer, ActorInfo actor)
        {
            _Players = new PinionCore.Remote.Depot<PinionCore.Project2.Shared.ICharactor>();
            _PlayersNotifier = _Players.ToNotifier<ICharactor>();

            _WorldName = new Property<string>(string.Empty);
            _DisposeHandlers = new System.Collections.Generic.List<System.Action>();            
            this._Binder = binder;
            this._WorldNotifer = worldNotifer;
            this._ActorInfo = actor;
        }

        void IStatus.Enter()
        {
            var obs = from uni in _WorldNotifer.QueryNotifier<IUniverse>().SupplyEvent()
                      from worldId in uni.QueryWorld("Test1").RemoteValue()
                      from world in uni.WorldNotifier.SupplyEvent().Where(w => w.Id == worldId).Take(1)
                      from actorId in world.Enter(_ActorInfo).RemoteValue()                      
                      select new {world, actorId };

            IDisposable disposable = obs.Subscribe(result => _Join(result.world, result.actorId));            


            _DisposeHandlers.Add(() => {
                disposable.Dispose();
            });

        }
         
        private void _Join(IWorld world,Guid actorId)
        {
            var gameSoul = _Binder.Bind<IGame>(this);
            _DisposeHandlers.Add(() => _Binder.Unbind(gameSoul));

            var viewSoul = _Binder.Bind<IView>(world);
            _DisposeHandlers.Add(() => _Binder.Unbind(viewSoul));

            var playersAddObs = world.Players.SupplyEvent().Where(p => p.ActorId == actorId).Take(1);
            var disposablePlayersAddObs = playersAddObs.Subscribe(_Players.Items.Add);
            _DisposeHandlers.Add(() => disposablePlayersAddObs.Dispose());

            var playersRemoveObs = world.Players.UnsupplyEvent().Where(p => p.ActorId == actorId).Take(1);
            var disposablePlayersRemoveObs = playersRemoveObs.Subscribe((p) => _Players.Items.Remove(p));
            _DisposeHandlers.Add(() => disposablePlayersRemoveObs.Dispose());

            // IActor 供應改由 ICharactor.Actors 承載:綁給 client 的 ICharactor ghost
            // 其 Actors 屬性由框架遞迴綁定自動轉發,User 端不需再手動搬運。

            _DisposeHandlers.Add(() => world.Leave(actorId).RemoteValue().Subscribe());
        }

       

        void IStatus.Leave()
        {

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