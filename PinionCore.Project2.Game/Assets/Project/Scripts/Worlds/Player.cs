using System;
using PinionCore.Project2.Shared;
using PinionCore.Remote;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace PinionCore.Project2.Worlds
{
    /// <summary>
    /// 封裝一顆 DOTS entity 的玩家物件:對外(協議)提供 IPlayer/IActor 的檢視與 IMoveable 的控制,
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
        readonly float _SightRadius;
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

        // 視野內角色(含自己);由 World 的 Sight 依「距離 + 地形遮蔽」判定增刪,
        // Enter/Leave 時由 World 直接增刪(self 成員資格也由 World 管)。
        readonly Depot<Player> _VisibleActors;
        readonly Notifier<IActor> _ActorsNotifier;
        Notifier<IActor> IPlayer.Actors => _ActorsNotifier;

        // 供 World 增刪可見角色
        internal Depot<Player> VisibleActors => _VisibleActors;

        // 可移動能力:由 PlayerController 的角色狀態機(Conscious/Unconscious)控制供應,
        // supply = client 可移動,unsupply = 能力收回(秒級,如無意識)。
        // 動作進行中的拒收(毫秒級)仍走 Move/Stop 的動作閘,兩者是不同時間尺度的閘。
        readonly Depot<Player> _Moveables;
        readonly Notifier<IMoveable> _MoveablesNotifier;
        Notifier<IMoveable> IPlayer.Moveable => _MoveablesNotifier;

        // 供 PlayerController 的狀態增刪供應(同 VisibleActors 模式)
        internal Depot<Player> Moveables => _Moveables;

        // 冒險/戰鬥能力:由 Conscious 內的 Adventure/Battle 子狀態互斥供應,
        // 狀態類(AdventureStatus/BattleStatus)自身即協議實作,Enter 供應自己、Leave 收回。
        readonly Depot<IAdventure> _Adventures;
        readonly Notifier<IAdventure> _AdventuresNotifier;
        Notifier<IAdventure> IPlayer.Adventure => _AdventuresNotifier;
        internal Depot<IAdventure> Adventures => _Adventures;

        readonly Depot<IBattle> _Battles;
        readonly Notifier<IBattle> _BattlesNotifier;
        Notifier<IBattle> IPlayer.Battle => _BattlesNotifier;
        internal Depot<IBattle> Battles => _Battles;

        // 權威狀態的唯一真相:等速直線 MoveInfo,任意時刻以 MoveSampler 取樣;
        // entity 的 LocalTransform 只是取樣結果的投影(供未來碰撞查詢使用)。
        MoveInfo _MoveInfo;

        // 自帶位移動作:烘焙成分段等速直線,依絕對 tick 排程逐段發出 MoveInfo。
        // _BoundaryTicks[i] = 第 i 段的結束時刻,StartAction 時一次算定;撞牆不平移排程,
        // 下一段從邊界時刻取樣到的實際位置重新起步(正面撞牆的突進停牆邊,後續段照常)。
        readonly ActionConfig[] _Actions;
        ActionConfig _CurrentAction;     // null = 無動作進行中
        long[] _BoundaryTicks;
        int _NextBoundary;
        // 動作起始朝向基底:段位移以此局部空間(x=右, y=前)轉世界座標;
        // 也是動作結束時恢復的視覺朝向(client 在動作期間凍結旋轉)
        Vector2 _ActionForward;
        Vector2 _ActionRight;
        // 目前直線段的名目速度:撞牆滑行的速度縮放基準(一般移動 = _MoveSpeed,動作段 = 段速度)
        float _BaseSpeed;

        // 供 editor 除錯繪製(WorldDebugDrawer)讀取當前 MoveInfo / 半徑 / 預計撞點
        internal MoveInfo CurrentMoveInfo => _MoveInfo;
        internal float Radius => _Radius;
        internal float SightRadius => _SightRadius;
        internal bool HasPendingHit => _HasPendingHit;
        internal Vector2 PendingHitPosition => _HitContactPos;

        // 供 editor 除錯繪製:動作進行中的預定路徑折線。
        // 目前段沿現行 MoveInfo 外推到段邊界(撞牆滑行/停止已反映),其餘段疊加理想位移;
        // 未來的撞牆不預測 —— 實際軌跡偏離此線即代表牆面干涉。
        internal bool ActionActive => _CurrentAction != null;
        internal System.Collections.Generic.IEnumerable<Vector2> ActionPlannedPath
        {
            get
            {
                if (_CurrentAction == null)
                    yield break;

                _SampleNow(out var position, out _, out _);
                yield return position;

                var boundary = _BoundaryTicks[_NextBoundary];
                var elapsed = (boundary - _MoveInfo.StartTicks) / (double)TimeSpan.TicksPerSecond;
                MoveSampler.Sample(_MoveInfo, Math.Max(0.0, elapsed), out var cursor, out _);
                yield return cursor;

                for (var i = _NextBoundary + 1; i < _CurrentAction.Segments.Length; i++)
                {
                    var offset = _CurrentAction.Segments[i].LocalOffset;
                    cursor += _ActionRight * offset.x + _ActionForward * offset.y;
                    yield return cursor;
                }
            }
        }

        public Player(Guid actorId, ActorInfo info, Unity.Entities.Entity entity, Unity.Entities.EntityManager entityManager, float moveSpeed, float moveAcceptInterval, float radius, float sightRadius, ActionConfig[] actions, Vector3 spawnPosition, World world)
        {
            _World = world;
            Entity = entity;
            _EntityManager = entityManager;
            _MoveSpeed = moveSpeed;
            _MoveAcceptIntervalTicks = (long)(moveAcceptInterval * TimeSpan.TicksPerSecond);
            _Radius = radius;
            _SightRadius = sightRadius;
            _Actions = actions ?? Array.Empty<ActionConfig>();
            _BaseSpeed = moveSpeed;
            _LastMoveAcceptedTicks = long.MinValue / 4;
            _LastRedirectTicks = long.MinValue / 4;
            _VisibleActors = new Depot<Player>();
            _ActorsNotifier = _VisibleActors.ToNotifier<IActor>();
            _Moveables = new Depot<Player>();
            _MoveablesNotifier = _Moveables.ToNotifier<IMoveable>();
            _Adventures = new Depot<IAdventure>();
            _AdventuresNotifier = _Adventures.ToNotifier<IAdventure>();
            _Battles = new Depot<IBattle>();
            _BattlesNotifier = _Battles.ToNotifier<IBattle>();
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

        // 冒險/戰鬥狀態:與 MoveEvent 同樣「訂閱即 replay」,晚訂閱的殼立即取得當前狀態
        StatusType _Status;
        event Action<StatusType> _StatusEvent;
        event Action<StatusType> IActor.StatusEvent
        {
            add
            {
                _StatusEvent += value;
                value(_Status);
            }
            remove
            {
                _StatusEvent -= value;
            }
        }

        // 動作播放狀態:與 Move/StatusEvent 同款「訂閱即 replay」;Action == None 表示無動作,
        // 結束時也以 None 發出 —— 這是 client 解除旋轉凍結的唯一權威訊號。
        ActionInfo _ActionInfo;
        event Action<ActionInfo> _ActionEvent;
        event Action<ActionInfo> IActor.ActionEvent
        {
            add
            {
                _ActionEvent += value;
                value(_ActionInfo);
            }
            remove
            {
                _ActionEvent -= value;
            }
        }

        void _SetActionInfo(ActionInfo info)
        {
            _ActionInfo = info;
            _ActionEvent?.Invoke(info);
        }

        /// <summary>
        /// 開始播放自帶位移動作。force = false 為玩家觸發路徑(進行中不可重入);
        /// force = true 供伺服器主動覆蓋(僵直/死亡等,未來傷害管線使用):
        /// 直接作廢舊排程,發新 ActionInfo 即同時取代 replay 值,client 收到即換動畫。
        /// </summary>
        internal bool StartAction(ActionType action, bool force)
        {
            if (action == ActionType.None)
                return false;

            ActionConfig config = null;
            foreach (var c in _Actions)
            {
                if (c != null && c.Action == action)
                {
                    config = c;
                    break;
                }
            }
            if (config == null || config.Segments == null || config.Segments.Length == 0)
                return false;

            // 先結清到期事件:可能包含「動作剛好已結束」,避免誤判進行中
            _ProcessDueRedirects(_World.ElapsedTicks);
            if (_CurrentAction != null && !force)
                return false;

            _SampleNow(out var position, out var facing, out var now);

            // 覆蓋進行中的動作時,基底沿用原動作的視覺朝向(當前段的速度方向可能是側移/滑行方向)
            var forward = _CurrentAction != null ? _ActionForward : facing;
            _CurrentAction = config;
            _ActionForward = forward;
            _ActionRight = new Vector2(forward.y, -forward.x);

            _BoundaryTicks = new long[config.Segments.Length];
            var boundary = now;
            for (var i = 0; i < config.Segments.Length; i++)
            {
                // 以 double 計 tick,避免 float 乘法在長時長時掉精度
                boundary += (long)(Math.Max(0.0, config.Segments[i].Duration) * TimeSpan.TicksPerSecond);
                _BoundaryTicks[i] = boundary;
            }
            _NextBoundary = 0;

            _SetActionInfo(new ActionInfo { Action = action, StartTicks = now });
            _StartSegment(0, position, now);
            return true;
        }

        /// <summary>以段的局部位移(動作起始朝向基底)組出等速直線 MoveInfo 並提交;零位移段 = 原地駐留。</summary>
        void _StartSegment(int index, Vector2 position, long startTicks)
        {
            var segment = _CurrentAction.Segments[index];
            var offset = _ActionRight * segment.LocalOffset.x + _ActionForward * segment.LocalOffset.y;
            var distance = offset.magnitude;

            MoveInfo candidate;
            if (distance <= 1e-6f || segment.Duration <= 0f)
            {
                _BaseSpeed = 0f;
                candidate = new MoveInfo
                {
                    Position = position,
                    Facing = _ActionForward,
                    Speed = 0f,
                    StartTicks = startTicks
                };
            }
            else
            {
                var speed = distance / segment.Duration;
                _BaseSpeed = speed;   // 撞牆滑行以段速度為基準,不是走路速度
                candidate = new MoveInfo
                {
                    Position = position,
                    Facing = offset / distance,
                    Speed = speed,
                    StartTicks = startTicks
                };
            }
            _SetMoveInfo(candidate, emit: true);
        }

        /// <summary>動作結束:發終停 MoveInfo(恢復視覺朝向)與 ActionInfo.None,解除移動鎖定。</summary>
        void _EndAction(Vector2 position, long tick)
        {
            _CurrentAction = null;
            _BoundaryTicks = null;
            _BaseSpeed = _MoveSpeed;
            _SetMoveInfo(new MoveInfo
            {
                Position = position,
                Facing = _ActionForward,
                Speed = 0f,
                StartTicks = tick
            }, emit: true);
            _SetActionInfo(new ActionInfo { Action = ActionType.None, StartTicks = tick });
        }

        // 以當下時間取樣權威位置/朝向,作為新 MoveInfo 的起點。
        void _SampleNow(out Vector2 position, out Vector2 facing, out long now)
        {
            now = _World.ElapsedTicks;
            var elapsed = (now - _MoveInfo.StartTicks) / (double)TimeSpan.TicksPerSecond;
            MoveSampler.Sample(_MoveInfo, elapsed, out position, out facing);
        }

        Value<bool> IMoveable.Move(Vector2 direction)
        {
            if (_MoveSpeed <= 0f || direction.sqrMagnitude <= 1e-6f)
                return false;

            // Move 節流:距上次被接受的 Move 未滿間隔即拒收;Stop 不受此限
            if (_World.ElapsedTicks - _LastMoveAcceptedTicks < _MoveAcceptIntervalTicks)
                return false;

            // 取樣前先結清已到期的撞牆 redirect,避免以穿牆的外推位置當新起點
            _ProcessDueRedirects(_World.ElapsedTicks);

            // 動作進行中位移權威屬於動作排程,玩家移動輸入一律拒收(結清後動作可能剛好結束)
            if (_CurrentAction != null)
                return false;

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

        Value<bool> IMoveable.Stop()
        {
            _ProcessDueRedirects(_World.ElapsedTicks);
            if (_CurrentAction != null)
                return false;
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

        /// <summary>
        /// 結清所有已到期的事件:撞牆 redirect 與動作段邊界按時間順序交錯處理,
        /// 每個事件都以其發生時刻(非當下)為新 MoveInfo 起點,幀率再低也不穿牆、段時長也不漂移。
        /// </summary>
        void _ProcessDueRedirects(long now)
        {
            var redirectGuard = 0;
            while (true)
            {
                var nextHit = _HasPendingHit ? _HitTicks : long.MaxValue;
                var nextBoundary = _CurrentAction != null ? _BoundaryTicks[_NextBoundary] : long.MaxValue;
                if (nextHit > now && nextBoundary > now)
                    return;

                if (nextHit <= nextBoundary)
                {
                    if (++redirectGuard > MaxRedirectsPerFrame)
                    {
                        // 病態幾何(如極窄縫隙)造成單幀鏈式 redirect 過多:強制停住並回報;
                        // 動作段邊界不受影響,之後的 Update 照常結清
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
                else
                {
                    // 段邊界到期:以邊界時刻取樣實際位置(撞牆滑行/停止都已反映在 _MoveInfo)起下一段
                    var elapsed = (nextBoundary - _MoveInfo.StartTicks) / (double)TimeSpan.TicksPerSecond;
                    MoveSampler.Sample(_MoveInfo, elapsed, out var position, out _);
                    if (_NextBoundary == _CurrentAction.Segments.Length - 1)
                    {
                        _EndAction(position, nextBoundary);
                    }
                    else
                    {
                        _NextBoundary++;
                        _StartSegment(_NextBoundary, position, nextBoundary);
                    }
                }
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
                // 以目前段的名目速度縮放(一般移動 = 走速;動作段 = 段速度,遠快於走速也不會被錯誤重設)
                Speed = _BaseSpeed * tangentLength,
                StartTicks = ticksAtContact
            };
        }

        // 由 Adventure/Battle 狀態的 Enter 呼叫,經 IActor.StatusEvent 廣播給所有看得到的 client
        internal void SetStatus(StatusType status)
        {
            _Status = status;
            _StatusEvent?.Invoke(status);
        }
    }
}
