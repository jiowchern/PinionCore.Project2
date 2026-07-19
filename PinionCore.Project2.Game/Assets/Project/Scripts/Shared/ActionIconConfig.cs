using UnityEngine;

namespace PinionCore.Project2.Shared
{
    [CreateAssetMenu(fileName = "ActionIconConfig", menuName = "PinionCore/ActionIconConfig")]
    public class ActionIconConfig : ScriptableObject
    {
        public ActionType Action = ActionType.BattleAttack;
        public ActionIcon Icon = null;
    }
}
