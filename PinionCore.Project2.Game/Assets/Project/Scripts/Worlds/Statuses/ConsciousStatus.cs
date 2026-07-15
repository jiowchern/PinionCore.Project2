using PinionCore.Utility;

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

        public ConsciousStatus(PlayerController controller)
        {
            _Controller = controller;
            _Machine = new StatusMachine();
        }

        void IStatus.Enter()
        {
            _Controller.Moveables.Items.Add(_Controller);
            _ToAdventure();
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
            _Machine.Push(status);
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
