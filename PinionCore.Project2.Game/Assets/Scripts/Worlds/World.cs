using PinionCore.Project2.Protocols.Worlds;
using PinionCore.Remote;
using System;

namespace PinionCore.Project2.Worlds
{

    public class World : IWorld
    {

        public World() { 
            // todo:初始化 unity dots 
        }        
        Value<bool> IWorld.LoadTerrain()
        {
            // todo : 載入地形 Assets/Prefabs/Terrain.prefab
            throw new NotImplementedException();
        }
    }
}
