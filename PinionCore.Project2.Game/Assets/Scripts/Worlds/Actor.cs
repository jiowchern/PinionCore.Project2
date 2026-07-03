using PinionCore.Project2.Protocols;
using PinionCore.Remote;
using System;
using UnityEngine;

namespace PinionCore.Project2.Worlds
{
    public class Actor : IActor
    {
        Property<uint> IActor.PrototypeId => throw new NotImplementedException();

        Property<uint> IActor.EntityId => throw new NotImplementedException();

        event Action<Path[]> IActor.OnPathChanged
        {
            add
            {
                throw new NotImplementedException();
            }

            remove
            {
                throw new NotImplementedException();
            }
        }

        Value<bool> IActor.Move(Vector2 target)
        {
            throw new NotImplementedException();
        }
    }
}
