using System;
using PinionCore.Project2.Shared;
using PinionCore.Remote;
using Unity.Mathematics;
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
        readonly long _MoveAcceptIntervalTicks;
        readonly World _World;

        // 上次被接受的 Move 時間戳,節流用;初值遠早於世界時間,首發必被接受
        long _LastMoveAcceptedTicks;

        public Property<Guid> ActorId { get; private set; }
        public Property<string> DisplayName { get; private set; }
        public Property<string> ModelName { get; private set; }

        // 權威狀態的唯一真相:等速直線 MoveInfo,任意時刻以 MoveSampler 取樣;
        // entity 的 LocalTransform 只是取樣結果的投影(供未來碰撞查詢使用)。
        MoveInfo _MoveInfo;

        // 供 editor 除錯繪製(WorldDebugDrawer)讀取當前 MoveInfo
        internal MoveInfo CurrentMoveInfo => _MoveInfo;

        public Player(Guid actorId, ActorInfo info, Unity.Entities.Entity entity, Unity.Entities.EntityManager entityManager, float moveSpeed, float moveAcceptInterval, Vector3 spawnPosition, World world)
        {
            _World = world;
            Entity = entity;
            _EntityManager = entityManager;
            _MoveSpeed = moveSpeed;
            _MoveAcceptIntervalTicks = (long)(moveAcceptInterval * TimeSpan.TicksPerSecond);
            _LastMoveAcceptedTicks = long.MinValue / 4;
            ActorId = new Property<Guid>(actorId);
            DisplayName = new Property<string>(info.DisplayName);
            ModelName = new Property<string>(info.ModelName);
            _MoveInfo = new MoveInfo
            {
                Position = new Vector2(spawnPosition.x, spawnPosition.z),
                Facing = new Vector2(0f, 1f), // 出生面向 +Z
                Speed = 0f,
                StartTicks = _World.ElapsedTicks
            };
        }

        event Action<MoveInfo> _MoveEvent;
        event Action<MoveInfo> IActor.MoveEvent
        {
            add
            {
                _MoveEvent += value;
                // 駐留與移動中都是有效狀態,一律 replay:晚訂閱的殼取樣即得正確狀態。
                value(_MoveInfo);
            }
            remove
            {
                _MoveEvent -= value;
            }

        }

        // 以當下時間取樣權威位置/朝向,作為新 MoveInfo 的起點。
        void _SampleNow(out Vector2 position, out Vector2 facing, out long now)
        {
            now = _World.ElapsedTicks;
            var elapsed = (now - _MoveInfo.StartTicks) / (double)TimeSpan.TicksPerSecond;
            MoveSampler.Sample(_MoveInfo, elapsed, out position, out facing);
        }

        Value<bool> IPlayer.Move(Vector2 direction)
        {
            if (_MoveSpeed <= 0f || direction.sqrMagnitude <= 1e-6f)
                return false;

            // Move 節流:距上次被接受的 Move 未滿間隔即拒收;Stop 不受此限
            if (_World.ElapsedTicks - _LastMoveAcceptedTicks < _MoveAcceptIntervalTicks)
                return false;

            _SampleNow(out var position, out _, out var now);

            // 瞬轉直走:朝向即刻設為指令方向(世界座標),直線前進直到下一個 Move/Stop;
            // 轉向表現由前端補間
            _MoveInfo = new MoveInfo
            {
                Position = position,
                Facing = direction.normalized,
                Speed = _MoveSpeed,
                StartTicks = now
            };
            _LastMoveAcceptedTicks = now;
            _MoveEvent?.Invoke(_MoveInfo);
            return true;
        }

        Value<bool> IPlayer.Stop()
        {
            if (_MoveInfo.Speed == 0f)
                return false;

            _SampleNow(out var position, out var facing, out var now);
            _MoveInfo = new MoveInfo
            {
                Position = position,
                Facing = facing,
                Speed = 0f,
                StartTicks = now
            };
            _MoveEvent?.Invoke(_MoveInfo);
            return true;
        }

        /// <summary>
        /// 由 World.Update 每幀驅動:把 MoveInfo 的取樣結果投影到 entity 的 LocalTransform。
        /// </summary>
        internal void Update()
        {
            _SampleNow(out var position, out var facing, out _);

            var transform = _EntityManager.GetComponentData<LocalTransform>(Entity);
            transform.Position = new float3(position.x, transform.Position.y, position.y);
            transform.Rotation = quaternion.LookRotationSafe(new float3(facing.x, 0f, facing.y), math.up());
            _EntityManager.SetComponentData(Entity, transform);
        }
    }
}
