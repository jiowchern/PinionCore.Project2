using System;
using PinionCore.Project2.Protocols;
using PinionCore.Remote;
using UnityEngine;

namespace PinionCore.Project2.Worlds
{
    public class Player : IPlayer
    {
        public Property<Guid> Id { get; private set; }
        public Property<string> DisplayName { get; private set; }

        Property<string> IActor.Name => throw new NotImplementedException();

        Property<Guid> IActor.ActorId => throw new NotImplementedException();

        public Player(Guid id, string displayName)
        {
            Id = new Property<Guid>(id);
            DisplayName = new Property<string>(displayName);
        }

        event Action<Vector2[]> _PathEvent;
        event Action<Vector2[]> IActor.PathEvent
        {
            add
            {
                _PathEvent += value;
            }
            remove
            {
                _PathEvent -= value;
            }

        }

        Value<bool> IPlayer.Move(Vector2 target)
        {
            throw new NotImplementedException();
        }
    }
}
