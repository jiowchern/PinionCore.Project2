using PinionCore.Project2.Shared;
using PinionCore.Remote;
using PinionCore.Utility;

namespace PinionCore.Project2.Worlds.Statuses
{
    /// <summary>
    /// 戰鬥狀態:狀態類自身即協議實作,Enter 供應 IBattle 給擁有者 client、
    /// 廣播 StanceType.Battle;Attack 直呼 Player.StartAction(零轉發跳數),
    /// ToAdventure RPC 觸發事件由 ConsciousStatus 切換子狀態。
    /// </summary>
    internal class BattleStatus : IStatus, IBattle
    {
        readonly PlayerController _Controller;

        public event System.Action AdventureEvent;
        public event System.Action<ActionType> CastEvent;

        public BattleStatus(PlayerController controller)
        {
            _Controller = controller;
        }

        void IStatus.Enter()
        {
            _Controller.Player.SetStance(StanceType.Battle);
            _Controller.Battles.Items.Add(this);
        }

        void IStatus.Leave()
        {
            _Controller.Battles.Items.Remove(this);
        }

        void IStatus.Update()
        {
        }

        Value<bool> IBattle.ToAdventure()
        {
            AdventureEvent?.Invoke();
            return true;
        }

        Value<bool> IBattle.Attack(ActionType type)
        {

            // todo : 這裡應該要有一個判斷,檢查 type 是否為可攻擊的技能,若不是則回傳 false,不觸發事件


            CastEvent?.Invoke(type);
            return true;
        }
    }
}
