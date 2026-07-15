using PinionCore.Project2.Shared;
using PinionCore.Remote;
using PinionCore.Utility;
using System.Collections.Generic;

namespace PinionCore.Project2.Users
{
    internal class UserGameConsciousBattle : IStatus ,IBattle
    {
        private ICollection<IBattle> _Battles;
        private readonly ICharactor _Charactor;
        public event System.Action AdventureEvent;

        public UserGameConsciousBattle(ICollection<IBattle> battles, ICharactor charactor)
        {
            this._Battles = battles;
            this._Charactor = charactor;
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

        Value<bool> IBattle.Attack()
        {
            // ICharactor 是跨 Users↔Worlds 的 ghost:PlayAction 回傳的 pending Value
            // 直接作為 RPC 回應鏈回傳(Value 跨層轉發是既有慣例,見 UserGame._EnterWorld)
            return _Charactor.PlayAction(ActionType.Attack);
        }

        void IStatus.Update()
        {

        }
    }
}