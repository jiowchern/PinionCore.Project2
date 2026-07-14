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

        // Leave 已跑;Enter 回應在這之後才抵達時要走補償退場(見 _EnterWorld)
        bool _Done;

        Property<string> _WorldName;
        Property<string> IGame.WorldName => _WorldName;

        PinionCore.Remote.Depot<PinionCore.Project2.Shared.ICharactor> _Charactors;
        Remote.Notifier<IPlayer> _PlayersNotifier;
        Remote.Notifier<IPlayer> IGame.Players => _PlayersNotifier;

        Remote.Notifier<IMoveable> _MoveablesNotifier;
        Remote.Notifier<IMoveable> IGame.Moveables => _MoveablesNotifier;

        public event System.Action DoneEvent;

        readonly PinionCore.Utility.StatusMachine _StatusMachine;
        public UserGame(ISessionBinder binder, INotifierQueryable worldNotifer, ActorInfo actor)
        {
            _StatusMachine = new StatusMachine();
            _Charactors = new PinionCore.Remote.Depot<PinionCore.Project2.Shared.ICharactor>();
            _PlayersNotifier = _Charactors.ToNotifier<IPlayer>();
            _MoveablesNotifier = _Charactors.ToNotifier<IMoveable>();

            _WorldName = new Property<string>(string.Empty);
            _DisposeHandlers = new System.Collections.Generic.List<System.Action>();            
            this._Binder = binder;
            this._WorldNotifer = worldNotifer;
            this._ActorInfo = actor;
        }

        void IStatus.Enter()
        {
            PinionCore.Utility.Log.Instance.WriteInfo("UserGame.Enter");
            var obs = from uni in _WorldNotifer.QueryNotifier<IUniverse>().SupplyEvent()
                      from worldId in uni.QueryWorld("Test1").RemoteValue()
                      from world in uni.WorldNotifier.SupplyEvent().Where(w => w.Id == worldId).Take(1)
                      select world;

            IDisposable disposable = obs.Subscribe(_EnterWorld);

            _DisposeHandlers.Add(() => {
                disposable.Dispose();
            });

        }

        private void _EnterWorld(IWorld world)
        {
            // Enter 送出後伺服器側就有 actor,回應的消化不能依賴 session 存活:
            // 若回應抵達前 session 已收尾(Leave 已跑),立即補償退場,避免 actor 殘留在 World。
            // 因此這條訂閱不掛進 _DisposeHandlers,由回呼自行了結(RemoteValue 發完一筆即完成)。
            world.Enter(_ActorInfo).RemoteValue().Subscribe(actorId =>
            {
                if (_Done)
                {
                    PinionCore.Utility.Log.Instance.WriteInfo($"UserGame enter-after-leave, compensating leave actor:{actorId}");
                    world.Leave(actorId).RemoteValue().Subscribe();
                    return;
                }
                _Join(world, actorId);
            });
        }

        private void _Join(IWorld world,Guid actorId)
        {
            PinionCore.Utility.Log.Instance.WriteInfo($"UserGame.Join actor:{actorId}");
            // IGame.Players(Notifier<IPlayer>)經框架遞迴綁定供應 IPlayer ghost 給 client;
            // 沒有這條 Bind,client 端不會收到任何 IPlayer。
            var gameSoul = _Binder.Bind<IGame>(this);
            _DisposeHandlers.Add(() => _Binder.Unbind(gameSoul));

            var viewSoul = _Binder.Bind<IView>(world);
            _DisposeHandlers.Add(() => _Binder.Unbind(viewSoul));

            var playersAddObs = world.Players.SupplyEvent().Where(p => p.ActorId == actorId).Take(1);
            var disposablePlayersAddObs = playersAddObs.Subscribe(_Start);
            _DisposeHandlers.Add(() => disposablePlayersAddObs.Dispose());

            var playersRemoveObs = world.Players.UnsupplyEvent().Where(p => p.ActorId == actorId).Take(1);
            var disposablePlayersRemoveObs = playersRemoveObs.Subscribe(_End);
            _DisposeHandlers.Add(() => disposablePlayersRemoveObs.Dispose());

            // IActor 供應由 IPlayer.Actors 承載:綁給 client 的 IPlayer ghost
            // 其 Actors 屬性由框架遞迴綁定自動轉發,User 端不需再手動搬運。

            _DisposeHandlers.Add(() =>
            {
                PinionCore.Utility.Log.Instance.WriteInfo($"UserGame world.Leave send actor:{actorId}");
                world.Leave(actorId).RemoteValue().Subscribe(
                    r => PinionCore.Utility.Log.Instance.WriteInfo($"UserGame world.Leave result:{r}"),
                    e => PinionCore.Utility.Log.Instance.WriteInfo($"UserGame world.Leave error:{e.Message}"));
            });

            
        }

        private void _End(ICharactor charactor)
        {
            _Charactors.Items.Remove(charactor);
            _ToExit();
        }

        private void _ToExit()
        {
            _StatusMachine.Empty();
        }

        private void _Start(ICharactor charactor)
        {
            _Charactors.Items.Add(charactor);

            // todo:尚未實作下面狀態 先通過編譯
            //_ToAdventure(charactor);
        }

        private void _ToAdventure(ICharactor charactor)
        {
            var status = new UserGameAdventure(charactor);
            status.BattleEvent += () => _ToBattle(charactor);
            _StatusMachine.Push(status);
        }

        private void _ToBattle(ICharactor charactor)
        {
            var status = new UserGameBattle(charactor);
            status.AdventureEvent += () => _ToAdventure(charactor)  ;
            _StatusMachine.Push(status);
        }

        void IStatus.Leave()
        {
            _StatusMachine.Termination();
            PinionCore.Utility.Log.Instance.WriteInfo($"UserGame.Leave handlers:{_DisposeHandlers.Count}");
            _Done = true;
            foreach (var handler in _DisposeHandlers)
            {
                handler();
            }
            _DisposeHandlers.Clear();
        }

        void IStatus.Update()
        {
            _StatusMachine.Update();
        }
    }
}