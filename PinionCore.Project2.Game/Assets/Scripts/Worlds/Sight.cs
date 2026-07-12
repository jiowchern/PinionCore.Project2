using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace PinionCore.Project2.Worlds
{
    /// <summary>
    /// 每個 World 一份的視野評估器:Burst job 算原始可見性(距離 + 地形遮蔽),
    /// 主執行緒套 hysteresis(進/出半徑 + 移除去彈跳)後增刪各 Player 的 VisibleActors。
    /// 自己對自己的成員資格不在此管理,由 World 的 Enter/Leave 直接處理。
    /// </summary>
    class Sight
    {
        /// <summary>視野判定節流間隔(秒);World.Update 依此驅動 Tick。</summary>
        public const float UpdateIntervalSeconds = 0.1f;

        /// <summary>離開半徑 = SightRadius × 此係數:已可見的配對要超出此距離才算失去,防邊界震盪。</summary>
        public const float ExitRadiusFactor = 1.1f;

        /// <summary>可見配對需連續失敗此次數才移除(吸收牆角遮蔽閃爍);加入則首次可見即生效。</summary>
        public const int InvisibleDebounceTicks = 3;

        // 僅「目前可見但連續判定失敗」的 (observer, target) 有 entry;成功、移除或 Forget 時清除
        readonly Dictionary<(Guid Observer, Guid Target), int> _InvisibleStreaks
            = new Dictionary<(Guid Observer, Guid Target), int>();

        /// <summary>
        /// 對 players 全體做一次視野判定並增刪 VisibleActors。
        /// 呼叫前各 entity 的 LocalTransform 必須已由 Player.Update 投影到最新位置。
        /// </summary>
        public void Tick(IReadOnlyList<Player> players, EntityManager entityManager, TerrainQuery terrain)
        {
            var n = players.Count;
            if (n <= 1)
                return;

            var positions = new NativeArray<float2>(n, Allocator.TempJob);
            var rayHeights = new NativeArray<float>(n, Allocator.TempJob);
            var sightRadii = new NativeArray<float>(n, Allocator.TempJob);
            var prevVisible = new NativeArray<bool>(n * n, Allocator.TempJob);
            var rawVisible = new NativeArray<bool>(n * n, Allocator.TempJob);

            for (var i = 0; i < n; i++)
            {
                var player = players[i];
                var transform = entityManager.GetComponentData<LocalTransform>(player.Entity);
                positions[i] = new float2(transform.Position.x, transform.Position.z);
                rayHeights[i] = player.Radius;   // 球心高度慣例:與碰撞查詢同高
                sightRadii[i] = player.SightRadius;
                for (var j = 0; j < n; j++)
                    prevVisible[i * n + j] = i != j && player.VisibleActors.Items.Contains(players[j]);
            }

            var job = new SightJob
            {
                PositionsXZ = positions,
                RayHeights = rayHeights,
                SightRadii = sightRadii,
                PrevVisible = prevVisible,
                RawVisible = rawVisible,
                Terrain = terrain != null ? terrain.TerrainBlob : default,
                TerrainFromWorld = terrain != null ? terrain.TerrainFromWorld : RigidTransform.identity,
                Filter = TerrainQuery.ActorObstacleFilter(),
                ExitFactor = ExitRadiusFactor,
            };
            job.Run();

            for (var i = 0; i < n; i++)
            {
                for (var j = 0; j < n; j++)
                {
                    if (i == j)
                        continue;

                    var raw = rawVisible[i * n + j];
                    var prev = prevVisible[i * n + j];
                    Guid observerId = players[i].ActorId;
                    Guid targetId = players[j].ActorId;
                    var key = (observerId, targetId);
                    if (raw)
                    {
                        _InvisibleStreaks.Remove(key);
                        if (!prev)
                            players[i].VisibleActors.Items.Add(players[j]);
                        continue;
                    }

                    if (!prev)
                        continue;

                    _InvisibleStreaks.TryGetValue(key, out var streak);
                    streak++;
                    if (streak >= InvisibleDebounceTicks)
                    {
                        players[i].VisibleActors.Items.Remove(players[j]);
                        _InvisibleStreaks.Remove(key);
                    }
                    else
                    {
                        _InvisibleStreaks[key] = streak;
                    }
                }
            }

            positions.Dispose();
            rayHeights.Dispose();
            sightRadii.Dispose();
            prevVisible.Dispose();
            rawVisible.Dispose();
        }

        /// <summary>玩家離開世界時清掉與其相關的 streak(observer 與 target 兩向)。</summary>
        public void Forget(Player player)
        {
            Guid id = player.ActorId;
            var stale = new List<(Guid, Guid)>();
            foreach (var key in _InvisibleStreaks.Keys)
            {
                if (key.Observer == id || key.Target == id)
                    stale.Add(key);
            }
            foreach (var key in stale)
                _InvisibleStreaks.Remove(key);
        }
    }

    /// <summary>
    /// 原始可見性計算:每個有序對 (i,j) 依「半徑閘(進/出半徑由 PrevVisible 決定)→ LOS raycast」判定。
    /// LOS 對稱,每個無序對只 raycast 一次。同點(距離平方 &lt; 1e-6)跳過 raycast 視為可見。
    /// 必須以 Run() 同步執行——blob 由 World 持有,若日後改跨幀 Schedule,
    /// World.Dispose 前必須先 Complete job handle。
    /// </summary>
    [BurstCompile]
    struct SightJob : IJob
    {
        [ReadOnly] public NativeArray<float2> PositionsXZ;
        [ReadOnly] public NativeArray<float> RayHeights;    // = 各自碰撞 Radius(球心高度)
        [ReadOnly] public NativeArray<float> SightRadii;
        [ReadOnly] public NativeArray<bool> PrevVisible;    // n×n row-major:i 目前是否看得到 j
        public NativeArray<bool> RawVisible;                // 輸出 n×n;自己配對維持 false
        [ReadOnly] public BlobAssetReference<Unity.Physics.Collider> Terrain;
        public RigidTransform TerrainFromWorld;
        public Unity.Physics.CollisionFilter Filter;
        public float ExitFactor;

        public void Execute()
        {
            var n = PositionsXZ.Length;
            for (var i = 0; i < n; i++)
            {
                for (var j = i + 1; j < n; j++)
                {
                    var distSq = math.lengthsq(PositionsXZ[j] - PositionsXZ[i]);
                    var inRangeIJ = distSq <= _RangeSq(i, j, n);
                    var inRangeJI = distSq <= _RangeSq(j, i, n);
                    if (!inRangeIJ && !inRangeJI)
                        continue;   // RawVisible 預設 false

                    var blocked = distSq >= 1e-6f && _LineBlocked(i, j);
                    RawVisible[i * n + j] = inRangeIJ && !blocked;
                    RawVisible[j * n + i] = inRangeJI && !blocked;
                }
            }
        }

        float _RangeSq(int observer, int target, int n)
        {
            var radius = SightRadii[observer];
            if (PrevVisible[observer * n + target])
                radius *= ExitFactor;
            return radius * radius;
        }

        bool _LineBlocked(int i, int j)
        {
            if (!Terrain.IsCreated)
                return false;

            var start = new float3(PositionsXZ[i].x, RayHeights[i], PositionsXZ[i].y);
            var end = new float3(PositionsXZ[j].x, RayHeights[j], PositionsXZ[j].y);
            var input = new Unity.Physics.RaycastInput
            {
                Start = math.transform(TerrainFromWorld, start),
                End = math.transform(TerrainFromWorld, end),
                Filter = Filter,
            };
            return Terrain.Value.CastRay(input);
        }
    }
}
