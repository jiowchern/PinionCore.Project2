using PinionCore.Project2.Shared;
using PinionCore.Remote;
using PinionCore.Utility;
using System.Collections.Generic;

namespace PinionCore.Project2.Users
{
    internal class UserGameConsciousBattle : IStatus ,IBattle
    {
        private ICollection<IBattle> _Battles;
        public event System.Action AdventureEvent;

        public UserGameConsciousBattle(ICollection<IBattle> battles)
        {
            this._Battles = battles;
        }

        void IStatus.Enter()
        {
            _Battles.Add(this);
        }

        void IStatus.Leave()
        {
            _Battles.Remove(this);
        }

        Value<bool> IBattle.ToAdventure()
        {
            AdventureEvent?.Invoke();
            return true;
        }

        void IStatus.Update()
        {

        }
    }
}