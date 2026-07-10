using UnityEngine;

namespace PinionCore.Project2.Worlds
{
    /// <summary>
    /// Editor 除錯繪製:把伺服器權威狀態畫進 Scene view,供與 client 殼比對同步。
    /// - 玩家:權威位置(球)+ 朝向(射線)+ 當前 MoveInfo 弧線軌跡(圓)
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
                Gizmos.DrawWireSphere(pos, 0.3f);
                Gizmos.DrawRay(pos, fwd * 1.2f);

                // 當前弧線:圓心 = 位置 + 帶號半徑 × PerpRight(朝向),對弧上任一點都不變
                var info = player.CurrentMoveInfo;
                if (info.Speed > 0f && Mathf.Abs(info.AngularSpeed) > 1e-4f)
                {
                    var radius = info.Speed / info.AngularSpeed;
                    var facing2 = new Vector2(fwd.x, fwd.z);
                    var center2 = new Vector2(pos.x, pos.z) + radius * new Vector2(facing2.y, -facing2.x);
                    UnityEditor.Handles.color = Color.yellow;
                    UnityEditor.Handles.DrawWireDisc(
                        new Vector3(center2.x, pos.y, center2.y), Vector3.up, Mathf.Abs(radius));
                }
                else if (info.Speed > 0f)
                {
                    // 直線:畫一段前進預告線
                    Gizmos.color = Color.yellow;
                    Gizmos.DrawRay(pos, fwd * 3f);
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
                }
            }
        }
#endif
    }
}
