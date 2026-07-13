using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace PinionCore.Project2.Worlds
{
    /// <summary>
    /// 碰撞層約定:烘焙與查詢共用的 CollisionFilter 位元。
    /// 地形 prefab 根節點的 collider = Ground(地板,移動查詢不理會),
    /// 子物件的非 trigger collider = Obstacle(牆等障礙,阻擋移動)。
    /// </summary>
    static class CollisionLayers
    {
        public const uint Ground = 1u << 0;
        public const uint Obstacle = 1u << 1;
        public const uint Actor = 1u << 2;
    }

    /// <summary>
    /// 把地形 prefab(根+子物件)的所有非 trigger collider 烘成單一 CompoundCollider blob。
    /// scale 直接烘進幾何(compound 子節點的 RigidTransform 不能帶縮放)。
    /// </summary>
    static class TerrainColliderBaker
    {
        /// <summary>供 WorldDebugDrawer 繪製個別子形狀(compound 內部結構不易走訪,烘焙時順手記下)。</summary>
        public struct TerrainDebugShape
        {
            public RigidTransform CompoundFromChild;
            public Unity.Physics.Aabb LocalAabb;   // 子 collider 自身空間的 AABB
            public bool IsGround;
        }

        /// <summary>
        /// 回傳 compound collider blob;沒有任何可用 collider 時回傳 default(caller 需檢查 IsCreated)。
        /// debugShapesOut 可為 null。
        /// </summary>
        public static Unity.Entities.BlobAssetReference<Unity.Physics.Collider> Bake(
            GameObject prefab, List<TerrainDebugShape> debugShapesOut)
        {
            var children = new List<Unity.Physics.CompoundCollider.ColliderBlobInstance>();
            var childIsGround = new List<bool>();
            var rootTransform = prefab.transform;

            foreach (var source in prefab.GetComponentsInChildren<UnityEngine.Collider>(includeInactive: false))
            {
                if (source.isTrigger)
                    continue;

                var isGround = source.transform == rootTransform;
                var filter = new Unity.Physics.CollisionFilter
                {
                    BelongsTo = isGround ? CollisionLayers.Ground : CollisionLayers.Obstacle,
                    CollidesWith = ~0u,
                    GroupIndex = 0,
                };

                // 子物件相對根的變換;scale 由此矩陣萃取後烘進幾何
                Matrix4x4 rootFromChild = rootTransform.worldToLocalMatrix * source.transform.localToWorldMatrix;
                var childRot = rootFromChild.rotation;

                Unity.Entities.BlobAssetReference<Unity.Physics.Collider> blob;
                RigidTransform compoundFromChild;
                switch (source)
                {
                    case UnityEngine.BoxCollider box:
                        {
                            // 縮放進 Size;collider.center 經完整矩陣(含縮放)轉到根空間當平移
                            var scale = rootFromChild.lossyScale;
                            var size = Vector3.Scale(box.size, scale);
                            var center = rootFromChild.MultiplyPoint(box.center);
                            blob = Unity.Physics.BoxCollider.Create(
                                new Unity.Physics.BoxGeometry
                                {
                                    Center = float3.zero,
                                    Orientation = quaternion.identity,
                                    Size = new float3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z)),
                                    BevelRadius = math.min(0.05f, 0.5f * math.cmin(math.abs((float3)(Vector3)size))),
                                },
                                filter, Unity.Physics.Material.Default);
                            compoundFromChild = new RigidTransform(childRot, center);
                            break;
                        }
                    case UnityEngine.MeshCollider meshCollider when meshCollider.sharedMesh != null:
                        {
                            blob = _BakeMesh(meshCollider.sharedMesh, rootFromChild, filter);
                            // 頂點已含完整變換,子節點放單位變換即可
                            compoundFromChild = RigidTransform.identity;
                            break;
                        }
                    default:
                        Debug.LogWarning($"[TerrainColliderBaker] 尚不支援的 collider 型別,略過:{source.GetType().Name} on {source.name}");
                        continue;
                }

                children.Add(new Unity.Physics.CompoundCollider.ColliderBlobInstance
                {
                    CompoundFromChild = compoundFromChild,
                    Collider = blob,
                });
                childIsGround.Add(isGround);
            }

            if (children.Count == 0)
                return default;

            var instances = new NativeArray<Unity.Physics.CompoundCollider.ColliderBlobInstance>(children.Count, Allocator.Temp);
            try
            {
                for (var i = 0; i < children.Count; i++)
                {
                    instances[i] = children[i];
                    debugShapesOut?.Add(new TerrainDebugShape
                    {
                        CompoundFromChild = children[i].CompoundFromChild,
                        LocalAabb = children[i].Collider.Value.CalculateAabb(RigidTransform.identity),
                        IsGround = childIsGround[i],
                    });
                }

                return Unity.Physics.CompoundCollider.Create(instances);
            }
            finally
            {
                instances.Dispose();

                // CompoundCollider.Create 會複製子 blob,原始子 blob 需自行釋放
                foreach (var child in children)
                    child.Collider.Dispose();
            }
        }

        /// <summary>把 mesh 頂點經 rootFromChild(可含非均勻縮放)轉換後烘成 MeshCollider blob。</summary>
        static Unity.Entities.BlobAssetReference<Unity.Physics.Collider> _BakeMesh(
            Mesh mesh, Matrix4x4 rootFromChild, Unity.Physics.CollisionFilter filter)
        {
            var sourceVertices = mesh.vertices;
            var sourceIndices = mesh.triangles;
            var vertices = new NativeArray<float3>(sourceVertices.Length, Allocator.Temp);
            var triangles = new NativeArray<int3>(sourceIndices.Length / 3, Allocator.Temp);
            try
            {
                for (var i = 0; i < sourceVertices.Length; i++)
                    vertices[i] = rootFromChild.MultiplyPoint(sourceVertices[i]);
                for (var i = 0; i < triangles.Length; i++)
                    triangles[i] = new int3(sourceIndices[i * 3], sourceIndices[i * 3 + 1], sourceIndices[i * 3 + 2]);

                return Unity.Physics.MeshCollider.Create(vertices, triangles, filter, Unity.Physics.Material.Default);
            }
            finally
            {
                vertices.Dispose();
                triangles.Dispose();
            }
        }
    }
}
