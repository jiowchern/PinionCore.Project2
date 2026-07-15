using PinionCore.Project2.Shared;
using PinionCore.Remote;
using PinionCore.Utility;

namespace PinionCore.Project2.Worlds.Statuses
{
    /// <summary>
    /// 冒險狀態:狀態類自身即協議實作,Enter 供應 IAdventure 給擁有者 client、
    /// 廣播 StatusType.Adventure;ToBattle RPC 觸發事件由 ConsciousStatus 切換子狀態。
    /// </summary>
    internal class AdventureStatus : IStatus, IAdventure
    {
        readonly PlayerController _Controller;

        public event System.Action BattleEvent;

        public AdventureStatus(PlayerController controller)
        {
            _Controller = controller;
        }

        void IStatus.Enter()
        {
            _Controller.Player.SetStatus(StatusType.Adventure);
            _Controller.Adventures.Items.Add(this);
        }

        void IStatus.Leave()
        {
            _Controller.Adventures.Items.Remove(this);
        }

        void IStatus.Update()
        {
        }

        Value<bool> IAdventure.ToBattle()
        {
            BattleEvent?.Invoke();
            return true;
        }
    }
}
