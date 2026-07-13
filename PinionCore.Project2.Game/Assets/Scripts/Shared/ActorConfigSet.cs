using System.Linq;
using UnityEngine;

namespace PinionCore.Project2.Shared
{
    // 角色配置集合:集中持有所有 ActorConfig,場景元件只引用這一份資產;
    // 新增/調整角色配置只需編輯此資產,不必逐場景改 MonoBehaviour 上的陣列。
    [CreateAssetMenu(fileName = "ActorConfigSet", menuName = "PinionCore/ActorConfigSet", order = 4)]
    public class ActorConfigSet : ScriptableObject
    {
        public ActorConfig[] Configs;

        public ActorConfig Find(string name)
        {
            return Configs.FirstOrDefault(c => c.Name == name);
        }
    }
}
