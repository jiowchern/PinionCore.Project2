using PinionCore.Project2.Shared;
using PinionCore.Utility;
using Unity.VisualScripting.YamlDotNet.Core.Events;

namespace PinionCore.Project2.Worlds.Statuses
{
    internal class CastStatus : IStatus
    {
        private PlayerController _Controller;
        private ActionType _Type;
        

        public event System.Action<bool> DoneEvent;
        public CastStatus(PlayerController controller, ActionType type)
        {
            DoneEvent += (ok   ) => { }; // 避免 null reference
            _Controller = controller;
            _Type = type;
        }   

        void IStatus.Enter()
        {
            // 玩家觸發路徑(force: false):動作進行中不可重入,拒收回 false
            _Controller.Player.StartAction(_Type, force: false);
        }

        void IStatus.Leave()
        {
            
        }

        void IStatus.Update()
        {
            // todo: 檢查動作是否完成 是的話返回 true,否則 false

            DoneEvent(false);
        }
    }
}
