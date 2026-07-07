using PinionCore.Remote;
using System;
using UnityEngine;
namespace PinionCore.Project2.Users
{
    public class User : IDisposable
    {
        private readonly ISessionBinder _Binder;
        private readonly INotifierQueryable _WorldNotifer;
        readonly Roster _Roster;

        readonly PinionCore.Utility.StatusMachine _StatusMachine;

        public User(ISessionBinder binder, INotifierQueryable worldNotifier)
        {
            _Roster = new Roster();
            this._Binder = binder;
            this._WorldNotifer = worldNotifier;
            this._StatusMachine = new PinionCore.Utility.StatusMachine();


            _ToVerify();
        }

        private void _ToVerify()
        {
            var status = new UserVerifier(_Binder, _Roster);
            status.OnVerified += (actor) =>
            {
                _ToGame(actor);
            };
            _StatusMachine.Push(status);
        }

        private void _ToGame(Protocols.ActorInfo actor)
        {
            var status = new UserGame(_Binder, _WorldNotifer, actor);
            status.DoneEvent += () =>
            {
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
        }
    }

}