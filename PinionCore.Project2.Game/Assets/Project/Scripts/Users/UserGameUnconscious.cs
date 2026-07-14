using PinionCore.Project2.Shared;
using PinionCore.Utility;

namespace PinionCore.Project2.Users
{
    internal class UserGameUnconscious : IStatus
    {
        private ICharactor _Charactor;
        
        public event System.Action ConsciousEvent;

        public UserGameUnconscious(ICharactor charactor)
        {
            this._Charactor = charactor;
        }

        void IStatus.Enter()
        {
            throw new System.NotImplementedException();
        }

        void IStatus.Leave()
        {
            throw new System.NotImplementedException();
        }

        void IStatus.Update()
        {
            throw new System.NotImplementedException();
        }
    }
}