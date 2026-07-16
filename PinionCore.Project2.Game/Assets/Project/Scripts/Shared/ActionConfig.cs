using UnityEngine;

namespace PinionCore.Project2.Shared
{
    // 一個「自帶位移的動作」的伺服器權威資料:root motion 由 editor 烘焙成
    // 分段等速直線(MotionSegment),伺服器依絕對 tick 排程逐段發出 MoveInfo,
    // 既有的去穿透 / TOI 撞牆對每段直線天然成立。
    [CreateAssetMenu(fileName = "ActionConfig", menuName = "PinionCore/ActionConfig", order = 5)]
    public class ActionConfig : ScriptableObject
    {
        [System.Serializable]
        public struct MotionSegment 
        {
            public Vector2 LocalOffset;  // 動作起始朝向的局部空間位移(x=右, y=前);零向量 = 原地
            public float Duration;       // 秒;段速度 = LocalOffset.magnitude / Duration
        }

        public ActionType Action = ActionType.BattleAttack;

        public ActionCategory Category = ActionCategory.Cast;

        // 動作總時長(秒)= 烘焙來源 clip 的長度;段總時長由烘焙器補零位移尾段對齊此值
        public float Duration;

        public MotionSegment[] Segments = System.Array.Empty<MotionSegment>();

#if UNITY_EDITOR
        [Header("烘焙設定(僅 Editor,player build 不含)")]
        public AnimationClip Clip;               // 烘焙來源;root motion 取樣自此 clip
        public GameObject BakeRig;               // 選填:模型 prefab 的 Animator 無 Avatar 時,指定有 Avatar 的 rig 烘焙
        public float SimplifyTolerance = 0.02f;  // 分段化最大偏差(公尺):折點到弦線垂距超過即切段
        public int MaxSegments = 8;              // 超過即警告(通常表示 clip 位移過於曲折或容差過小)
#endif
    }
}
