using UnityEngine;


namespace PinionCore.Project2.Shared
{

    public interface IActor : Remote.Protocolable
    {
        PinionCore.Remote.Property<string> DisplayName { get; }
        PinionCore.Remote.Property<string> ModelName { get; }
        PinionCore.Remote.Property<System.Guid> ActorId { get; }

        event System.Action<Vector3[]> PathEvent;

    }
    public interface IPlayer : IActor
    {

        PinionCore.Remote.Value<bool> Move(Vector3 target);



    }
}
