using System;


namespace PinionCore.Project2.Shared
{
    public interface IGame : Remote.Protocolable
    {
        PinionCore.Remote.Notifier<IView> View { get; }

        PinionCore.Remote.Property<string> WorldName { get; }

        PinionCore.Remote.Notifier<IPlayer> Player { get; } 
    }
}
