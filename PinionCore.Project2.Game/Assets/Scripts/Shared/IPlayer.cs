using UnityEngine;


namespace PinionCore.Project2.Shared
{
    public interface IPlayer : IActor
    {

        PinionCore.Remote.Value<bool> Move(Vector3 target);

        PinionCore.Remote.Value<bool> Stop();

    }
}
