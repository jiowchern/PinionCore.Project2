using UnityEngine;
using UnityEngine.AddressableAssets;

namespace PinionCore.Project2.Worlds
{
    [CreateAssetMenu(fileName = "ActorConfig", menuName = "PinionCore/ActorConfig", order = 2)]
    public class ActorConfig : ScriptableObject
    {
        public string Name;
        public AssetReferenceGameObject ModelPrefab;
    }
}
