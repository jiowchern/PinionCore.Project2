using System;
using PinionCore.Project2.Shared;
using PinionCore.Remote;
using Unity.Transforms;
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

        readonly Unity.Entities.EntityManager _EntityManager;
        readonly float _MoveSpeed;

        public Property<Guid> ActorId { get; private set; }
        public Property<string> DisplayName { get; private set; }
        public Property<string> ModelName { get; private set; }

        public Property<Vector3> Position { get; private set; }

        // 移動狀態:權威位置在 LocalTransform,Position 屬性只在出生與到達時更新,
        // 進行中的移動由 PathEvent 交給前端播放。
        bool _Moving;
        Vector3 _MoveTarget;
        Path[] _CurrentPaths;

        public Player(Guid actorId, ActorInfo info, Unity.Entities.Entity entity, Unity.Entities.EntityManager entityManager, float moveSpeed, Vector3 spawnPosition)
        {
            Entity = entity;
            _EntityManager = entityManager;
            _MoveSpeed = moveSpeed;
            ActorId = new Property<Guid>(actorId);
            DisplayName = new Property<string>(info.DisplayName);
            ModelName = new Property<string>(info.ModelName);
            Position = new Property<Vector3>(spawnPosition);
        }

        event Action<Path[]> _PathEvent;
        event Action<Path[]> IActor.PathEvent
        {
            add
            {
                _PathEvent += value;
                // 移動中才訂閱的殼也要收到進行中的路徑
                if (_Moving)
                    value(_CurrentPaths);
            }
            remove
            {
                _PathEvent -= value;
            }

        }

        Value<bool> IPlayer.Move(Vector3 target)
        {
            if (_MoveSpeed <= 0f)
                return false;

            // Path 以 XZ 平面表示,Y 維持現值
            Vector3 current = _EntityManager.GetComponentData<LocalTransform>(Entity).Position;
            var destination = new Vector3(target.x, current.y, target.z);
            if ((destination - current).sqrMagnitude <= 1e-6f)
                return false;

            _MoveTarget = destination;
            _CurrentPaths = new[]
            {
                new Path
                {
                    Start = new Vector2(current.x, current.z),
                    End = new Vector2(destination.x, destination.z),
                    Speed = _MoveSpeed
                }
            };
            _Moving = true;
            _PathEvent?.Invoke(_CurrentPaths);
            return true;
        }

        /// <summary>
        /// 由 World.Update 每幀驅動:以 MoveSpeed 推進 entity,直到抵達目標。
        /// </summary>
        internal void Update(float deltaSeconds)
        {
            if (!_Moving)
                return;

            var transform = _EntityManager.GetComponentData<LocalTransform>(Entity);
            Vector3 next = Vector3.MoveTowards(transform.Position, _MoveTarget, _MoveSpeed * deltaSeconds);
            transform.Position = next;
            _EntityManager.SetComponentData(Entity, transform);

            if ((next - _MoveTarget).sqrMagnitude <= 1e-6f)
            {
                _Moving = false;
                _CurrentPaths = null;
                Position.Value = _MoveTarget;
            }
        }
    }
}
