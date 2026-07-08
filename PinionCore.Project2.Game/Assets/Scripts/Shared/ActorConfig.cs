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
    }
}
