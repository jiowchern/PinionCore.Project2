using PinionCore.Project2.Shared;
using PinionCore.Utility;
using System;

namespace PinionCore.Project2.Worlds.Statuses
{
    /// <summary>
    /// 有意識:供應 IMoveable 給擁有者 client(可移動),並以子狀態機互斥供應
    /// IAdventure/IBattle(進入即冒險態)。狀態即能力閘(GameProject1 Play 模式):
    /// Enter 供應、Leave 收回,client 由 Supply/Unsupply 事件得知當下擁有的能力。
    /// </summary>
    internal class ConsciousStatus : IStatus
    {
        readonly PlayerController _Controller;
        readonly StatusMachine _Machine;
        private readonly StatusType _state;

        public event Action<ActionType> CastEvent;

        public ConsciousStatus(PlayerController controller, StatusType state)
        {
            CastEvent += (type) => { }; // 避免 null reference
            _Controller = controller;
            _Machine = new StatusMachine();
            _state = state;
        }

        void IStatus.Enter()
        {
            _Controller.Moveables.Items.Add(_Controller);
            if (_state == StatusType.Adventure)
            {
                _ToAdventure();
            }
            else
            {
                _ToBattle();
            }
        }

        void _ToAdventure()
        {
            var status = new AdventureStatus(_Controller);
            status.BattleEvent += _ToBattle;
            _Machine.Push(status);
        }

        void _ToBattle()
        {
            var status = new BattleStatus(_Controller);
            status.AdventureEvent += _ToAdventure;
            status.CastEvent += _ToCast;
            _Machine.Push(status);
        }

        private void _ToCast(ActionType type)
        {
            CastEvent(type);
        }

        void IStatus.Leave()
        {
            _Machine.Termination();
            _Controller.Moveables.Items.Remove(_Controller);
        }

        void IStatus.Update()
        {
            _Machine.Update();
        }
    }
}
