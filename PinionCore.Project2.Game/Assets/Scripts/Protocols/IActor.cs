using UnityEngine;

namespace PinionCore.Project2.Protocols
{
    public interface IActor : Remote.Protocolable
    {

        Remote.Property<uint> PrototypeId { get; }
        Remote.Property<uint> EntityId { get; }

        event System.Action<Path[]> OnPathChanged;

        Remote.Value<bool> Move(Vector2 target);

    }
}
