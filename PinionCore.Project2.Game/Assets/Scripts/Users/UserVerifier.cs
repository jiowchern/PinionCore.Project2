using PinionCore.Project2.Protocols.Users;
using PinionCore.Remote;
using PinionCore.Utility;
using System;
namespace PinionCore.Project2.Users
{
    class UserVerifier : PinionCore.Utility.IStatus , PinionCore.Project2.Protocols.Users.IVerifiable
    {
        private readonly ISessionBinder _Binder;
        readonly System.Collections.Generic.List<ISoul> _Souls;
        private readonly Roster _Roster;

        public event Action<Protocols.ActorInfo> OnVerified;

        public UserVerifier(ISessionBinder binder, Roster roster)
        {
            _Souls= new System.Collections.Generic.List<ISoul>();
            _Binder = binder;
            _Roster = roster;
        }

        void IStatus.Enter()
        {
            _Souls.Add(_Binder.Bind<IVerifiable>(this));
        }

        void IStatus.Leave()
        {
            foreach (var soul in _Souls)
            {
                _Binder.Unbind(soul);
            }           
        }

        void IStatus.Update()
        {
            
        }

        PinionCore.Remote.Value<bool> IVerifiable.Verify(string name)
        {
            var actor = _Roster.Register(name);
            if (actor == null)
                return false;

            OnVerified?.Invoke(actor.Value);
            return true;

        }
    }

}