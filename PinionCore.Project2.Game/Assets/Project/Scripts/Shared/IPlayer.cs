using PinionCore.Remote;


namespace PinionCore.Project2.Shared
{
    // 角色的控制功能跟私有顯示資訊
    // 只曝光給 client 的擁有者
    public interface IPlayer : IIdentity
    {


        // 在角色視野內的 Actor 離開視野則觸發 UnsupplyEvent,進入視野則觸發 SupplyEvent, 包含自己。
        Notifier<IActor> Actors { get; }

        // 可移動能力:由 world 端角色狀態機(Conscious/Unconscious)控制供應;
        // supply = 可移動,unsupply = 能力收回(如無意識)。client 以 Supply/Unsupply 事件得知。
        Notifier<IMoveable> Moveable { get; }

        // 冒險/戰鬥能力:由 world 端 Conscious 內的子狀態互斥供應(狀態類自身即協議實作),
        // 供應哪個 = 角色當下處於哪個狀態;表現廣播仍走 IActor.StanceEvent。
        Notifier<IAdventure> Adventure { get; }

        Notifier<IBattle> Battle { get; }
    }
}
