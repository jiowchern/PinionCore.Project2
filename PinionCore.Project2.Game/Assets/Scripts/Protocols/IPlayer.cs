using UnityEngine;


namespace PinionCore.Project2.Protocols
{

    public interface IActor : Remote.Protocolable
    {
        PinionCore.Remote.Property<string> DisplayName { get; }
        PinionCore.Remote.Property<string> ModelName { get; }
        PinionCore.Remote.Property<System.Guid> ActorId { get; }

        event System.Action<Vector2[]> PathEvent;

    }
    public interface IPlayer : IActor
    {

        PinionCore.Remote.Value<bool> Move(Vector2 target);



    }
}
