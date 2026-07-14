using PinionCore.Project2.Shared.Users;
using PinionCore.Remote;
using PinionCore.Utility;
using System;
namespace PinionCore.Project2.Users
{
    class UserVerifier : PinionCore.Utility.IStatus , PinionCore.Project2.Shared.Users.ILogin
    {
        

        private readonly System.Collections.Generic.ICollection<ILogin> _Verifiables;
        
        private readonly Roster _Roster;

        public event Action<Shared.ActorInfo> OnVerified;

        public UserVerifier(System.Collections.Generic.ICollection<ILogin> verifiables, Roster roster)
        {
            
            _Verifiables = verifiables;
            _Roster = roster;
        }

        void IStatus.Enter()
        {
            _Verifiables.Add(this);
        }

        void IStatus.Leave()
        {
            _Verifiables.Remove(this);           
        }

        void IStatus.Update()
        {
            
        }

        PinionCore.Remote.Value<bool> ILogin.Verify(string name, CharactorType type)
        {
            var actor = _Roster.Register(name,type);
            if (actor == null)
                return false;
            
            OnVerified?.Invoke(actor.Value);
            return true;

        }
    }

}