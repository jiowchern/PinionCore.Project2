namespace PinionCore.Project2.Shared
{
    // 冒險狀態, 前端切換至冒險動作
    public interface IAdventure : Remote.Protocolable
    {
        PinionCore.Remote.Value<bool> ToBattle();
    }
}
