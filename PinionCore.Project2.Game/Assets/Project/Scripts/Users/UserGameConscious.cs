using PinionCore.Project2.Shared;
using PinionCore.Utility;
using System.Collections.Generic;

namespace PinionCore.Project2.Users
{
    internal class UserGameConscious : IStatus
    {
        private ICharactor _Charactor;
        
        readonly ICollection<IMoveable> _Moves;
        public event System.Action UnconsciousEvent;
        public UserGameConscious(ICollection<IMoveable> moveables, ICharactor charactor)
        {
            this._Charactor = charactor;
            this._Moves = moveables;
        }

        void IStatus.Enter()
        {
            _Moves.Add(_Charactor);
        }

        void IStatus.Leave()
        {
            _Moves.Remove(_Charactor);
        }

        void IStatus.Update()
        {            
        }
    }
}