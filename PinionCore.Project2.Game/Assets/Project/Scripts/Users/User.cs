using PinionCore.Project2.Shared;
using PinionCore.Project2.Shared.Users;
using PinionCore.Remote;
using System;
using UnityEngine;
namespace PinionCore.Project2.Users
{
    public class User : IDisposable , IUserEntry
    {
        private readonly ISessionBinder _Binder;
        private readonly INotifierQueryable _WorldNotifer;
        readonly Roster _Roster;

        readonly PinionCore.Utility.StatusMachine _StatusMachine;

        readonly Depot<IVerifiable> _Verifiables;

        readonly Notifier<IVerifiable> _VerifiablesNotifier;
        Notifier<IVerifiable> IUserEntry.Verifiables => _VerifiablesNotifier;


        readonly Depot<IGame> _Games;
        readonly Notifier<IGame> _GamesNotifier;
        Notifier<IGame> IUserEntry.Games => _GamesNotifier;

        readonly System.Collections.Generic.List<System.Action> _DisposeHandlers;

        public User(ISessionBinder binder, INotifierQueryable worldNotifier)
        {
            _Roster = new Roster();
            this._Binder = binder;
            this._WorldNotifer = worldNotifier;
            this._StatusMachine = new PinionCore.Utility.StatusMachine();

            _Verifiables = new PinionCore.Remote.Depot<IVerifiable>();
            _VerifiablesNotifier = new PinionCore.Remote.Notifier<IVerifiable>(_Verifiables);

            _Games = new PinionCore.Remote.Depot<IGame>();
            _GamesNotifier = new PinionCore.Remote.Notifier<IGame>(_Games);


            _DisposeHandlers = new System.Collections.Generic.List<System.Action>();
            var soul = _Binder.Bind<IUserEntry>(this);
            _DisposeHandlers.Add(() => _Binder.Unbind(soul));
            _ToVerify();
            
            
        }

        private void _ToVerify()
        {
            var status = new UserVerifier(_Verifiables, _Roster);
            status.OnVerified += (actor) =>
            {
                _ToGame(actor);
            };
            _StatusMachine.Push(status);
        }

        private void _ToGame(Shared.ActorInfo actor)
        {
            var status = new UserGame(_Games, _WorldNotifer, actor);
            status.DoneEvent += () =>
            {
                _Roster.Unregister(actor.DisplayName);
                _ToVerify();
            };
            _StatusMachine.Push(status);
        }

        public void Update()
        {
            _StatusMachine.Update();
        }

        public void Dispose()
        {
            _StatusMachine.Termination();

            foreach (var handler in _DisposeHandlers)
            {
                handler();
            }
        }
    }

}