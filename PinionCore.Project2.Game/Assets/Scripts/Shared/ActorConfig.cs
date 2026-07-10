using UnityEngine;
using UnityEngine.AddressableAssets;

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
    }
}
