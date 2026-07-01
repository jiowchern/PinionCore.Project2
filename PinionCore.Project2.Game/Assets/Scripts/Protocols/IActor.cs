using UnityEngine;

namespace PinionCore.Project2.Protocols.Worlds
{
    public interface IActor : PinionCore.Remote.Protocolable
    {

        PinionCore.Remote.Property<uint> PrototypeId { get; }
        PinionCore.Remote.Property<uint> EntityId { get; }

        event System.Action<Path[]> OnPathChanged;

        PinionCore.Remote.Value<bool> Move(Vector2 target);

    }
}
