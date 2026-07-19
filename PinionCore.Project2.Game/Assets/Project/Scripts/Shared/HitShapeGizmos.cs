#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace PinionCore.Project2.Shared
{
    /// <summary>
    /// hitbox 形狀繪製(僅 Editor):ActionConfig Inspector 預覽與 WorldDebugDrawer 共用。
    /// 座標約定與 HitGeometry 一致:動作局部 x=右、y=前,世界面為 XZ;
    /// dir(θ) = forward·cosθ + right·sinθ(0°=前方,正=偏右,與 Handles 繞 +Y 的正角同向)。
    /// </summary>
    public static class HitShapeGizmos
    {
        /// <summary>
        /// 畫一個 hit 段。sweepProgress:Sweep 扇形的掃掠進度 0~1
        /// (全範圍淡框 + 已掃區實面 + 瞬時邊緣粗線);NaN 或非 Sweep = 畫整個形狀。
        /// extrudeHeight > 0 時追加柱狀線框輪廓:判定忽略 Y,柱體即真實命中範圍,
        /// 透視視角可直接看模型肢體與柱體相交(只畫線不畫牆,避免遮模型)。
        /// </summary>
        public static void Draw(in ActionConfig.HitSegment segment, Vector2 anchor, Vector2 right, Vector2 forward,
            float height, Color fill, Color outline, float sweepProgress = float.NaN, float extrudeHeight = 0f)
        {
            var center2 = anchor + right * segment.LocalOffset.x + forward * segment.LocalOffset.y;
            var center = new Vector3(center2.x, height, center2.y);
            var right3 = new Vector3(right.x, 0f, right.y);
            var forward3 = new Vector3(forward.x, 0f, forward.y);

            var previous = Handles.color;
            switch (segment.Shape)
            {
                case HitShapeType.Circle:
                    Handles.color = fill;
                    Handles.DrawSolidDisc(center, Vector3.up, segment.Radius);
                    Handles.color = outline;
                    Handles.DrawWireDisc(center, Vector3.up, segment.Radius);
                    break;

                case HitShapeType.Box:
                    {
                        var rad = segment.Rotation * Mathf.Deg2Rad;
                        var u = right3 * Mathf.Cos(rad) - forward3 * Mathf.Sin(rad);
                        var v = forward3 * Mathf.Cos(rad) + right3 * Mathf.Sin(rad);
                        var ex = u * segment.HalfExtents.x;
                        var ey = v * segment.HalfExtents.y;
                        var corners = new[]
                        {
                            center - ex - ey,
                            center - ex + ey,
                            center + ex + ey,
                            center + ex - ey,
                        };
                        Handles.DrawSolidRectangleWithOutline(corners, fill, outline);
                    }
                    break;

                case HitShapeType.Sector:
                    {
                        var from = Dir(right3, forward3, segment.AngleFrom);
                        var span = segment.AngleTo - segment.AngleFrom;
                        if (segment.Sweep == SectorSweepMode.Sweep && !float.IsNaN(sweepProgress))
                        {
                            // 全範圍淡框
                            Handles.color = new Color(outline.r, outline.g, outline.b, outline.a * 0.3f);
                            Handles.DrawWireArc(center, Vector3.up, from, span, segment.Radius);
                            // 已掃區實面
                            var current = Mathf.Lerp(segment.AngleFrom, segment.AngleTo, Mathf.Clamp01(sweepProgress));
                            Handles.color = fill;
                            Handles.DrawSolidArc(center, Vector3.up, from, current - segment.AngleFrom, segment.Radius);
                            // 瞬時邊緣粗線
                            Handles.color = outline;
                            Handles.DrawLine(center, center + Dir(right3, forward3, current) * segment.Radius, 3f);
                        }
                        else
                        {
                            Handles.color = fill;
                            Handles.DrawSolidArc(center, Vector3.up, from, span, segment.Radius);
                            Handles.color = outline;
                            Handles.DrawWireArc(center, Vector3.up, from, span, segment.Radius);
                            Handles.DrawLine(center, center + from * segment.Radius);
                            Handles.DrawLine(center, center + Dir(right3, forward3, segment.AngleTo) * segment.Radius);
                        }
                    }
                    break;
            }
            if (extrudeHeight > 0f)
                _DrawColumn(segment, center, right3, forward3, extrudeHeight, outline);
            Handles.color = previous;
        }

        /// <summary>柱狀輪廓:頂框用 outline 原色、垂直邊線 alpha 減半 —— 底面仍是視覺主體。</summary>
        static void _DrawColumn(in ActionConfig.HitSegment segment, Vector3 center, Vector3 right3, Vector3 forward3,
            float extrudeHeight, Color outline)
        {
            var lift = Vector3.up * extrudeHeight;
            var top = center + lift;
            var faded = new Color(outline.r, outline.g, outline.b, outline.a * 0.5f);
            switch (segment.Shape)
            {
                case HitShapeType.Circle:
                    Handles.color = outline;
                    Handles.DrawWireDisc(top, Vector3.up, segment.Radius);
                    Handles.color = faded;
                    for (var angle = 0f; angle < 360f; angle += 30f)
                    {
                        var p = center + Dir(right3, forward3, angle) * segment.Radius;
                        Handles.DrawLine(p, p + lift);
                    }
                    break;

                case HitShapeType.Box:
                    {
                        var rad = segment.Rotation * Mathf.Deg2Rad;
                        var u = right3 * Mathf.Cos(rad) - forward3 * Mathf.Sin(rad);
                        var v = forward3 * Mathf.Cos(rad) + right3 * Mathf.Sin(rad);
                        var ex = u * segment.HalfExtents.x;
                        var ey = v * segment.HalfExtents.y;
                        var corners = new[]
                        {
                            center - ex - ey,
                            center - ex + ey,
                            center + ex + ey,
                            center + ex - ey,
                        };
                        Handles.color = outline;
                        for (var i = 0; i < 4; i++)
                            Handles.DrawLine(corners[i] + lift, corners[(i + 1) % 4] + lift);
                        Handles.color = faded;
                        foreach (var corner in corners)
                            Handles.DrawLine(corner, corner + lift);
                    }
                    break;

                case HitShapeType.Sector:
                    {
                        // Sweep 也只畫全範圍包絡:柱體表達判定可及範圍,掃掠進度留在底面
                        var from = Dir(right3, forward3, segment.AngleFrom);
                        var span = segment.AngleTo - segment.AngleFrom;
                        Handles.color = outline;
                        Handles.DrawWireArc(top, Vector3.up, from, span, segment.Radius);
                        Handles.DrawLine(top, top + from * segment.Radius);
                        Handles.DrawLine(top, top + Dir(right3, forward3, segment.AngleTo) * segment.Radius);
                        Handles.color = faded;
                        var n = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(span) / 30f));
                        for (var i = 0; i <= n; i++)
                        {
                            var angle = Mathf.Lerp(segment.AngleFrom, segment.AngleTo, i / (float)n);
                            var p = center + Dir(right3, forward3, angle) * segment.Radius;
                            Handles.DrawLine(p, p + lift);
                        }
                        Handles.DrawLine(center, top);
                    }
                    break;
            }
        }

        public static Vector3 Dir(Vector3 right3, Vector3 forward3, float angleDegrees)
        {
            var rad = angleDegrees * Mathf.Deg2Rad;
            return forward3 * Mathf.Cos(rad) + right3 * Mathf.Sin(rad);
        }
    }
}
#endif
