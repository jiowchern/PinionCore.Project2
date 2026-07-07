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

            // Enter 驗證用的 actor 設定:ModelName 必須對得上 ActorConfig.Name 才能進入世界。
            var actorConfig = ScriptableObject.CreateInstance<PinionCore.Project2.Worlds.ActorConfig>();
            actorConfig.Name = "TestActor";
            _world = new PinionCore.Project2.Worlds.World(System.Guid.NewGuid(), worldInfo, new[] { actorConfig });
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

        [UnityTest]
        public IEnumerator EnterAndLeaveTest()
        {
            IWorld world = _world;
            yield return null;

            IPlayer supplied = null;
            IPlayer unsupplied = null;
            world.Players.Base.Supply += p => supplied = p;
            world.Players.Base.Unsupply += p => unsupplied = p;

            // Enter:ModelName 不在 actorConfigs 內 → 拒絕,回 Guid.Empty,無任何 Supply。
            var invalidEnterId = System.Guid.NewGuid();
            world.Enter(new ActorInfo { ModelName = "Unknown", DisplayName = "Nobody" })
                .RemoteValue().Subscribe(id => invalidEnterId = id);
            Assert.AreEqual(System.Guid.Empty, invalidEnterId, "不合法的 ModelName 應回傳 Guid.Empty");
            Assert.IsNull(supplied, "不合法的 Enter 不應加入玩家");

            // Enter:合法 ModelName → 取得 actorId,Players 發出 Supply,DOTS 世界多一顆玩家實體。
            var actorId = System.Guid.Empty;
            world.Enter(new ActorInfo { ModelName = "TestActor", DisplayName = "Tester" })
                .RemoteValue().Subscribe(id => actorId = id);
            Assert.AreNotEqual(System.Guid.Empty, actorId, "合法的 Enter 應回傳 actorId");
            Assert.IsNotNull(supplied, "Enter 後 Players 應發出 Supply");
            System.Guid suppliedActorId = supplied.ActorId;
            Assert.AreEqual(actorId, suppliedActorId, "Supply 的玩家 ActorId 應與 Enter 回傳一致");
            string suppliedModelName = supplied.ModelName;
            Assert.AreEqual("TestActor", suppliedModelName);
            string suppliedDisplayName = supplied.DisplayName;
            Assert.AreEqual("Tester", suppliedDisplayName);

            var em = _world.Dots.EntityManager;
            using (var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.Exclude<PinionCore.Project2.Worlds.TerrainTag>()))
            {
                Assert.AreEqual(1, query.CalculateEntityCount(), "Enter 後應有一顆玩家實體");
            }

            // Leave:無效 id → false,不影響現有玩家。
            var invalidLeaveResult = true;
            world.Leave(System.Guid.NewGuid()).RemoteValue().Subscribe(r => invalidLeaveResult = r);
            Assert.IsFalse(invalidLeaveResult, "無效的 actorId 應回傳 false");
            Assert.IsNull(unsupplied, "無效的 Leave 不應移除玩家");

            // Leave:有效 id → true,Unsupply 觸發,玩家實體銷毀。
            var leaveResult = false;
            world.Leave(actorId).RemoteValue().Subscribe(r => leaveResult = r);
            Assert.IsTrue(leaveResult, "有效的 actorId 應回傳 true");
            Assert.IsNotNull(unsupplied, "Leave 後 Players 應發出 Unsupply");
            using (var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.Exclude<PinionCore.Project2.Worlds.TerrainTag>()))
            {
                Assert.AreEqual(0, query.CalculateEntityCount(), "Leave 後玩家實體應被銷毀");
            }
        }
    }

}
