using PinionCore.Project2.Shared;
using PinionCore.Remote;
using PinionCore.Utility;
using System;
using System.Linq;
using UnityEngine;

namespace PinionCore.Project2.Worlds
{
    // 管理所有 world 的类
    public class Universe : MonoBehaviour , PinionCore.Project2.Shared.IUniverse
    {
        public WorldConfigSet WorldConfigs;
        public ActorConfigSet ActorConfigs;

        readonly PinionCore.Remote.Depot<World> _WorldsDepot;
        readonly PinionCore.Remote.Notifier<IWorld> _WorldsNotifier;

        PinionCore.Remote.Notifier<IWorld> IUniverse.Worlds => _WorldsNotifier;

        // 供 editor 除錯繪製(WorldDebugDrawer)走訪現存 world
        internal System.Collections.Generic.IEnumerable<World> WorldItems => _WorldsDepot.ReadOnlyItems;

        public Universe()
        {
            _WorldsDepot = new PinionCore.Remote.Depot<World>();
            _WorldsNotifier = _WorldsDepot.ToNotifier<IWorld>();
        }

        Value<Guid> IUniverse.QueryWorld(string name)
        {
            // 同名世界共用同一個實例,所有玩家才會進到同一個世界看見彼此
            var existing = _WorldsDepot.ReadOnlyItems.FirstOrDefault(w => w.Name == name);
            if (existing != null)
                return new Value<Guid>(existing.Id);

            var config = WorldConfigs.Find(name);
            if (config == null)
            {
                Debug.LogError($"WorldConfig not found for name: {name}");
                return new Value<Guid>(Guid.Empty);
            }

            var worldId = Guid.NewGuid();
            var world = new World(worldId, config, ActorConfigs.Configs);
            _WorldsDepot.Items.Add(world);

            return new Value<Guid>(worldId);
        }

        Value<bool> IUniverse.DestroyWorld(Guid worldId)
        {
            var world = _WorldsDepot.ReadOnlyItems.FirstOrDefault(w => w.Id == worldId);
            if (world == null)
                return false;

            world.Dispose();
            _WorldsDepot.Items.Remove(world);
            return true;


        }

        public void Update()
        {
            foreach (var world in _WorldsDepot.ReadOnlyItems)
            {
                world.Update();
            }
        }

        private void OnDestroy()
        {
            // World 持有 Persistent 原生資源(DOTS world、collider blob),
            // 場景卸載/停止 Play Mode 時必須逐一 Dispose,否則觸發 native leak 警告。
            foreach (var world in _WorldsDepot.ReadOnlyItems.ToArray())
            {
                world.Dispose();
            }
            _WorldsDepot.Items.Clear();
        }
    }
}
