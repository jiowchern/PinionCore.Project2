using PinionCore.Project2.Shared;
using PinionCore.Utility;
using System;
using System.Collections.Generic;

namespace PinionCore.Project2.Users
{
    internal class UserGameConscious : IStatus
    {
        private ICharactor _Charactor;
        
        readonly ICollection<IMoveable> _Moves;
        readonly ICollection<IAdventure> _Adventures;
        readonly ICollection<IBattle> _Battles;
        public event System.Action UnconsciousEvent;

        readonly StatusMachine _Machine;
        public UserGameConscious(ICollection<IMoveable> moveables, ICollection<IAdventure> adventures, ICollection<IBattle> battles, ICharactor charactor)
        {
            _Machine = new StatusMachine();
            this._Charactor = charactor;
            this._Moves = moveables;
            this._Adventures = adventures;
            this._Battles = battles;
        }

        void IStatus.Enter()
        {
            _Moves.Add(_Charactor);
            _ToAdventures();
        }

        private void _ToAdventures()
        {
            _Charactor.SetStatus(StatusType.Adventure);
            var status = new UserGameConsciousAdventure(_Adventures);
            status.BattleEvent += _ToBattle;
            _Machine.Push(status);
        }

        private void _ToBattle()
        {
            _Charactor.SetStatus(StatusType.Battle);
            var status = new UserGameConsciousBattle(_Battles);
            status.AdventureEvent += _ToAdventures;
            _Machine.Push(status);
        }

        void IStatus.Leave()
        {
            _Machine.Termination();
            _Moves.Remove(_Charactor);
        }

        void IStatus.Update()
        {
            _Machine.Update();
        }
    }
}