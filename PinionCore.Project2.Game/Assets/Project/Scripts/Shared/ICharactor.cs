namespace PinionCore.Project2.Shared
{

    // World 內的角色狀態資訊不曝光給 client , 只給 user
    public interface ICharactor : IPlayer, IActor ,IMoveable
    {

        void SetStatus(StatusType status);

        // 播放自帶位移動作(玩家觸發路徑):動作進行中回 false;
        // 伺服器內部覆蓋(僵直/死亡)走 World 端的 force 路徑,不經此介面
        PinionCore.Remote.Value<bool> PlayAction(ActionType action);
    }
}
