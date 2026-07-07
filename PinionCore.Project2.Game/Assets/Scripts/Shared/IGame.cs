using System;


namespace PinionCore.Project2.Shared
{
    
    public interface IGame : Remote.Protocolable
    {
        

        PinionCore.Remote.Property<string> WorldName { get; }

        PinionCore.Remote.Notifier<IActor> Actors { get; }

        PinionCore.Remote.Notifier<IPlayer> Players { get; }


    }
}
