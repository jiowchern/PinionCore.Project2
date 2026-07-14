using PinionCore.Project2.Shared;
using PinionCore.Remote;
using PinionCore.Utility;
using System.Collections.Generic;

namespace PinionCore.Project2.Users
{
    class UserGameConsciousAdventure : IStatus ,IAdventure
    {
        private ICollection<IAdventure> _Adventures;

        public event System.Action BattleEvent;

        public UserGameConsciousAdventure(ICollection<IAdventure> adventures)
        {
            this._Adventures = adventures;
        }

        void IStatus.Enter()
        {
            _Adventures.Add(this);
        }

        void IStatus.Leave()
        {
            _Adventures.Remove(this);
        }

        Value<bool> IAdventure.ToBattle()
        {
            BattleEvent?.Invoke();
            return true;
        }

        void IStatus.Update()
        {
           
        }
    }
}