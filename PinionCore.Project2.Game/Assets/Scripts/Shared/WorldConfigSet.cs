using System.Linq;
using UnityEngine;

namespace PinionCore.Project2.Shared
{
    // 世界配置集合:集中持有所有 WorldConfig,場景元件只引用這一份資產;
    // 新增/調整世界配置只需編輯此資產,不必逐場景改 MonoBehaviour 上的陣列。
    [CreateAssetMenu(fileName = "WorldConfigSet", menuName = "PinionCore/WorldConfigSet", order = 3)]
    public class WorldConfigSet : ScriptableObject
    {
        public WorldConfig[] Configs;

        public WorldConfig Find(string name)
        {
            return Configs.FirstOrDefault(c => c.Name == name);
        }
    }
}
