using UnityEngine;

namespace PinionCore.Project2.Worlds
{
    [CreateAssetMenu(fileName = "WorldConfig", menuName = "PinionCore/WorldConfig", order = 1)]
    public class WorldConfig : ScriptableObject
    {
        public string Name;
        public GameObject TerrainPrefab;
    }
}
