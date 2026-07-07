using PinionCore.NetSync.UniRx;
using PinionCore.Project2.Protocols;
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

        PinionCore.Remote.Depot<PinionCore.Project2.Protocols.IActor> _Actors;
        
        Remote.Notifier<IActor> _ActorsNotifier;
        Remote.Notifier<IActor> IGame.Actors => _ActorsNotifier;

        PinionCore.Remote.Depot<PinionCore.Project2.Protocols.IPlayer> _Players;
        Remote.Notifier<IPlayer> _PlayersNotifier;        
        Remote.Notifier<IPlayer> IGame.Players => _PlayersNotifier;

        public event System.Action DoneEvent;
        public UserGame(ISessionBinder binder, INotifierQueryable worldNotifer, ActorInfo actor)
        {
            _Players = new PinionCore.Remote.Depot<PinionCore.Project2.Protocols.IPlayer>();
            _PlayersNotifier = _Players.ToNotifier<IPlayer>();

            _Actors = new PinionCore.Remote.Depot<PinionCore.Project2.Protocols.IActor>();
            _ActorsNotifier = _Actors.ToNotifier<IActor>();
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

            var actorsAddObs = world.Players.SupplyEvent(); 
            var disposableActorsAddObs = actorsAddObs.Subscribe(_Actors.Items.Add);
            _DisposeHandlers.Add(() => disposableActorsAddObs.Dispose());

            var actorsRemoveObs = world.Players.UnsupplyEvent();
            var disposableActorsRemoveObs = actorsRemoveObs.Subscribe((p) => _Actors.Items.Remove(p));
            _DisposeHandlers.Add(() => disposableActorsRemoveObs.Dispose());

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