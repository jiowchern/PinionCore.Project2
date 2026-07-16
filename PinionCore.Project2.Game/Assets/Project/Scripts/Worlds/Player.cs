using System;
using PinionCore.Project2.Shared;
using PinionCore.Remote;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace PinionCore.Project2.Worlds
{
    /// <summary>
    /// 封裝一顆 DOTS entity 的玩家純模擬核心:MoveInfo 權威狀態、碰撞、動作排程與 transform 投影。
    /// 協議曝光面(ICharactor)由 PlayerController 承載並委派到此處的公開成員;
    /// entity 的建立與銷毀由 World 負責,Player 只持有參考。
    /// </summary>
    public class Player
    {
        // World 在 Leave 時據此銷毀 entity。
        public readonly Unity.Entities.Entity Entity;

        readonly Unity.Entities.EntityManager _EntityManager;
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

        // 權威狀態的唯一真相:等速直線 MoveInfo,任意時刻以 MoveSampler 取樣;
        // entity 的 LocalTransform 只是取樣結果的投影(供未來碰撞查詢使用)。
        MoveInfo _MoveInfo;

        // 自帶位移動作:烘焙成分段等速直線,依絕對 tick 排程逐段發出 MoveInfo。
        // _BoundaryTicks[i] = 第 i 段的結束時刻,StartAction 時一次算定;撞牆不平移排程,
        // 下一段從邊界時刻取樣到的實際位置重新起步(正面撞牆的突進停牆邊,後續段照常)。
        readonly ActionConfig[] _Actions;
        // 走路(Locomotion)動作:Move 啟動、循環播放直到 Stop / 被 Cast 打斷;
        // null = 此角色無走路動作,Move 一律拒收。
        readonly ActionConfig _LocomotionConfig;
        ActionConfig _CurrentAction;     // null = 無動作進行中
        long[] _BoundaryTicks;
        int _NextBoundary;
        // 動作起始朝向基底:段位移以此局部空間(x=右, y=前)轉世界座標;
        // 也是動作結束時恢復的視覺朝向(client 在動作期間凍結旋轉)
        Vector2 _ActionForward;
        Vector2 _ActionRight;
        // 目前直線段的名目速度:撞牆滑行的速度縮放基準(= 現行動作段的段速度)
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

        public Player(Guid actorId, ActorInfo info, Unity.Entities.Entity entity, Unity.Entities.EntityManager entityManager, float moveAcceptInterval, float radius, float sightRadius, ActionConfig[] actions, Vector3 spawnPosition, World world)
        {
            _World = world;
            Entity = entity;
            _EntityManager = entityManager;
            _MoveAcceptIntervalTicks = (long)(moveAcceptInterval * TimeSpan.TicksPerSecond);
            _Radius = radius;
            _SightRadius = sightRadius;
            _Actions = actions ?? Array.Empty<ActionConfig>();
            // 快取走路動作:需有段資料且總循環時長 > 0(零時長循環會無限 wrap)
            foreach (var c in _Actions)
            {
                if (c == null || c.Category != ActionCategory.Locomotion ||
                    c.Segments == null || c.Segments.Length == 0)
                    continue;
                var total = 0.0;
                foreach (var segment in c.Segments)
                    total += Math.Max(0.0, segment.Duration);
                if (total * TimeSpan.TicksPerSecond < 1)
                    continue;
                _LocomotionConfig = c;
                break;
            }
            _LastMoveAcceptedTicks = long.MinValue / 4;
            _LastRedirectTicks = long.MinValue / 4;
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
        public event Action<MoveInfo> MoveEvent
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
        public event Action<StatusType> StatusEvent
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
        public event Action<ActionInfo> ActionEvent
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
            // 走路(Locomotion)可被 Cast 打斷:發新 ActionInfo 直接取代,不發中間 None;
            // Cast 進行中且非 force 仍不可重入
            if (_CurrentAction != null && !force &&
                !(_CurrentAction.Category == ActionCategory.Locomotion && config.Category == ActionCategory.Cast))
                return false;

            _SampleNow(out var position, out var facing, out var now);

            // 覆蓋進行中的動作時,基底沿用原動作的視覺朝向(當前段的速度方向可能是側移/滑行方向);
            // 走路的 _ActionForward 即移動指令方向,被攻擊打斷時攻擊自然朝走路方向
            var forward = _CurrentAction != null ? _ActionForward : facing;
            _CurrentAction = config;
            _ActionForward = forward;
            _ActionRight = new Vector2(forward.y, -forward.x);
            _ScheduleBoundaries(config, now);

            _SetActionInfo(new ActionInfo { Action = action, StartTicks = now });
            _StartSegment(0, position, now);
            return true;
        }

        /// <summary>一次算定所有段邊界的絕對 tick(now + 累計段時長),並重置段游標。</summary>
        void _ScheduleBoundaries(ActionConfig config, long now)
        {
            if (_BoundaryTicks == null || _BoundaryTicks.Length != config.Segments.Length)
                _BoundaryTicks = new long[config.Segments.Length];
            var boundary = now;
            for (var i = 0; i < config.Segments.Length; i++)
            {
                // 以 double 計 tick,避免 float 乘法在長時長時掉精度
                boundary += (long)(Math.Max(0.0, config.Segments[i].Duration) * TimeSpan.TicksPerSecond);
                _BoundaryTicks[i] = boundary;
            }
            _NextBoundary = 0;
        }

        /// <summary>
        /// 以段的局部位移(動作起始朝向基底)組出等速直線 MoveInfo 並提交;零位移段 = 原地駐留。
        /// suppressRedundant:新段與現行 MoveInfo 的外推同向同速且位置吻合時不 emit
        /// (走路循環 wrap 用,直線走路每循環網路成本趨近 0;訂閱 replay 送內部狀態,語意等價)。
        /// </summary>
        void _StartSegment(int index, Vector2 position, long startTicks, bool suppressRedundant = false)
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
            _SetMoveInfo(candidate, emit: !suppressRedundant || !_MatchesExtrapolation(candidate));
        }

        /// <summary>candidate 是否與現行 MoveInfo 的外推等價(同向同速、位置吻合,容差 1e-4 公尺)。</summary>
        bool _MatchesExtrapolation(in MoveInfo candidate)
        {
            if (Mathf.Abs(candidate.Speed - _MoveInfo.Speed) > 1e-5f)
                return false;
            if ((candidate.Facing - _MoveInfo.Facing).sqrMagnitude > 1e-8f)
                return false;
            var elapsed = (candidate.StartTicks - _MoveInfo.StartTicks) / (double)TimeSpan.TicksPerSecond;
            if (elapsed < 0)
                return false;
            MoveSampler.Sample(_MoveInfo, elapsed, out var extrapolated, out _);
            return (candidate.Position - extrapolated).sqrMagnitude <= 1e-8f;
        }

        /// <summary>動作結束:發終停 MoveInfo(恢復視覺朝向)與 ActionInfo.None,解除移動鎖定。</summary>
        void _EndAction(Vector2 position, long tick)
        {
            _CurrentAction = null;
            _BoundaryTicks = null;
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

        public Value<bool> Move(Vector2 direction)
        {
            if (direction.sqrMagnitude <= 1e-6f)
                return false;

            // Move 節流:距上次被接受的 Move 未滿間隔即拒收;Stop 不受此限
            if (_World.ElapsedTicks - _LastMoveAcceptedTicks < _MoveAcceptIntervalTicks)
                return false;

            // 取樣前先結清已到期的撞牆 redirect,避免以穿牆的外推位置當新起點
            _ProcessDueRedirects(_World.ElapsedTicks);

            // Cast 動作進行中位移權威屬於動作排程,玩家移動輸入一律拒收(結清後動作可能剛好結束)
            if (_CurrentAction != null && _CurrentAction.Category != ActionCategory.Locomotion)
                return false;

            if (_CurrentAction != null)
            {
                // 走路中重定向:換朝向基底、以當下取樣位置重發現行段。
                // 段內等速 → 邊界 tick 不動,剩餘距離 = 名目速度 × 剩餘時間自動成立;
                // 不發 ActionInfo,client 動畫連續不重播。
                _SampleNow(out var current, out _, out var tick);
                _ActionForward = direction.normalized;
                _ActionRight = new Vector2(_ActionForward.y, -_ActionForward.x);
                _LastMoveAcceptedTicks = tick;
                _StartSegment(_NextBoundary, current, tick);
                return true;
            }

            // 無走路動作的角色不能移動:位移權威一律來自烘焙的 root motion 排程
            if (_LocomotionConfig == null)
                return false;

            // 啟動走路:同 StartAction 排程,但朝向基底 = 指令方向(非取樣朝向);
            // 循環直到 Stop / 被 Cast 打斷,速度由烘焙的 root motion 決定。
            _SampleNow(out var start, out _, out var startTick);
            _CurrentAction = _LocomotionConfig;
            _ActionForward = direction.normalized;
            _ActionRight = new Vector2(_ActionForward.y, -_ActionForward.x);
            _ScheduleBoundaries(_LocomotionConfig, startTick);
            _LastMoveAcceptedTicks = startTick;
            _SetActionInfo(new ActionInfo { Action = _LocomotionConfig.Action, StartTicks = startTick });
            _StartSegment(0, start, startTick);
            return true;
        }

        public Value<bool> Stop()
        {
            _ProcessDueRedirects(_World.ElapsedTicks);
            if (_CurrentAction != null)
            {
                // Cast 不能被玩家停止;走路 = 結束循環(發 None + Speed=0,
                // 終停朝向 = _ActionForward = 移動指令方向,語意剛好)
                if (_CurrentAction.Category != ActionCategory.Locomotion)
                    return false;
                _SampleNow(out var current, out _, out var tick);
                _EndAction(current, tick);
                return true;
            }
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
        /// 結束進行中的走路動作(Cast 不受影響):供狀態機在收回移動能力(無意識等)時呼叫,
        /// 避免能力已收回但角色繼續循環走路。
        /// </summary>
        internal void StopLocomotion()
        {
            _ProcessDueRedirects(_World.ElapsedTicks);
            if (_CurrentAction == null || _CurrentAction.Category != ActionCategory.Locomotion)
                return;
            _SampleNow(out var position, out _, out var now);
            _EndAction(position, now);
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
                        if (_CurrentAction.Category == ActionCategory.Locomotion)
                        {
                            // 循環 wrap:以本輪最後邊界時刻(非 now)為下一輪基準,零漂移;
                            // 朝向基底不變、不重發 ActionInfo(client 的 loop 動畫自己循環)。
                            // 重排後若時間軸無前進(零時長循環)則結束,防無限 wrap。
                            _ScheduleBoundaries(_CurrentAction, nextBoundary);
                            if (_BoundaryTicks[_BoundaryTicks.Length - 1] > nextBoundary)
                                _StartSegment(0, position, nextBoundary, suppressRedundant: true);
                            else
                                _EndAction(position, nextBoundary);
                        }
                        else
                        {
                            _EndAction(position, nextBoundary);
                        }
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
