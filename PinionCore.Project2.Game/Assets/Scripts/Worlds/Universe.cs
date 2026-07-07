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
        public WorldConfig[] WorldConfigs;
        public ActorConfig[] ActorConfigs;

        readonly PinionCore.Remote.Depot<World> _WorldsDepot;
        readonly PinionCore.Remote.Notifier<IWorld> _WorldsNotifier;

        PinionCore.Remote.Notifier<IWorld> IUniverse.WorldNotifier => _WorldsNotifier;

        public Universe()
        {
            _WorldsDepot = new PinionCore.Remote.Depot<World>();
            _WorldsNotifier = _WorldsDepot.ToNotifier<IWorld>();
        }

        Value<Guid> IUniverse.QueryWorld(string name)
        {
            var config = WorldConfigs.FirstOrDefault(c => c.Name == name);  
            if (config == null)
            {
                Debug.LogError($"WorldConfig not found for name: {name}");
                return new Value<Guid>(Guid.Empty);
            }

            var worldId = Guid.NewGuid();
            var world = new World(worldId, config, ActorConfigs);
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
    }
}
