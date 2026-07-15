using PinionCore.Project2.Shared;
using PinionCore.Remote;
using PinionCore.Utility;

namespace PinionCore.Project2.Worlds.Statuses
{
    /// <summary>
    /// 戰鬥狀態:狀態類自身即協議實作,Enter 供應 IBattle 給擁有者 client、
    /// 廣播 StatusType.Battle;Attack 直呼 Player.StartAction(零轉發跳數),
    /// ToAdventure RPC 觸發事件由 ConsciousStatus 切換子狀態。
    /// </summary>
    internal class BattleStatus : IStatus, IBattle
    {
        readonly Player _Player;

        public event System.Action AdventureEvent;

        public BattleStatus(Player player)
        {
            _Player = player;
        }

        void IStatus.Enter()
        {
            _Player.SetStatus(StatusType.Battle);
            _Player.Battles.Items.Add(this);
        }

        void IStatus.Leave()
        {
            _Player.Battles.Items.Remove(this);
        }

        void IStatus.Update()
        {
        }

        Value<bool> IBattle.ToAdventure()
        {
            AdventureEvent?.Invoke();
            return true;
        }

        Value<bool> IBattle.Attack()
        {
            // 玩家觸發路徑(force: false):動作進行中不可重入,拒收回 false
            return _Player.StartAction(ActionType.Attack, force: false);
        }
    }
}
