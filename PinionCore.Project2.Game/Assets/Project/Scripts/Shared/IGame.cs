using System;


namespace PinionCore.Project2.Shared
{
    public interface IGame : Remote.Protocolable
    {
        PinionCore.Remote.Notifier<IView> View { get; }

        PinionCore.Remote.Property<string> WorldName { get; }

        PinionCore.Remote.Notifier<IPlayer> Player { get; }

        // 移動控制介面與 Players 同源(同一角色);獨立供應
        // 使未來可單獨撤供 IMoveable(如失控/載具)而不影響 IPlayer。
        PinionCore.Remote.Notifier<IAdventure> Adventure { get; }

        PinionCore.Remote.Notifier<IBattle> Battle { get; }

        PinionCore.Remote.Notifier<IMoveable> Moveable { get; }




    }
}
