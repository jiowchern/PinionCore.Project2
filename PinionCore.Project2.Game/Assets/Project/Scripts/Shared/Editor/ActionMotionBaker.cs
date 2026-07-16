using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace PinionCore.Project2.Shared.Editor
{
    /// <summary>
    /// 把 ActionConfig.Clip 的 root motion 烘焙成分段等速直線(MotionSegment)。
    /// 取樣:PlayableGraph 手動評估(尊重 clip 匯入設定;humanoid/generic 通吃),
    /// 逐步累加 Animator.deltaPosition;貪婪分段化(折點到弦線垂距超過容差才切段)。
    /// 注意:clip 匯入設定若勾了 Bake Into Pose(XZ),root 位移會被抵銷,烘出零位移 —— 會警告。
    /// 比照 ActorConfigBaker:build 前自動跑,也可從選單手動執行。
    /// </summary>
    class ActionMotionBaker : IPreprocessBuildWithReport
    {
        public int callbackOrder => 1;   // 在 ActorConfigBaker(0)之後

        const float SampleStep = 1f / 60f;

        public void OnPreprocessBuild(BuildReport report)
        {
            Bake();
        }

        [MenuItem("PinionCore/Bake Action Motions")]
        public static void Bake()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:ActorConfig"))
            {
                var config = AssetDatabase.LoadAssetAtPath<ActorConfig>(AssetDatabase.GUIDToAssetPath(guid));
                if (config == null || config.Actions == null)
                    continue;

                foreach (var action in config.Actions)
                {
                    if (action == null || action.Clip == null)
                        continue;   // 沒有烘焙來源:視為手填段資料,不動

                    if (BakeAction(action, config))
                    {
                        EditorUtility.SetDirty(action);
                        AssetDatabase.SaveAssetIfDirty(action);
                    }
                }
            }
        }

        static bool BakeAction(ActionConfig action, ActorConfig owner)
        {
            var rigSource = action.BakeRig != null ? action.BakeRig
                          : owner.ModelPrefab != null ? owner.ModelPrefab.editorAsset
                          : null;
            if (rigSource == null)
            {
                Debug.LogError($"[ActionMotionBaker] {action.name}: 無烘焙 rig(BakeRig 未設且 {owner.name} 無 ModelPrefab)");
                return false;
            }

            var instance = Object.Instantiate(rigSource, Vector3.zero, Quaternion.identity);
            instance.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                var animator = instance.GetComponentInChildren<Animator>();
                if (animator == null)
                {
                    Debug.LogError($"[ActionMotionBaker] {action.name}: rig '{rigSource.name}' 上找不到 Animator");
                    return false;
                }
                if (action.Clip.isHumanMotion && (animator.avatar == null || !animator.avatar.isHuman))
                {
                    Debug.LogError(
                        $"[ActionMotionBaker] {action.name}: clip '{action.Clip.name}' 是 humanoid," +
                        $"但 rig '{rigSource.name}' 的 Animator 沒有 humanoid Avatar —— " +
                        "請在 ActionConfig.BakeRig 指定有 Avatar 的 rig(如 unitychan.prefab)");
                    return false;
                }

                var points = SampleRootMotion(animator, action.Clip);
                if (points == null)
                    return false;

                var total = 0f;
                for (var i = 1; i < points.Count; i++)
                    total += Vector2.Distance(points[i].xz, points[i - 1].xz);
                if (total < 0.01f)
                    Debug.LogWarning(
                        $"[ActionMotionBaker] {action.name}: clip '{action.Clip.name}' 幾乎沒有 root 位移" +
                        "(in-place clip 或匯入設定勾了 Bake Into Pose XZ),烘出原地動作");

                if (action.Loop)
                    AlignNetDisplacementForward(action, points);

                var tolerance = Mathf.Max(0.001f, action.SimplifyTolerance);
                var maxSegments = Mathf.Max(1, action.MaxSegments);
                var cuts = Simplify(points, tolerance);
                // 超出段數上限:放寬容差重試,寧可失真也不爆協議外的伺服器排程長度
                for (var retry = 0; cuts.Count - 1 > maxSegments && retry < 5; retry++)
                {
                    tolerance *= 2f;
                    cuts = Simplify(points, tolerance);
                }
                if (cuts.Count - 1 > maxSegments)
                    Debug.LogWarning(
                        $"[ActionMotionBaker] {action.name}: 分段數 {cuts.Count - 1} 仍超過上限 {maxSegments}" +
                        $"(容差已放寬至 {tolerance:F3});請調高 SimplifyTolerance 或 MaxSegments");

                var segments = new List<ActionConfig.MotionSegment>();
                for (var i = 1; i < cuts.Count; i++)
                {
                    var a = points[cuts[i - 1]];
                    var b = points[cuts[i]];
                    segments.Add(new ActionConfig.MotionSegment
                    {
                        LocalOffset = b.xz - a.xz,
                        Duration = b.t - a.t,
                    });
                }

                // 段總時長對齊 clip 長度:位移取樣終點可能略短於 clip(取樣步長殘差)。
                // 一次性動作補零位移尾段;循環(Loop)動作殘差併入最後一段 —— 循環不得有零速尾段,
                // 否則線速度每循環歸零一次,walk 表現閃 idle
                var covered = points[cuts[cuts.Count - 1]].t;
                var residual = action.Clip.length - covered;
                if (residual > 1e-4f)
                {
                    if (action.Loop && segments.Count > 0)
                    {
                        var last = segments[segments.Count - 1];
                        last.Duration += residual;
                        segments[segments.Count - 1] = last;
                    }
                    else
                    {
                        segments.Add(new ActionConfig.MotionSegment
                        {
                            LocalOffset = Vector2.zero,
                            Duration = residual,
                        });
                    }
                }

                action.Duration = action.Clip.length;
                action.Segments = segments.ToArray();
                Debug.Log(
                    $"[ActionMotionBaker] {action.name}: '{action.Clip.name}' → {segments.Count} 段," +
                    $"總位移 {(points[points.Count - 1].xz - points[0].xz).magnitude:F3}m,時長 {action.Clip.length:F3}s");
                return true;
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        /// <summary>
        /// 旋轉整條取樣路徑,使每循環淨位移對齊 +y(局部前方)。
        /// 循環動作的側向淨分量會每循環累積成緩慢偏航(Kevin 走路 clip 確有側向分量),必須在烘焙時消除;
        /// 淨位移過小的 clip 無法循環推進,直接報錯提醒檢查匯入設定。
        /// </summary>
        static void AlignNetDisplacementForward(ActionConfig action, List<(float t, Vector2 xz)> points)
        {
            var net = points[points.Count - 1].xz - points[0].xz;
            if (net.magnitude < 0.01f)
            {
                Debug.LogError(
                    $"[ActionMotionBaker] {action.name}: Locomotion clip '{action.Clip.name}' 每循環淨位移過小" +
                    $"({net.magnitude:F4}m)—— 循環動作需要淨位移(檢查匯入設定 Bake Into Pose XZ)");
                return;
            }

            var dir = net.normalized;
            // R 把 dir 轉到 (0,1):cosθ = dir.y, sinθ = dir.x → v' = (v.x·dir.y - v.y·dir.x, v.x·dir.x + v.y·dir.y)
            for (var i = 0; i < points.Count; i++)
            {
                var p = points[i].xz;
                points[i] = (points[i].t, new Vector2(p.x * dir.y - p.y * dir.x, p.x * dir.x + p.y * dir.y));
            }
        }

        /// <summary>逐步評估 clip,回傳 (時間, 累計 XZ 位移) 折點序列;rig 在原點單位旋轉,結果即動作局部空間(x=右, y=前)。</summary>
        static List<(float t, Vector2 xz)> SampleRootMotion(Animator animator, AnimationClip clip)
        {
            animator.applyRootMotion = true;

            var graph = PlayableGraph.Create("ActionMotionBake");
            try
            {
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var output = AnimationPlayableOutput.Create(graph, "bake", animator);
                var playable = AnimationClipPlayable.Create(graph, clip);
                playable.SetApplyFootIK(false);
                output.SetSourcePlayable(playable);
                graph.Play();

                var points = new List<(float, Vector2)> { (0f, Vector2.zero) };
                var accumulated = Vector2.zero;
                var transformStart = animator.transform.position;
                var time = 0f;

                // 第一次 Evaluate(0) 建立初始 pose,deltaPosition 從第二步起才有意義
                graph.Evaluate(0f);

                while (time < clip.length)
                {
                    var step = Mathf.Min(SampleStep, clip.length - time);
                    graph.Evaluate(step);
                    time += step;

                    var delta = animator.deltaPosition;
                    accumulated += new Vector2(delta.x, delta.z);
                    points.Add((time, accumulated));
                }

                // deltaPosition 在部分編輯器狀態下不更新:退回累計 transform 位移
                if (accumulated.sqrMagnitude < 1e-8f)
                {
                    var moved = animator.transform.position - transformStart;
                    var movedXZ = new Vector2(moved.x, moved.z);
                    if (movedXZ.sqrMagnitude >= 1e-8f)
                    {
                        Debug.LogWarning("[ActionMotionBaker] deltaPosition 未更新,退回 transform 位移(僅端點線性,無中途折點)");
                        points.Clear();
                        points.Add((0f, Vector2.zero));
                        points.Add((clip.length, movedXZ));
                    }
                }
                return points;
            }
            finally
            {
                if (graph.IsValid())
                    graph.Destroy();
            }
        }

        /// <summary>貪婪分段:沿折點延伸,任一內部點到弦線垂距超過容差即在前一點切段;回傳切點索引(含首尾)。</summary>
        static List<int> Simplify(List<(float t, Vector2 xz)> points, float tolerance)
        {
            var cuts = new List<int> { 0 };
            var start = 0;
            for (var end = 2; end < points.Count; end++)
            {
                var maxDeviation = 0f;
                for (var i = start + 1; i < end; i++)
                {
                    var d = DistanceToSegment(points[i].xz, points[start].xz, points[end].xz);
                    if (d > maxDeviation)
                        maxDeviation = d;
                }
                if (maxDeviation > tolerance)
                {
                    cuts.Add(end - 1);
                    start = end - 1;
                }
            }
            cuts.Add(points.Count - 1);
            return cuts;
        }

        static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var lengthSq = ab.sqrMagnitude;
            if (lengthSq < 1e-12f)
                return Vector2.Distance(point, a);
            var s = Mathf.Clamp01(Vector2.Dot(point - a, ab) / lengthSq);
            return Vector2.Distance(point, a + ab * s);
        }
    }
}
