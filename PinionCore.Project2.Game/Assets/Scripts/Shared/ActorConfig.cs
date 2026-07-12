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

        // 碰撞半徑(XZ 平面上的圓):伺服器權威碰撞查詢用,角色以此半徑的球對障礙做掃掠
        public float Radius = 0.3f;

        // 視野半徑(XZ 平面上的圓):伺服器權威視野查詢用,角色以此半徑的球對其他角色做掃掠
        public float SightRadius = 5.0f;
    }
}
