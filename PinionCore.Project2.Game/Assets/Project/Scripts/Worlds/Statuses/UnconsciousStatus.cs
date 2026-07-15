using PinionCore.Utility;

namespace PinionCore.Project2.Worlds.Statuses
{
    /// <summary>
    /// 無意識(僵直/死亡等):不供應任何能力介面,client 的 IMoveable 已被收回。
    /// 觸發源(HP 歸零等)屬未來戰鬥管線;目前由 Player.ToUnconscious 進入。
    /// </summary>
    internal class UnconsciousStatus : IStatus
    {
        void IStatus.Enter()
        {
        }

        void IStatus.Leave()
        {
        }

        void IStatus.Update()
        {
        }
    }
}
