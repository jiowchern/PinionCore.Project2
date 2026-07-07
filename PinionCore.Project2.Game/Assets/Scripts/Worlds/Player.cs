using System;
using PinionCore.Project2.Shared;
using PinionCore.Remote;
using UnityEngine;

namespace PinionCore.Project2.Worlds
{
    /// <summary>
    /// 封裝一顆 DOTS entity 的玩家物件:對外(協議)提供 IPlayer/IActor 的檢視與控制,
    /// entity 的建立與銷毀由 World 負責,Player 只持有參考。
    /// </summary>
    public class Player : IPlayer
    {
        // World 在 Leave 時據此銷毀 entity。
        public readonly Unity.Entities.Entity Entity;

        public Property<Guid> ActorId { get; private set; }
        public Property<string> DisplayName { get; private set; }
        public Property<string> ModelName { get; private set; }

        public Player(Guid actorId, ActorInfo info, Unity.Entities.Entity entity)
        {
            Entity = entity;
            ActorId = new Property<Guid>(actorId);
            DisplayName = new Property<string>(info.DisplayName);
            ModelName = new Property<string>(info.ModelName);
        }

        event Action<Vector3[]> _PathEvent;
        event Action<Vector3[]> IActor.PathEvent
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

        Value<bool> IPlayer.Move(Vector3 target)
        {
            throw new NotImplementedException();
        }
    }
}
