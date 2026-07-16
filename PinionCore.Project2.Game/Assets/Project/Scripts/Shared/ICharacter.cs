namespace PinionCore.Project2.Shared
{

    // World 內的角色完整視圖聚合,不曝光給 client , 只給 user(IWorld.Players 供應單位)。
    // 控制(移動/出招/狀態轉移)已收斂到 world 端控制狀態機:
    // client 走 IPlayer.Controllable 供應的 IControllable,表現廣播走 IActor 事件。
    public interface ICharacter : IPlayer, IActor
    {
    }
}
