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

        [Header("能力/權限(伺服器權威讀取)")]
        public bool Loop;            // 段播完 wrap 重排循環;false = 段播完 _EndAction 結束
        public bool Redirectable;    // 進行中可被 Move 換向;也是 Move 直接起走與 Stop 的選型條件
        public bool Interruptible;   // 進行中可被非 force 的新動作取代

        [Header("表現(client 讀取)")]
        public bool HoldRotation;    // 動作期間 client 凍結旋轉(段 Facing 是速度方向非視覺朝向)
        public StanceType Stance;    // 動畫組歸屬/輸入 gating(取代已拆除的 StanceOf 推導)

        // 動作總時長(秒)= 烘焙來源 clip 的長度;段總時長由烘焙器補零位移尾段對齊此值
        public float Duration;

        public MotionSegment[] Segments = System.Array.Empty<MotionSegment>();

#if UNITY_EDITOR
        [Header("烘焙設定(僅 Editor,player build 不含)")]
        public AnimationClip Clip;               // 烘焙來源;root motion 取樣自此 clip
        public GameObject BakeRig;               // 選填:模型 prefab 的 Animator 無 Avatar 時,指定有 Avatar 的 rig 烘焙
        public float SimplifyTolerance = 0.02f;  // 分段化最大偏差(公尺):折點到弦線垂距超過即切段
        public int MaxSegments = 8;              // 超過即警告(通常表示 clip 位移過於曲折或容差過小)

        void OnValidate()
        {
            if (Redirectable && !Loop)
                Debug.LogError($"ActionConfig {name}: Redirectable 依賴循環邊界相位保持,非 Loop 動作的重定向語意未定義", this);
            if (Redirectable && HoldRotation)
                Debug.LogWarning($"ActionConfig {name}: Redirectable(重定向改朝向)與 HoldRotation(凍結旋轉)語意矛盾", this);
            if (Loop && Segments != null && Segments.Length > 0)
            {
                var total = 0.0;
                foreach (var segment in Segments)
                    total += System.Math.Max(0.0, segment.Duration);
                if (total * System.TimeSpan.TicksPerSecond < 1)
                    Debug.LogError($"ActionConfig {name}: Loop 動作段總時長為零,會無限 wrap", this);
            }
        }
#endif
    }
}
