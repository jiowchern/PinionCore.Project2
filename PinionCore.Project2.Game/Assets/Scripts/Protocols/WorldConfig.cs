using UnityEngine;
using UnityEngine.AddressableAssets;

namespace PinionCore.Project2.Protocols
{

    [CreateAssetMenu(fileName = "WorldConfig", menuName = "PinionCore/WorldConfig", order = 1)]
    public class WorldConfig : ScriptableObject
    {
        public string Name;

        /// <summary>
        /// 地形資源的 Addressable 參考。
        /// 改用 AssetReference 後,資源不再硬綁進場景/SO 首包,而是按需非同步載入,
        /// 這是 WebGL 控制記憶體與下載體積的關鍵。
        /// </summary>
        public AssetReferenceGameObject TerrainPrefab;

        public UnityEngine.Vector3 Entrance;
        public float TimeUpdateInterval;
    }
}
