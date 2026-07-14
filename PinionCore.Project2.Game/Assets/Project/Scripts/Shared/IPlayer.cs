using PinionCore.Remote;


namespace PinionCore.Project2.Shared
{
    // 角色的控制功能跟私有顯示資訊
    // 只曝光給 client 的擁有者
    public interface IPlayer : IIdentity
    {


        // 在角色視野內的 Actor 離開視野則觸發 UnsupplyEvent,進入視野則觸發 SupplyEvent, 包含自己。
        Notifier<IActor> Actors { get; }
    }
}
