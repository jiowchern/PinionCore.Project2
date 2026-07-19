using UnityEngine;

namespace PinionCore.Project2.Shared
{
    [CreateAssetMenu(fileName = "ActionIconConfigSet", menuName = "PinionCore/ActionIconConfigSet")]
    public class ActionIconConfigSet : ScriptableObject
    {
        public ActionIconConfig[] Configs;

        public ActionIconConfigSet()
        {
        }

        public ActionIconConfig Find(ActionType action)
        {
            foreach (var config in Configs)
            {
                if (config.Action == action)
                {
                    return config;
                }
            }
            return null;
        }
    }
}
