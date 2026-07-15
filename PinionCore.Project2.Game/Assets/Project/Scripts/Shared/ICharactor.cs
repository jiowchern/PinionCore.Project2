namespace PinionCore.Project2.Shared
{

    // World 內的角色完整視圖聚合,不曝光給 client , 只給 user(IWorld.Players 供應單位)。
    // 狀態切換與動作觸發已下沉 world 端狀態機:狀態廣播走 IActor.StatusEvent,
    // 出招走 IBattle.Attack(戰鬥狀態才供應),伺服器內部覆蓋走 World 端的 force 路徑。
    public interface ICharactor : IPlayer, IActor ,IMoveable
    {
    }
}
