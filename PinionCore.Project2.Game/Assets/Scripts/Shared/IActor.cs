using UnityEngine;

namespace PinionCore.Project2.Shared
{
    public interface IActor : Remote.Protocolable
    {
        PinionCore.Remote.Property<string> DisplayName { get; }
        PinionCore.Remote.Property<string> ModelName { get; }
        PinionCore.Remote.Property<System.Guid> ActorId { get; }

        PinionCore.Remote.Property<Vector3> Position { get; }

        event System.Action<Path[]> PathEvent; 

    }
}
