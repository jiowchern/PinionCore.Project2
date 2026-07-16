using PinionCore.Project2.Shared.Users;
using PinionCore.Remote;
using PinionCore.Utility;
using System;
namespace PinionCore.Project2.Users
{
    class UserVerifier : PinionCore.Utility.IStatus , PinionCore.Project2.Shared.Users.IVerifier
    {
        

        private readonly System.Collections.Generic.ICollection<IVerifier> _Verifiers;
        
        private readonly Roster _Roster;

        public event Action<Shared.ActorInfo> OnVerified;

        public UserVerifier(System.Collections.Generic.ICollection<IVerifier> verifiers, Roster roster)
        {
            
            _Verifiers = verifiers;
            _Roster = roster;
        }

        void IStatus.Enter()
        {
            _Verifiers.Add(this);
        }

        void IStatus.Leave()
        {
            _Verifiers.Remove(this);           
        }

        void IStatus.Update()
        {
            
        }

        PinionCore.Remote.Value<bool> IVerifier.Verify(string name, ModelType type)
        {
            var actor = _Roster.Register(name,type);
            if (actor == null)
                return false;
            
            OnVerified?.Invoke(actor.Value);
            return true;

        }
    }

}