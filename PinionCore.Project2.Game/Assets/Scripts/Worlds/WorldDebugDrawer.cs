using UnityEngine;

namespace PinionCore.Project2.Worlds
{
    /// <summary>
    /// Editor 除錯繪製:把伺服器權威狀態畫進 Scene view,供與 client 殼比對同步。
    /// - 玩家:權威位置(球)+ 朝向(射線)+ 當前 MoveInfo 前進預告線
    /// - 地形:碰撞 AABB(線框盒)
    /// 以元件 enabled 與各 Draw 開關控制;只在 Play Mode 且編輯器內生效,不影響建置。
    /// </summary>
    public class WorldDebugDrawer : MonoBehaviour
    {
        public Universe Universe;
        public bool DrawActors = true;
        public bool DrawTerrainBounds = true;

#if UNITY_EDITOR
        void OnDrawGizmos()
        {
            // OnDrawGizmos 不受 enabled 影響,自行尊重開關
            if (!isActiveAndEnabled || Universe == null || !Application.isPlaying)
                return;

            foreach (var world in Universe.Worlds)
            {
                if (DrawActors)
                    _DrawActors(world);
                if (DrawTerrainBounds)
                    _DrawTerrainBounds(world);
            }
        }

        static void _DrawActors(World world)
        {
            var em = world.Dots.EntityManager;
            foreach (var player in world.PlayerItems)
            {
                // LocalTransform 是 World.Update 每幀由 MoveInfo 取樣投影的權威狀態
                var t = em.GetComponentData<Unity.Transforms.LocalTransform>(player.Entity);
                var pos = (Vector3)t.Position;
                var fwd = (Vector3)Unity.Mathematics.math.forward(t.Rotation);

                Gizmos.color = Color.red;
                Gizmos.DrawWireSphere(pos, player.Radius);
                Gizmos.DrawRay(pos, fwd * 1.2f);

                // 移動中:畫一段前進預告線
                var info = player.CurrentMoveInfo;
                if (info.Speed > 0f)
                {
                    Gizmos.color = Color.yellow;
                    Gizmos.DrawRay(pos, fwd * 3f);
                }

                // 有預計撞牆點:畫洋紅十字(高度取角色半徑,與掃掠球心一致)
                if (player.HasPendingHit)
                {
                    var hit = new Vector3(player.PendingHitPosition.x, player.Radius, player.PendingHitPosition.y);
                    Gizmos.color = Color.magenta;
                    Gizmos.DrawLine(hit + Vector3.left * 0.2f, hit + Vector3.right * 0.2f);
                    Gizmos.DrawLine(hit + Vector3.back * 0.2f, hit + Vector3.forward * 0.2f);
                }
            }
        }

        static void _DrawTerrainBounds(World world)
        {
            var em = world.Dots.EntityManager;
            using (var query = em.CreateEntityQuery(
                Unity.Entities.ComponentType.ReadOnly<TerrainTag>(),
                Unity.Entities.ComponentType.ReadOnly<Unity.Physics.PhysicsCollider>(),
                Unity.Entities.ComponentType.ReadOnly<Unity.Transforms.LocalTransform>()))
            using (var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp))
            {
                foreach (var entity in entities)
                {
                    var t = em.GetComponentData<Unity.Transforms.LocalTransform>(entity);
                    var collider = em.GetComponentData<Unity.Physics.PhysicsCollider>(entity);
                    if (!collider.Value.IsCreated)
                        continue;

                    var aabb = collider.Value.Value.CalculateAabb(
                        new Unity.Mathematics.RigidTransform(t.Rotation, t.Position));
                    var center = (Vector3)((aabb.Min + aabb.Max) * 0.5f);
                    var size = (Vector3)(aabb.Max - aabb.Min);
                    Gizmos.color = Color.green;
                    Gizmos.DrawWireCube(center, size);

                    // 個別子形狀:地面灰、障礙青(compound 內部不易走訪,用烘焙時記下的清單)
                    var worldFromTerrain = new Unity.Mathematics.RigidTransform(t.Rotation, t.Position);
                    foreach (var shape in world.TerrainDebugShapes)
                    {
                        var worldFromChild = Unity.Mathematics.math.mul(worldFromTerrain, shape.CompoundFromChild);
                        var localCenter = (shape.LocalAabb.Min + shape.LocalAabb.Max) * 0.5f;
                        var childCenter = (Vector3)Unity.Mathematics.math.transform(worldFromChild, localCenter);
                        var childSize = (Vector3)(shape.LocalAabb.Max - shape.LocalAabb.Min);

                        Gizmos.color = shape.IsGround ? Color.gray : Color.cyan;
                        var prev = Gizmos.matrix;
                        Gizmos.matrix = Matrix4x4.TRS(childCenter, worldFromChild.rot, Vector3.one);
                        Gizmos.DrawWireCube(Vector3.zero, childSize);
                        Gizmos.matrix = prev;
                    }
                }
            }
        }
#endif
    }
}
