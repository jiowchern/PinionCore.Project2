using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace PinionCore.Project2.Worlds
{
    /// <summary>
    /// 對地形 compound collider blob 的碰撞查詢服務。
    /// 不建 CollisionWorld/broadphase:地形只有一顆靜態 compound,直接對 blob 查詢即可。
    /// 角色以「XZ 平面上的圓」抽象:實際用球(中心高度 = 半徑)做掃掠/距離查詢,
    /// filter 只跟 Obstacle 層相撞,地板(Ground 層)永不成為候選命中。
    /// </summary>
    class TerrainQuery : IDisposable
    {
        /// <summary>接觸點與牆面保持的安全間隙,防止滑行時反覆重新命中同一面牆。</summary>
        public const float Skin = 0.02f;

        // 地形 blob 由 World 持有並釋放,這裡只是借用
        readonly Unity.Entities.BlobAssetReference<Unity.Physics.Collider> _Terrain;
        readonly RigidTransform _WorldFromTerrain;
        readonly RigidTransform _TerrainFromWorld;

        // 去穿透用的膠囊高度:深嵌牆中時,球的最近分離面可能是牆的頂/底面(垂直法線,XZ 推不動);
        // 改用由角色高度往上延伸的高膠囊,讓側面成為最近分離面,法線必為水平。
        // 代價:比牆頂還高的懸空幾何(若未來有)也會被此查詢視為障礙。
        const float DepenetrationCapsuleHeight = 50f;

        // 查詢形狀依半徑快取;由本類持有,Dispose 時釋放
        readonly Dictionary<float, Unity.Entities.BlobAssetReference<Unity.Physics.Collider>> _SpheresByRadius
            = new Dictionary<float, Unity.Entities.BlobAssetReference<Unity.Physics.Collider>>();
        readonly Dictionary<float, Unity.Entities.BlobAssetReference<Unity.Physics.Collider>> _CapsulesByRadius
            = new Dictionary<float, Unity.Entities.BlobAssetReference<Unity.Physics.Collider>>();

        public TerrainQuery(Unity.Entities.BlobAssetReference<Unity.Physics.Collider> terrain, RigidTransform worldFromTerrain)
        {
            _Terrain = terrain;
            _WorldFromTerrain = worldFromTerrain;
            _TerrainFromWorld = math.inverse(worldFromTerrain);
        }

        public void Dispose()
        {
            foreach (var sphere in _SpheresByRadius.Values)
            {
                if (sphere.IsCreated)
                    sphere.Dispose();
            }
            _SpheresByRadius.Clear();

            foreach (var capsule in _CapsulesByRadius.Values)
            {
                if (capsule.IsCreated)
                    capsule.Dispose();
            }
            _CapsulesByRadius.Clear();
        }

        /// <summary>
        /// 以半徑 radius 的球沿 XZ 方向掃掠,回報最近的障礙(Obstacle 層)命中。
        /// hitNormalXZ 為世界空間、指向角色側的牆面法線(已投影到 XZ 並歸一)。
        /// </summary>
        public bool CastSphere(float radius, Vector2 startXZ, Vector2 dirXZ, float maxDistance,
            out float hitDistance, out Vector2 hitNormalXZ)
        {
            hitDistance = 0f;
            hitNormalXZ = Vector2.zero;
            if (!_Terrain.IsCreated || maxDistance <= 0f)
                return false;

            var worldStart = new float3(startXZ.x, radius, startXZ.y);
            var worldEnd = worldStart + new float3(dirXZ.x, 0f, dirXZ.y) * maxDistance;
            var input = new Unity.Physics.ColliderCastInput(
                _GetSphere(radius),
                math.transform(_TerrainFromWorld, worldStart),
                math.transform(_TerrainFromWorld, worldEnd),
                quaternion.identity);

            if (!_Terrain.Value.CastCollider(input, out Unity.Physics.ColliderCastHit hit))
                return false;

            // 命中結果在地形空間,法線轉回世界空間
            var worldNormal = math.rotate(_WorldFromTerrain.rot, hit.SurfaceNormal);
            // Filter 已排除地板;此斷言防未來把斜面誤掛成「牆」的作圖錯誤
            UnityEngine.Debug.Assert(math.abs(worldNormal.y) < 0.6f,
                $"[TerrainQuery] 障礙命中法線接近垂直({worldNormal}),Obstacle 層不應包含地板/斜坡");

            var normalXZ = new Vector2(worldNormal.x, worldNormal.z);
            if (normalXZ.sqrMagnitude <= 1e-8f)
                return false;   // 純垂直法線(理論上不會發生):視為未命中

            hitDistance = hit.Fraction * maxDistance;
            hitNormalXZ = normalXZ.normalized;
            return true;
        }

        /// <summary>
        /// 檢查 posXZ 是否嵌進障礙;是則回傳把角色推出所需的位移(世界 XZ,含 Skin 間隙)。
        /// </summary>
        public bool ComputePenetration(float radius, Vector2 posXZ, out Vector2 pushOutXZ)
        {
            pushOutXZ = Vector2.zero;
            if (!_Terrain.IsCreated)
                return false;

            var worldPos = new float3(posXZ.x, radius, posXZ.y);
            var input = new Unity.Physics.ColliderDistanceInput(
                _GetCapsule(radius),
                maxDistance: 0f,
                new RigidTransform(quaternion.identity, math.transform(_TerrainFromWorld, worldPos)));

            if (!_Terrain.Value.CalculateDistance(input, out Unity.Physics.DistanceHit hit) || hit.Distance >= 0f)
                return false;

            var worldNormal = math.rotate(_WorldFromTerrain.rot, hit.SurfaceNormal);
            var normalXZ = new Vector2(worldNormal.x, worldNormal.z);
            if (normalXZ.sqrMagnitude <= 1e-8f)
                return false;

            pushOutXZ = normalXZ.normalized * (-hit.Distance + Skin);
            return true;
        }

        Unity.Entities.BlobAssetReference<Unity.Physics.Collider> _GetSphere(float radius)
        {
            if (_SpheresByRadius.TryGetValue(radius, out var sphere))
                return sphere;

            sphere = Unity.Physics.SphereCollider.Create(
                new Unity.Physics.SphereGeometry { Center = float3.zero, Radius = radius },
                _ActorFilter(),
                Unity.Physics.Material.Default);
            _SpheresByRadius.Add(radius, sphere);
            return sphere;
        }

        Unity.Entities.BlobAssetReference<Unity.Physics.Collider> _GetCapsule(float radius)
        {
            if (_CapsulesByRadius.TryGetValue(radius, out var capsule))
                return capsule;

            capsule = Unity.Physics.CapsuleCollider.Create(
                new Unity.Physics.CapsuleGeometry
                {
                    Vertex0 = float3.zero,
                    Vertex1 = new float3(0f, DepenetrationCapsuleHeight, 0f),
                    Radius = radius,
                },
                _ActorFilter(),
                Unity.Physics.Material.Default);
            _CapsulesByRadius.Add(radius, capsule);
            return capsule;
        }

        static Unity.Physics.CollisionFilter _ActorFilter()
        {
            return new Unity.Physics.CollisionFilter
            {
                BelongsTo = CollisionLayers.Actor,
                CollidesWith = CollisionLayers.Obstacle,
                GroupIndex = 0,
            };
        }
    }
}
