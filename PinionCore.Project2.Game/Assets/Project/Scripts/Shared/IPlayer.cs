using PinionCore.Remote;


namespace PinionCore.Project2.Shared
{
    // 角色的控制功能跟私有顯示資訊
    // 只曝光給 client 的擁有者
    public interface IPlayer : IIdentity
    {


        // 在角色視野內的 Actor 離開視野則觸發 UnsupplyEvent,進入視野則觸發 SupplyEvent, 包含自己。
        Notifier<IActor> Actors { get; }

        // 控制能力:由 world 端控制狀態機供應,同一時間至多一顆(單數 = 至多供應一個);
        // 每個控制狀態即一顆 soul,狀態轉移 = unsupply + supply,
        // 無意識(能力收回)= 不供應。client 以 Supply/Unsupply 事件得知。
        Notifier<IControllable> Controllable { get; }
    }
}
