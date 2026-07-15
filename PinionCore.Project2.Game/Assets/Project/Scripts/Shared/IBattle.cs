namespace PinionCore.Project2.Shared
{
    // 戰鬥狀態, 前端切換至戰鬥動作
    public interface IBattle : Remote.Protocolable
    {
        PinionCore.Remote.Value<bool> ToAdventure();

        // 出招(自帶位移動作);動作進行中回 false(不可重入)
        PinionCore.Remote.Value<bool> Attack();
    }
}
