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
    public class Player : ICharactor
    {
        // World 在 Leave 時據此銷毀 entity。
        public readonly Unity.Entities.Entity Entity;

        readonly Unity.Entities.EntityManager _EntityManager;
        readonly float _MoveSpeed;
        readonly long _MoveAcceptIntervalTicks;
        readonly float _Radius;
        readonly World _World;

        // 上次被接受的 Move 時間戳,節流用;初值遠早於世界時間,首發必被接受
        long _LastMoveAcceptedTicks;

        // 解析式撞牆預測:MoveInfo 是等速直線、地形靜態,撞擊時刻在 MoveInfo 變更時
        // 一次算定(sphere cast),之後每幀只需比較時間,不做物理查詢。
        bool _HasPendingHit;
        long _HitTicks;
        Vector2 _HitNormal;
        Vector2 _HitContactPos;

        // 轉角防震盪:短時間內連續兩次 redirect 視為卡在兩面牆之間,對兩法線都投影
        Vector2 _LastRedirectNormal;
        long _LastRedirectTicks;

        // 撞牆掃掠的最大距離;超過此距離的牆等下次 MoveInfo 變更再說(正常地圖尺寸內足夠)
        const float MaxCastDistance = 100f;
        // 切線分量低於此比例視為正面撞牆 → 停止(避免以極慢速度貼牆蠕動)
        const float MinTangentSpeedRatio = 0.15f;
        // 轉角判定窗:此間隔內的第二次 redirect 需同時滿足前一面牆的約束
        static readonly long CornerWindowTicks = TimeSpan.TicksPerSecond / 20;
        // 單幀 redirect 鏈上限,防病態幾何造成無限迴圈
        const int MaxRedirectsPerFrame = 4;

        public Property<Guid> ActorId { get; private set; }
        public Property<string> DisplayName { get; private set; }
        public Property<string> ModelName { get; private set; }

        // 視野內角色(含自己);由 World 在 Enter/Leave 時增刪,目前尚無視野過濾,
        // 供應範圍即整個世界的玩家。
        readonly Depot<Player> _VisibleActors;
        readonly Notifier<IActor> _ActorsNotifier;
        Notifier<IActor> IPlayer.Actors => _ActorsNotifier;

        // 供 World 增刪可見角色
        internal Depot<Player> VisibleActors => _VisibleActors;

        // 權威狀態的唯一真相:等速直線 MoveInfo,任意時刻以 MoveSampler 取樣;
        // entity 的 LocalTransform 只是取樣結果的投影(供未來碰撞查詢使用)。
        MoveInfo _MoveInfo;

        // 供 editor 除錯繪製(WorldDebugDrawer)讀取當前 MoveInfo / 半徑 / 預計撞點
        internal MoveInfo CurrentMoveInfo => _MoveInfo;
        internal float Radius => _Radius;
        internal bool HasPendingHit => _HasPendingHit;
        internal Vector2 PendingHitPosition => _HitContactPos;

        public Player(Guid actorId, ActorInfo info, Unity.Entities.Entity entity, Unity.Entities.EntityManager entityManager, float moveSpeed, float moveAcceptInterval, float radius, Vector3 spawnPosition, World world)
        {
            _World = world;
            Entity = entity;
            _EntityManager = entityManager;
            _MoveSpeed = moveSpeed;
            _MoveAcceptIntervalTicks = (long)(moveAcceptInterval * TimeSpan.TicksPerSecond);
            _Radius = radius;
            _LastMoveAcceptedTicks = long.MinValue / 4;
            _LastRedirectTicks = long.MinValue / 4;
            _VisibleActors = new Depot<Player>();
            _ActorsNotifier = _VisibleActors.ToNotifier<IActor>();
            ActorId = new Property<Guid>(actorId);
            DisplayName = new Property<string>(info.DisplayName);
            ModelName = new Property<string>(info.ModelName);

            // 出生點也走 _SetMoveInfo:嵌牆的 Entrance 會被推出;
            // 此時尚無訂閱者,不需 emit,訂閱時的 replay 會送出修正後狀態。
            _SetMoveInfo(new MoveInfo
            {
                Position = new Vector2(spawnPosition.x, spawnPosition.z),
                Facing = new Vector2(0f, 1f), // 出生面向 +Z
                Speed = 0f,
                StartTicks = _World.ElapsedTicks
            }, emit: false);
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

            // 取樣前先結清已到期的撞牆 redirect,避免以穿牆的外推位置當新起點
            _ProcessDueRedirects(_World.ElapsedTicks);
            _SampleNow(out var position, out _, out var now);

            // 瞬轉直走:朝向即刻設為指令方向(世界座標),直線前進直到下一個 Move/Stop;
            // 轉向表現由前端補間。貼牆朝牆走時 _SetMoveInfo 會先投影成滑行再發出。
            _LastMoveAcceptedTicks = now;
            _SetMoveInfo(new MoveInfo
            {
                Position = position,
                Facing = direction.normalized,
                Speed = _MoveSpeed,
                StartTicks = now
            }, emit: true);
            return true;
        }

        Value<bool> IPlayer.Stop()
        {
            _ProcessDueRedirects(_World.ElapsedTicks);
            if (_MoveInfo.Speed == 0f)
                return false;

            _SampleNow(out var position, out var facing, out var now);
            _SetMoveInfo(new MoveInfo
            {
                Position = position,
                Facing = facing,
                Speed = 0f,
                StartTicks = now
            }, emit: true);
            return true;
        }

        /// <summary>
        /// 由 World.Update 每幀驅動:先結清到期的撞牆 redirect,
        /// 再把 MoveInfo 的取樣結果投影到 entity 的 LocalTransform。
        /// </summary>
        internal void Update()
        {
            _ProcessDueRedirects(_World.ElapsedTicks);
            _SampleNow(out var position, out var facing, out _);

            var transform = _EntityManager.GetComponentData<LocalTransform>(Entity);
            transform.Position = new float3(position.x, transform.Position.y, position.y);
            transform.Rotation = quaternion.LookRotationSafe(new float3(facing.x, 0f, facing.y), math.up());
            _EntityManager.SetComponentData(Entity, transform);
        }

        /// <summary>
        /// 提交新的權威 MoveInfo:先去穿透、再處理「起點已貼牆且朝牆走」的立即滑行,
        /// 最後單次發出事件並重算下一個撞擊時刻。Move/Stop/出生都走這裡。
        /// </summary>
        void _SetMoveInfo(MoveInfo candidate, bool emit)
        {
            var terrain = _World.Terrain;
            if (terrain != null)
            {
                // 去穿透:嵌進障礙時沿法線推出(多面牆需要多次)
                for (var i = 0; i < 4 && terrain.ComputePenetration(_Radius, candidate.Position, out var push); i++)
                    candidate.Position += push;

                // 貼牆即滑:起點就在牆邊(Skin 距離內)且朝牆走 → 立即投影成滑行,
                // 讓發出的 MoveInfo 從一開始就不含穿牆路徑(轉角可能需要第二次)
                for (var i = 0; i < 2 && candidate.Speed > 0f; i++)
                {
                    if (!terrain.CastSphere(_Radius, candidate.Position, candidate.Facing,
                            TerrainQuery.Skin * 2f + 0.01f, out _, out var normal))
                        break;
                    if (Vector2.Dot(candidate.Facing, normal) >= 0f)
                        break;   // 平行或遠離牆面的方向不需處理
                    candidate = _ProjectOntoTangent(candidate, normal, candidate.Position, candidate.StartTicks);
                }
            }

            _MoveInfo = candidate;
            if (emit)
                _MoveEvent?.Invoke(_MoveInfo);
            _RecomputeImpact();
        }

        /// <summary>結清所有已到期的撞牆 redirect;每個 redirect 都以撞擊時刻(非當下)為起點,幀率再低也不穿牆。</summary>
        void _ProcessDueRedirects(long now)
        {
            var guard = 0;
            while (_HasPendingHit && now >= _HitTicks)
            {
                if (++guard > MaxRedirectsPerFrame)
                {
                    // 病態幾何(如極窄縫隙)造成單幀鏈式 redirect 過多:強制停住並回報
                    UnityEngine.Debug.LogWarning("[Player] 單幀撞牆 redirect 超過上限,強制停止");
                    _MoveInfo = new MoveInfo
                    {
                        Position = _HitContactPos,
                        Facing = _MoveInfo.Facing,
                        Speed = 0f,
                        StartTicks = _HitTicks
                    };
                    _MoveEvent?.Invoke(_MoveInfo);
                    _HasPendingHit = false;
                    return;
                }

                _MoveInfo = _ProjectOntoTangent(_MoveInfo, _HitNormal, _HitContactPos, _HitTicks);
                _MoveEvent?.Invoke(_MoveInfo);
                _RecomputeImpact();
            }
        }

        /// <summary>沿目前 MoveInfo 的直線路徑掃掠,預算下一個撞牆時刻(解析式 TOI)。</summary>
        void _RecomputeImpact()
        {
            _HasPendingHit = false;
            var terrain = _World.Terrain;
            if (terrain == null || _MoveInfo.Speed <= 0f)
                return;

            if (!terrain.CastSphere(_Radius, _MoveInfo.Position, _MoveInfo.Facing, MaxCastDistance,
                    out var distance, out var normal))
                return;

            // 接觸點沿入射方向回退 Skin:滑行方向與牆平行,之後的掃掠不會重複命中同一面牆
            var travel = Mathf.Max(0f, distance - TerrainQuery.Skin);
            _HitContactPos = _MoveInfo.Position + _MoveInfo.Facing * travel;
            _HitTicks = _MoveInfo.StartTicks + (long)(travel / _MoveInfo.Speed * TimeSpan.TicksPerSecond);
            _HitNormal = normal;
            _HasPendingHit = true;
        }

        /// <summary>
        /// 撞牆回應:把朝向投影到牆面切線續走(速度按切線分量縮放);
        /// 正面撞(切線分量過小)則停止。轉角窗內的第二次 redirect 需同時滿足前一面牆的約束。
        /// </summary>
        MoveInfo _ProjectOntoTangent(MoveInfo info, Vector2 normal, Vector2 contactPos, long ticksAtContact)
        {
            var facing = info.Facing;
            var tangent = facing - Vector2.Dot(facing, normal) * normal;

            if (ticksAtContact - _LastRedirectTicks < CornerWindowTicks)
                tangent -= Vector2.Dot(tangent, _LastRedirectNormal) * _LastRedirectNormal;

            _LastRedirectNormal = normal;
            _LastRedirectTicks = ticksAtContact;

            var tangentLength = tangent.magnitude;
            if (tangentLength < MinTangentSpeedRatio)
            {
                // 停止時保留原朝向:表現上角色仍面對牆,而不是突然轉向
                return new MoveInfo
                {
                    Position = contactPos,
                    Facing = facing,
                    Speed = 0f,
                    StartTicks = ticksAtContact
                };
            }

            return new MoveInfo
            {
                Position = contactPos,
                Facing = tangent / tangentLength,
                Speed = _MoveSpeed * tangentLength,
                StartTicks = ticksAtContact
            };
        }
    }
}
