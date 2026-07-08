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
        readonly World _World;

        public Property<Guid> ActorId { get; private set; }
        public Property<string> DisplayName { get; private set; }
        public Property<string> ModelName { get; private set; }

        // 權威位置在 LocalTransform;對外的位置狀態只有 _MoveInfo:
        // 駐留(Start==End)或移動中的時間戳路徑,前端以 world time 取樣。
        bool _Moving;
        Vector3 _MoveTarget;
        MoveInfo _MoveInfo;

        public Player(Guid actorId, ActorInfo info, Unity.Entities.Entity entity, Unity.Entities.EntityManager entityManager, float moveSpeed, Vector3 spawnPosition, World world)
        {
            _World = world;
            Entity = entity;
            _EntityManager = entityManager;
            _MoveSpeed = moveSpeed;
            ActorId = new Property<Guid>(actorId);
            DisplayName = new Property<string>(info.DisplayName);
            ModelName = new Property<string>(info.ModelName);
            _MoveInfo = _Stand(spawnPosition);
        }

        // 駐留 = Start==End 的退化路徑;取樣結果恆為該點,與移動共用同一條前端邏輯。
        MoveInfo _Stand(Vector3 position)
        {
            return new MoveInfo
            {
                Paths = new[]
                {
                    new Path
                    {
                        Start = new Vector2(position.x, position.z),
                        End = new Vector2(position.x, position.z),
                        Speed = _MoveSpeed
                    }
                },
                StartTicks = _World.ElapsedTicks
            };
        }

        event Action<MoveInfo> _MoveEvent;
        event Action<MoveInfo> IActor.MoveEvent
        {
            add
            {
                _MoveEvent += value;
                // 駐留與移動中都是有效狀態,一律 replay:晚訂閱的殼取樣即得正確位置。
                value(_MoveInfo);
            }
            remove
            {
                _MoveEvent -= value;
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
            _MoveInfo = new MoveInfo
            {
                Paths = new[]
                {
                    new Path
                    {
                        Start = new Vector2(current.x, current.z),
                        End = new Vector2(destination.x, destination.z),
                        Speed = _MoveSpeed
                    }
                },
                StartTicks = _World.ElapsedTicks
            };
            _Moving = true;
            _MoveEvent?.Invoke(_MoveInfo);
            return true;
        }

        Value<bool> IPlayer.Stop()
        {
            if (!_Moving)
                return false;

            Vector3 current = _EntityManager.GetComponentData<LocalTransform>(Entity).Position;
            _Moving = false;
            _MoveInfo = _Stand(current);
            _MoveEvent?.Invoke(_MoveInfo);
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
                _MoveInfo = _Stand(_MoveTarget);
            }
        }
    }
}
