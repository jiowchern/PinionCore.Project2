using System;
using PinionCore.Project2.Protocols;
using PinionCore.Remote;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;      // MeshCollider / PhysicsCollider / CollisionFilter / Material
using Unity.Transforms;   // LocalTransform
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace PinionCore.Project2.Worlds
{
    

    /// <summary>
    /// Unity DOTS 的封裝類:只負責「後端碰撞/模擬」。
    /// 內部自建一個獨立的 DOTS 世界(不注入預設世界);需要什麼系統再逐一加,
    /// 目前的 LoadTerrain 直接對 EntityManager 建實體,尚不需要任何 system。
    /// 渲染不在這裡 —— 那是前端 WebGL 的事。
    /// </summary>
    public class World : IWorld, IDisposable
    {
        // 內部持有、自建的 DOTS 世界。
        readonly Unity.Entities.World _dots;
        readonly WorldConfig _info;

        // 記住建立出來的 collider blob;blob 不隨 world 自動釋放,Dispose 時要手動釋放。
        Unity.Entities.BlobAssetReference<Unity.Physics.Collider> _terrainCollider;

        /// <summary>
        /// 對外開放內部的 DOTS 世界,讓遊戲系統或測試可以查詢 EntityManager。
        /// </summary>
        public Unity.Entities.World Dots => _dots;

        Property<string> IView.Name => new Property<string>(_info.Name);

        Property<Guid> IWorld.Id => new Property<Guid>(Id);

        public readonly Guid Id;



        public World(Guid id,WorldConfig worldInfo)
        {
            Id = id;
            _info = worldInfo;
            _dots = new Unity.Entities.World(_info.Name);

            _LoadTerrain();
        }


        public void Dispose()
        {
            if (_terrainCollider.IsCreated)
                _terrainCollider.Dispose();
            if (_dots.IsCreated)
                _dots.Dispose();
        }

        Value<bool> _LoadTerrain()
        {
            // 回傳 Value<bool>:Value<T> 有 bool 的隱式轉換,所以直接 return true/false 即可。
            //
            // TerrainPrefab 現為 Addressable 參考。後端(伺服器)側只需讀取一次 mesh 來烘焙
            // 碰撞 blob,故用同步 WaitForCompletion 取得 prefab;此路徑跑在非 WebGL 的後端,
            // 同步載入可接受。烘焙完成後立即 Release,不常駐佔用資源。
            UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> loadHandle = default;
            try
            {
                if (_info.TerrainPrefab == null || !_info.TerrainPrefab.RuntimeKeyIsValid())
                {
                    Debug.LogError("[World] WorldInfo.TerrainPrefab 未設定有效的 Addressable 參考");
                    return false;
                }

                loadHandle = _info.TerrainPrefab.LoadAssetAsync<GameObject>();
                var prefab = loadHandle.WaitForCompletion();
                if (prefab == null)
                {
                    Debug.LogError("[World] 地形 Addressable 載入失敗");
                    return false;
                }

                // 後端只要碰撞:優先取 MeshCollider 的幾何,退而取 MeshFilter 的 mesh。
                var meshCollider = prefab.GetComponentInChildren<UnityEngine.MeshCollider>();
                var mesh = meshCollider != null ? meshCollider.sharedMesh : null;
                if (mesh == null)
                {
                    var filter = prefab.GetComponentInChildren<MeshFilter>();
                    mesh = filter != null ? filter.sharedMesh : null;
                }
                if (mesh == null)
                {
                    Debug.LogError("[World] 地形 prefab 找不到可用的碰撞 Mesh(MeshCollider / MeshFilter)");
                    return false;
                }

                CreateTerrainCollider(mesh, prefab.transform.position);
                return true;
            }
            catch (Exception e)
            {
                // 任何例外都視為載入失敗,回報 false,而不是讓例外往外拋。
                Debug.LogException(e);
                return false;
            }
            finally
            {
                // mesh 幾何已烘進 collider blob,原 prefab 資源可釋放。
                if (loadHandle.IsValid())
                    Addressables.Release(loadHandle);
            }
        }

        /// <summary>
        /// 用 mesh 建一顆 Unity.Physics 的碰撞實體:PhysicsCollider + 位置 + TerrainTag。
        /// 完全不加渲染元件。
        /// </summary>
        void CreateTerrainCollider(Mesh mesh, Vector3 position)
        {
            // 把 UnityEngine.Mesh 烘成 DOTS 物理的 collider blob(碰撞幾何)。
            _terrainCollider = Unity.Physics.MeshCollider.Create(
                mesh,
                Unity.Physics.CollisionFilter.Default,
                Unity.Physics.Material.Default);

            var em = _dots.EntityManager;
            var entity = em.CreateEntity();

            // 位置:給物理與後續 Transform 用。
            var pos = new float3(position.x, position.y, position.z);
            em.AddComponentData(entity, LocalTransform.FromPosition(pos));

            // 碰撞:PhysicsCollider 指向剛烘好的 blob。
            em.AddComponentData(entity, new Unity.Physics.PhysicsCollider { Value = _terrainCollider });

            // 標記為物理世界 0 的一員(之後要做碰撞查詢時,物理系統會據此把它納入)。
            em.AddSharedComponent(entity, new Unity.Physics.PhysicsWorldIndex());

            // 讓「這顆是地形」成為可查詢的事實(供遊戲系統與測試使用)。
            em.AddComponent<TerrainTag>(entity);
        }
    }
}
