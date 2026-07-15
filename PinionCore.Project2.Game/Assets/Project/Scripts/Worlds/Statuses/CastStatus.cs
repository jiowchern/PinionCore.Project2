using PinionCore.Project2.Shared;
using PinionCore.Utility;

namespace PinionCore.Project2.Worlds.Statuses
{
    internal class CastStatus : IStatus
    {
        private readonly PlayerController _Controller;
        private readonly ActionType _Type;

        ActionInfo _Started;   // 訂閱 replay 記下的基準(剛啟動的那筆)
        bool _Subscribed;      // 已訂閱 ActionEvent(Leave 據此取消)
        bool _Baselined;       // 已收到 replay 基準
        bool _Done;            // DoneEvent 已發,防重複

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
            if (!_Controller.Player.StartAction(_Type, force: false))
            {
                _Finish(false);   // 啟動失敗,立即回報
                return;
            }
            _Subscribed = true;
            // 訂閱即 replay:第一發即剛啟動的 ActionInfo,記為基準
            _Controller.Player.ActionEvent += _OnAction;
        }

        void _OnAction(ActionInfo info)
        {
            if (!_Baselined)
            {
                _Started = info;
                _Baselined = true;
                return;
            }
            if (info.Action == ActionType.None)
                _Finish(true);    // 正常播完
            else if (info.Action != _Started.Action || info.StartTicks != _Started.StartTicks)
                _Finish(false);   // 被 force 覆蓋(含同型別重播,靠 StartTicks 判別)
        }

        void _Finish(bool completed)
        {
            if (_Done)
                return;
            _Done = true;
            DoneEvent(completed);
        }

        void IStatus.Leave()
        {
            if (_Subscribed)
                _Controller.Player.ActionEvent -= _OnAction;
        }

        void IStatus.Update()
        {
        }
    }
}
