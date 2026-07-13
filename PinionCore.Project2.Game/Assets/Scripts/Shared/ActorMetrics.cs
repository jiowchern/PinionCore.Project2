using UnityEngine;

namespace PinionCore.Project2.Shared
{
    /// <summary>
    /// 掛在角色 ModelPrefab 根上:伺服器權威度量(碰撞半徑、視野半徑)的資料來源。
    /// Editor 下 ActorConfig.Radius / SightRadius 優先讀此元件;player build 讀 build 前烘焙進 ActorConfig 的值。
    /// </summary>
    public class ActorMetrics : MonoBehaviour
    {
        /// <summary>碰撞半徑(XZ 平面上的圓):伺服器以此半徑的球對障礙做掃掠。</summary>
        public float CollisionRadius = 0.3f;

        /// <summary>視野半徑(XZ 平面上的圓):伺服器權威視野查詢用。</summary>
        public float SightRadius = 5.0f;

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            var center = transform.position;
            UnityEditor.Handles.color = Color.red;
            UnityEditor.Handles.DrawWireDisc(center, Vector3.up, CollisionRadius);
            UnityEditor.Handles.color = new Color(0f, 1f, 1f, 0.8f);
            UnityEditor.Handles.DrawWireDisc(center, Vector3.up, SightRadius);
            UnityEditor.Handles.color = new Color(0f, 1f, 1f, 0.25f);
            UnityEditor.Handles.DrawWireDisc(center, Vector3.up, SightRadius * SightRules.ExitRadiusFactor);
        }
#endif
    }
}
