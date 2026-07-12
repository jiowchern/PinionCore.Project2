using PinionCore.Project2.Shared;
using PinionCore.Remote;
using System;
using System.Diagnostics;
using System.Linq;
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
        private readonly ActorConfig[] actorConfigs;

        // 記住建立出來的 collider blob;blob 不隨 world 自動釋放,Dispose 時要手動釋放。
        Unity.Entities.BlobAssetReference<Unity.Physics.Collider> _terrainCollider;

        // 地形碰撞查詢服務(Player 移動用);與 debug 繪製用的子形狀清單
        TerrainQuery _terrainQuery;
        readonly System.Collections.Generic.List<TerrainColliderBaker.TerrainDebugShape> _terrainDebugShapes
            = new System.Collections.Generic.List<TerrainColliderBaker.TerrainDebugShape>();

        // 供 Player 做移動碰撞查詢
        internal TerrainQuery Terrain => _terrainQuery;

        // 供 editor 除錯繪製(WorldDebugDrawer)畫個別地形子形狀
        internal System.Collections.Generic.IReadOnlyList<TerrainColliderBaker.TerrainDebugShape> TerrainDebugShapes => _terrainDebugShapes;

        /// <summary>
        /// 對外開放內部的 DOTS 世界,讓遊戲系統或測試可以查詢 EntityManager。
        /// </summary>
        public Unity.Entities.World Dots => _dots;

        Property<string> IView.Name => new Property<string>(_info.Name);

        // 供 Universe.QueryWorld 以名稱找回既有世界
        internal string Name => _info.Name;

        Property<Guid> IWorld.Id => new Property<Guid>(Id);


        Depot<Player> _Players ;
        Notifier<ICharactor> _PlayersNotifier;
        Notifier<ICharactor> IWorld.Players => _PlayersNotifier;

        // 供 editor 除錯繪製(WorldDebugDrawer)走訪權威玩家狀態
        internal System.Collections.Generic.IEnumerable<Player> PlayerItems => _Players.Items;

        // TimeSpan ticks(100ns),與 TimeTicksEvent / 前端 WorldTimeHandler.CurrentTime 同單位;
        // 不可用 Stopwatch.ElapsedTicks(原始計數,頻率依平台)。
        public long ElapsedTicks { get => _elapsedWatch.Elapsed.Ticks; }

        public readonly Guid Id;

        // stopwatch 用來計算地圖產生開始的時間戳記。
        readonly System.Diagnostics.Stopwatch _elapsedWatch ;
        readonly System.Diagnostics.Stopwatch _UpdateWatch ;

        // 視野評估:Burst job 判定距離 + 遮蔽,結果增刪各 Player.VisibleActors;節流 stopwatch 控制頻率
        readonly Sight _Sight;
        readonly System.Diagnostics.Stopwatch _SightWatch;

        public World(Guid id,WorldConfig worldInfo, ActorConfig[] actorConfigs)
        {
            _elapsedWatch = Stopwatch.StartNew();
            _UpdateWatch = Stopwatch.StartNew();
            _Sight = new Sight();
            _SightWatch = Stopwatch.StartNew();
            _Players = new Depot<Player>();
            _PlayersNotifier = _Players.ToNotifier<ICharactor>();

            Id = id;
            _info = worldInfo;
            this.actorConfigs = actorConfigs;
            _dots = new Unity.Entities.World(_info.Name);



            _LoadTerrain();
        }

        event Action<long> _TimeTicksEvent;
        event Action<long> IView.TimeTicksEvent
        {
            add
            {
                
                _TimeTicksEvent += value;
                value(_elapsedWatch.Elapsed.Ticks);
            }

            remove
            {
                _TimeTicksEvent -= value;
            }
        }
       

        public void Dispose()
        {
            // 先清掉殘留玩家讓 Unsupply 通知送出;entity 隨 _dots.Dispose() 一併銷毀。
            foreach (var player in _Players.Items)
                player.VisibleActors.Items.Clear();
            _Players.Items.Clear();
            _terrainQuery?.Dispose();
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
                    UnityEngine.Debug.LogError("[World] WorldInfo.TerrainPrefab 未設定有效的 Addressable 參考");
                    return false;
                }

                loadHandle = _info.TerrainPrefab.LoadAssetAsync<GameObject>();
                var prefab = loadHandle.WaitForCompletion();
                if (prefab == null)
                {
                    UnityEngine.Debug.LogError("[World] 地形 Addressable 載入失敗");
                    return false;
                }

                // 後端只要碰撞:把根+子物件所有非 trigger collider 烘成單一 compound
                //(根節點 = Ground 層、子物件 = Obstacle 層,見 TerrainColliderBaker)。
                var compound = TerrainColliderBaker.Bake(prefab, _terrainDebugShapes);
                if (!compound.IsCreated)
                {
                    UnityEngine.Debug.LogError("[World] 地形 prefab 找不到可用的 collider(MeshCollider / BoxCollider)");
                    return false;
                }

                CreateTerrainCollider(compound, prefab.transform.position);
                return true;
            }
            catch (Exception e)
            {
                // 任何例外都視為載入失敗,回報 false,而不是讓例外往外拋。
                UnityEngine.Debug.LogException(e);
                return false;
            }
            finally
            {
                // mesh 幾何已烘進 collider blob,原 prefab 資源可釋放。
                if (loadHandle.IsValid())
                    UnityEngine.AddressableAssets.Addressables.Release(loadHandle);
            }
        }

        /// <summary>
        /// 用烘好的 compound blob 建一顆 Unity.Physics 的碰撞實體:PhysicsCollider + 位置 + TerrainTag。
        /// 完全不加渲染元件;同時建立 TerrainQuery 供移動碰撞查詢。
        /// </summary>
        void CreateTerrainCollider(Unity.Entities.BlobAssetReference<Unity.Physics.Collider> compound, Vector3 position)
        {
            _terrainCollider = compound;
            _terrainQuery = new TerrainQuery(
                _terrainCollider,
                new Unity.Mathematics.RigidTransform(quaternion.identity, new float3(position.x, position.y, position.z)));

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

        Value<Guid> IWorld.Enter(ActorInfo actor)
        {
            // ModelName 必須存在於 actorConfigs 才允許進入;Value<T> 沒有錯誤通道,失敗以 Guid.Empty 表示。
            var config = actorConfigs.FirstOrDefault(c => c.Name == actor.ModelName);
            if (config == null)
                return Guid.Empty;

            var em = _dots.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, LocalTransform.FromPosition(_info.Entrance));

            var actorId = Guid.NewGuid();
            var player = new Player(actorId, actor, entity, em, config.MoveSpeed, config.MoveAcceptInterval, config.Radius, config.SightRadius, _info.Entrance, this);

            // 自己永遠可見;其餘互見交給 Sight 依「距離 + 遮蔽」判定。
            // 先投影全體 transform(ctor 去穿透可能把出生點推離 Entrance)再評估;
            // 先填好新玩家的名單再加入 _Players,綁定時的 replay 會送出完整名單。
            player.VisibleActors.Items.Add(player);
            var evaluated = new System.Collections.Generic.List<Player>(_Players.Items) { player };
            foreach (var p in evaluated)
                p.Update();
            _Sight.Tick(evaluated, em, _terrainQuery);

            _Players.Items.Add(player);
            return actorId;
        }

        Value<bool> IWorld.Leave(Guid actorId)
        {
            var player = _Players.Items.FirstOrDefault(p => p.ActorId == actorId);
            PinionCore.Utility.Log.Instance.WriteInfo($"World.Leave actor:{actorId} found:{player != null}");
            if (player == null)
                return false;

            // 從所有玩家(含自己)的視野移除,讓 Unsupply 先送達;離開不走 debounce,立即移除
            foreach (var other in _Players.Items)
                other.VisibleActors.Items.Remove(player);
            player.VisibleActors.Items.Clear();
            _Sight.Forget(player);

            _Players.Items.Remove(player);
            _dots.EntityManager.DestroyEntity(player.Entity);
            return true;
        }

        internal void Update()
        {
            // 每 _info.TimeUpdateInterval 秒送一次時間戳給前端,讓前端知道後端的時間進度。
            if (_TimeTicksEvent != null && _UpdateWatch.ElapsedMilliseconds > _info.TimeUpdateInterval * 1000)
            {
                _TimeTicksEvent(_elapsedWatch.Elapsed.Ticks);
                _UpdateWatch.Restart();
            }

            // 把所有玩家的 MoveInfo 取樣結果投影到 entity。
            foreach (var player in _Players.Items)
                player.Update();

            // 視野判定節流:transform 投影完才評估,結果增刪 VisibleActors → Supply/Unsupply
            if (_SightWatch.Elapsed.TotalSeconds >= Sight.UpdateIntervalSeconds)
            {
                _Sight.Tick(new System.Collections.Generic.List<Player>(_Players.Items), _dots.EntityManager, _terrainQuery);
                _SightWatch.Restart();
            }
        }

        /// <summary>
        /// 供測試決定性驅動視野判定:投影全體 transform 後立刻評估一次,不受節流間隔影響。
        /// </summary>
        internal void TickSight()
        {
            var players = new System.Collections.Generic.List<Player>(_Players.Items);
            foreach (var player in players)
                player.Update();
            _Sight.Tick(players, _dots.EntityManager, _terrainQuery);
        }
    }
}
