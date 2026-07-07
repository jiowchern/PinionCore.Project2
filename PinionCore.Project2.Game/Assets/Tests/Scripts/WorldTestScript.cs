using System.Collections;
using NUnit.Framework;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.TestTools;
using UniRx;                       // Subscribe 等 UniRx 擴充
using PinionCore.NetSync.UniRx;    // RemoteValue():把 Value<T> 轉成 IObservable
using PinionCore.Project2.Protocols;


namespace PinionCore.Project2.Tests
{

    public class WorldTestScript
    {
        // 這個 World 內部自建一個獨立的 DOTS 世界(world.Dots),測試之間互不污染。
        PinionCore.Project2.Worlds.World _world;

        [SetUp]
        public void SetUp()
        {
            var worldInfo = ScriptableObject.CreateInstance<PinionCore.Project2.Worlds.WorldConfig>();
            worldInfo.Name = "TestWorld";

            // TerrainPrefab 已改為 Addressable 參考;以 Terrain.prefab 的 GUID 建立 AssetReference。
            // (editor 測試以 AssetDatabase provider 解析,World 內部 WaitForCompletion 可同步取得。)
            worldInfo.TerrainPrefab = new UnityEngine.AddressableAssets.AssetReferenceGameObject("84e3641b69ee6b2419379df04933bb0d");
            _world = new PinionCore.Project2.Worlds.World(System.Guid.NewGuid(), worldInfo, new Worlds.ActorConfig[0]);
        }

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();   // 釋放內部自建的 DOTS 世界
            _world = null;
        }

        [UnityTest]
        public IEnumerator IWorldTestScriptWithEnumeratorPasses()
        {
            IWorld world = _world;

            // Act:呼叫 LoadTerrain,並用 UniRx 的 RemoteValue() 訂閱其非同步結果。
            // RemoteValue 會在結果解析後發一次值(這裡是同步完成,通常立即)。
            yield return null;


            // L2 存在:DOTS 世界內確實出現「剛好一顆」帶 TerrainTag 的地形實體。
            var em = _world.Dots.EntityManager;
            using (var query = em.CreateEntityQuery(typeof(PinionCore.Project2.Worlds.TerrainTag)))
            {
                Assert.AreEqual(1, query.CalculateEntityCount(), "應剛好有一顆 Terrain 實體");

                // L3 內容:實體帶有預期的元件(位置 + 碰撞)。
                var terrain = query.GetSingletonEntity();
                Assert.IsTrue(em.HasComponent<LocalTransform>(terrain),
                    "Terrain 缺少 LocalTransform");
                Assert.IsTrue(em.HasComponent<Unity.Physics.PhysicsCollider>(terrain),
                    "Terrain 缺少碰撞資料 PhysicsCollider");
            }
        }
    }

}
