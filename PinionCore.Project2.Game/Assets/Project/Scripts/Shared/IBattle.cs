namespace PinionCore.Project2.Shared
{
    // 戰鬥狀態, 前端切換至戰鬥動作
    public interface IBattle : Remote.Protocolable
    {
        PinionCore.Remote.Value<bool> ToAdventure();
    }
}
