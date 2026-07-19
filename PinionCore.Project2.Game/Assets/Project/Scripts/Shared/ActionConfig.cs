using UnityEngine;

namespace PinionCore.Project2.Shared
{
}

namespace PinionCore.Project2.Shared
{
    // 一個「自帶位移的動作」的伺服器權威資料:root motion 由 editor 烘焙成
    // 分段等速直線(MotionSegment),伺服器依絕對 tick 排程逐段發出 MoveInfo,
    // 既有的去穿透 / TOI 撞牆對每段直線天然成立。
    public enum HitShapeType
    {
        Circle = 0,
        Box = 1,
        Sector = 2,
    }

    public enum SectorSweepMode
    {
        Static = 0,   // 時間窗內整個扇形常駐
        Sweep = 1,    // 角度邊緣隨時間從 AngleFrom 線性掃到 AngleTo
    }

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
        [Min(0f)]
        public float ChainWindow;    // 接招窗(秒):播完後 Playables 白名單仍開放、延遲轉移 Next;0 = 播完立即轉移

        [Header("表現(client 讀取)")]
        public bool HoldRotation;    // 動作期間 client 凍結旋轉(段 Facing 是速度方向非視覺朝向)
        public StanceType Stance;    // 動畫組歸屬/輸入 gating(取代已拆除的 StanceOf 推導)

        // 動作總時長(秒)= 烘焙來源 clip 的長度;段總時長由烘焙器補零位移尾段對齊此值
        public float Duration;

        public MotionSegment[] Segments = System.Array.Empty<MotionSegment>();

        // 攻擊命中判定窗:與 MotionSegment 同一動作局部空間(x=右, y=前)與秒制時間軸;
        // 形狀參數攤平在同一 struct(Unity 序列化不支援多型),依 Shape 取用對應欄位。
        [System.Serializable]
        public struct HitSegment
        {
            public float StartTime;       // 秒,相對動作開始
            public float Duration;        // 秒
            public HitShapeType Shape;
            public Vector2 LocalOffset;   // 形狀錨點(扇形頂點/圓心/盒中心)的局部位移

            public float Radius;          // Circle / Sector:半徑
            public Vector2 HalfExtents;   // Box:半寬(x=右向, y=前向)
            public float Rotation;        // Box:相對動作前方的旋轉角(度,正=偏右)
            public float AngleFrom;       // Sector:起始角(度,0=動作前方,正=偏右);Sweep 的掃掠起點
            public float AngleTo;         // Sector:結束角;允許 To<From(往左掃)與跨 ±180°
            public SectorSweepMode Sweep; // Sector 專用;其餘形狀忽略
        }

        public HitSegment[] HitSegments = System.Array.Empty<HitSegment>();

#if UNITY_EDITOR
        [Header("烘焙設定(僅 Editor,player build 不含)")]
        public AnimationClip Clip;               // 烘焙來源;root motion 取樣自此 clip
        public GameObject BakeRig;               // 選填:模型 prefab 的 Animator 無 Avatar 時,指定有 Avatar 的 rig 烘焙
        public float SimplifyTolerance = 0.02f;  // 分段化最大偏差(公尺):折點到弦線垂距超過即切段
        public int MaxSegments = 8;              // 超過即警告(通常表示 clip 位移過於曲折或容差過小)

        [Header("軌跡預覽(僅 Editor,player build 不含)")]
        public string TrailProbe;    // 預覽模型階層內的 Transform 名稱(骨骼名或武器尖端名);空 = 不畫軌跡
        public string TrailProbeB;   // 選填:第二探測點,與 TrailProbe 間連線成揮擊帶(如 手骨→刀尖 的刀刃掃過面)

        void OnValidate()
        {
            if (Redirectable && !Loop)
                Debug.LogError($"ActionConfig {name}: Redirectable 依賴循環邊界相位保持,非 Loop 動作的重定向語意未定義", this);
            if (Redirectable && HoldRotation)
                Debug.LogWarning($"ActionConfig {name}: Redirectable(重定向改朝向)與 HoldRotation(凍結旋轉)語意矛盾", this);
            if (ChainWindow > 0f && Loop)
                Debug.LogWarning($"ActionConfig {name}: Loop 動作不會自然播完(EndEvent 只有守門結束),ChainWindow 無意義", this);
            if (Loop && Segments != null && Segments.Length > 0)
            {
                var total = 0.0;
                foreach (var segment in Segments)
                    total += System.Math.Max(0.0, segment.Duration);
                if (total * System.TimeSpan.TicksPerSecond < 1)
                    Debug.LogError($"ActionConfig {name}: Loop 動作段總時長為零,會無限 wrap", this);
            }
            if (HitSegments != null && HitSegments.Length > 0)
            {
                if (Loop)
                    Debug.LogWarning($"ActionConfig {name}: Loop 動作帶 HitSegments 不支援(wrap 不換動作實例,一次命中的去重語意未定義)", this);
                for (var i = 0; i < HitSegments.Length; i++)
                {
                    var hit = HitSegments[i];
                    if (hit.StartTime < 0f || hit.Duration <= 0f || hit.StartTime + hit.Duration > Duration + 1e-4f)
                        Debug.LogWarning($"ActionConfig {name}: HitSegments[{i}] 時間窗 [{hit.StartTime:0.###}, {hit.StartTime + hit.Duration:0.###}] 超出動作時長 {Duration:0.###}", this);
                    if ((hit.Shape == HitShapeType.Circle || hit.Shape == HitShapeType.Sector) && hit.Radius <= 0f)
                        Debug.LogWarning($"ActionConfig {name}: HitSegments[{i}] 半徑必須為正", this);
                    if (hit.Shape == HitShapeType.Box && (hit.HalfExtents.x <= 0f || hit.HalfExtents.y <= 0f))
                        Debug.LogWarning($"ActionConfig {name}: HitSegments[{i}] HalfExtents 必須為正", this);
                }
            }
        }
#endif
    }
}
