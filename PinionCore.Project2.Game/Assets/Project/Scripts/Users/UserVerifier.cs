using PinionCore.Project2.Shared.Users;
using PinionCore.Remote;
using PinionCore.Utility;
using System;
namespace PinionCore.Project2.Users
{
    class UserVerifier : PinionCore.Utility.IStatus , PinionCore.Project2.Shared.Users.IVerifiable
    {
        private readonly System.Collections.Generic.ICollection<IVerifiable> _Verifiables;
        
        private readonly Roster _Roster;

        public event Action<Shared.ActorInfo> OnVerified;

        public UserVerifier(System.Collections.Generic.ICollection<IVerifiable> verifiables, Roster roster)
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