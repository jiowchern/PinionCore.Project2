using PinionCore.Utility;

namespace PinionCore.Project2.Worlds.Statuses
{
    /// <summary>
    /// 無意識(僵直/死亡等):不供應任何能力介面,client 的 IMoveable 已被收回。
    /// 觸發源(HP 歸零等)屬未來戰鬥管線;目前由 Player.ToUnconscious 進入。
    /// </summary>
    internal class UnconsciousStatus : IStatus
    {
        readonly Player _Player;

        public UnconsciousStatus(Player player)
        {
            _Player = player;
        }

        void IStatus.Enter()
        {
            // 能力收回的同時結束進行中的走路循環,避免無意識後角色繼續走;
            // Cast(僵直/死亡動作)不受影響,由動作排程自然播完
            _Player.StopLocomotion();
        }

        void IStatus.Leave()
        {
        }

        void IStatus.Update()
        {
        }
    }
}
