using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Serialization;

namespace PinionCore.Project2.Shared
{
    [CreateAssetMenu(fileName = "ActorConfig", menuName = "PinionCore/ActorConfig", order = 2)]
    public class ActorConfig : ScriptableObject
    {
        public string Name;
        public AssetReferenceGameObject ModelPrefab;
        public float MoveSpeed = 1.0f;

        // Move 指令的最小接受間隔(秒):間隔內的 Move 被拒絕(回傳 false),Stop 不受限
        public float MoveAcceptInterval = 0.1f;

        // 序列化欄位是 fallback / build 烘焙值:Editor 下若 ModelPrefab 有 ActorMetrics,
        // getter 以 metrics 為準(setter 寫入的值會被蓋過);player build 一律讀此欄位,
        // 由 build 前的 ActorConfigBaker 保證與 metrics 同步。測試不設 ModelPrefab,setter 有效。
        [SerializeField, FormerlySerializedAs("Radius")]
        float _Radius = 0.3f;

        [SerializeField, FormerlySerializedAs("SightRadius")]
        float _SightRadius = 5.0f;

        // 碰撞半徑(XZ 平面上的圓):伺服器權威碰撞查詢用,角色以此半徑的球對障礙做掃掠
        public float Radius
        {
            get
            {
#if UNITY_EDITOR
                var metrics = _EditorMetrics();
                if (metrics != null)
                    return metrics.CollisionRadius;
#endif
                return _Radius;
            }
            set { _Radius = value; }
        }

        // 視野半徑(XZ 平面上的圓):伺服器權威視野查詢用,角色以此半徑的球對其他角色做掃掠
        public float SightRadius
        {
            get
            {
#if UNITY_EDITOR
                var metrics = _EditorMetrics();
                if (metrics != null)
                    return metrics.SightRadius;
#endif
                return _SightRadius;
            }
            set { _SightRadius = value; }
        }

#if UNITY_EDITOR
        ActorMetrics _EditorMetrics()
        {
            if (ModelPrefab == null)
                return null;
            // 空 guid 的 AssetReference 其 editorAsset 為 null,不會丟例外
            var go = ModelPrefab.editorAsset;
            return go != null ? go.GetComponent<ActorMetrics>() : null;
        }

        /// <summary>供 build 前處理器呼叫:把 ActorMetrics 值寫進序列化欄位。回傳是否有變更。</summary>
        public bool BakeFromMetrics()
        {
            var metrics = _EditorMetrics();
            if (metrics == null)
                return false;
            if (_Radius == metrics.CollisionRadius && _SightRadius == metrics.SightRadius)
                return false;
            _Radius = metrics.CollisionRadius;
            _SightRadius = metrics.SightRadius;
            return true;
        }
#endif
    }
}
