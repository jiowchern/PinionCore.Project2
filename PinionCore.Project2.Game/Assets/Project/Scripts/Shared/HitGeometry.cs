using UnityEngine;

namespace PinionCore.Project2.Shared
{
    // 攻擊 hitbox 幾何判定:純靜態無狀態,伺服器判定與 editor 預覽共用。
    // 座標約定:動作局部空間 x=右、y=前;角度 0°=動作前方、正=偏右,
    // angle(v) = atan2(dot(v, right), dot(v, forward))。目標一律是 XZ 平面的圓。
    public static class HitGeometry
    {
        /// <summary>
        /// hit 段於 evalFrom~evalTo 判定區間內是否命中目標圓。
        /// anchor = 攻擊者當下取樣位置;right/forward = 動作開始基底(攻擊不可重定向,進行中不變)。
        /// Sweep 扇形以「本次判定區間掃過的角度區間」判定 —— 逐次判定的區間首尾相接,
        /// 任意幀率下對整段掃掠無縫覆蓋(呼叫端負責讓 evalFrom 接上前次的 evalTo)。
        /// </summary>
        public static bool Test(in ActionConfig.HitSegment segment, Vector2 anchor, Vector2 right, Vector2 forward,
            long segStartTicks, long segEndTicks, long evalFromTicks, long evalToTicks,
            Vector2 targetPosition, float targetRadius)
        {
            var center = anchor + right * segment.LocalOffset.x + forward * segment.LocalOffset.y;
            var delta = targetPosition - center;
            switch (segment.Shape)
            {
                case HitShapeType.Circle:
                    {
                        var reach = segment.Radius + targetRadius;
                        return delta.sqrMagnitude <= reach * reach;
                    }
                case HitShapeType.Box:
                    return _TestBox(segment, delta, right, forward, targetRadius);
                case HitShapeType.Sector:
                    {
                        SectorInterval(segment, segStartTicks, segEndTicks, evalFromTicks, evalToTicks, out var lo, out var hi);
                        return _TestSector(segment.Radius, lo, hi, delta, right, forward, targetRadius);
                    }
                default:
                    return false;
            }
        }

        /// <summary>Sweep 扇形在 ticks 時刻的瞬時邊緣角(度);Static 模式呼叫無意義。供預覽與判定共用。</summary>
        public static float SweepAngleAt(in ActionConfig.HitSegment segment, long segStartTicks, long segEndTicks, long ticks)
        {
            var duration = segEndTicks - segStartTicks;
            var t = duration > 0 ? Mathf.Clamp01((ticks - segStartTicks) / (float)duration) : 1f;
            return Mathf.Lerp(segment.AngleFrom, segment.AngleTo, t);
        }

        /// <summary>
        /// 本次判定生效的角度區間 [lo, hi](度)。Static = 整個扇形;
        /// Sweep = evalFrom~evalTo 之間掃過的角度(線性掃掠單調,聯集即端點區間)。
        /// </summary>
        public static void SectorInterval(in ActionConfig.HitSegment segment, long segStartTicks, long segEndTicks,
            long evalFromTicks, long evalToTicks, out float lo, out float hi)
        {
            float a, b;
            if (segment.Sweep == SectorSweepMode.Sweep)
            {
                a = SweepAngleAt(segment, segStartTicks, segEndTicks, evalFromTicks);
                b = SweepAngleAt(segment, segStartTicks, segEndTicks, evalToTicks);
            }
            else
            {
                a = segment.AngleFrom;
                b = segment.AngleTo;
            }
            lo = Mathf.Min(a, b);
            hi = Mathf.Max(a, b);
        }

        static bool _TestBox(in ActionConfig.HitSegment segment, Vector2 delta, Vector2 right, Vector2 forward, float targetRadius)
        {
            // 盒軸 = 動作基底在局部平面內旋轉 Rotation 度:v(盒前)= dir(θ)、u(盒右)= dir(θ+90°)
            var rad = segment.Rotation * Mathf.Deg2Rad;
            var cos = Mathf.Cos(rad);
            var sin = Mathf.Sin(rad);
            var u = right * cos - forward * sin;
            var v = forward * cos + right * sin;

            // 目標圓心轉入盒局部框,夾到盒面取最近點:點到 OBB 距離 ≤ 目標半徑即命中(盒內距離 0)
            var local = new Vector2(Vector2.Dot(delta, u), Vector2.Dot(delta, v));
            var closest = new Vector2(
                Mathf.Clamp(local.x, -segment.HalfExtents.x, segment.HalfExtents.x),
                Mathf.Clamp(local.y, -segment.HalfExtents.y, segment.HalfExtents.y));
            return (local - closest).sqrMagnitude <= targetRadius * targetRadius;
        }

        static bool _TestSector(float radius, float lo, float hi, Vector2 delta, Vector2 right, Vector2 forward, float targetRadius)
        {
            var d = delta.magnitude;
            if (d > radius + targetRadius)
                return false;
            if (d <= targetRadius)
                return true;   // 扇形頂點被目標圓吞掉,角度無意義

            // 目標圓對頂點的半張角 = 精確的角度容差;把目標角對齊到區間中心 ±180° 的窗內,
            // 支援跨 ±180° 與寬度 >180° 的區間(區間 + 容差 ≥ 360° 時任意角度都命中)
            var angle = Mathf.Atan2(Vector2.Dot(delta, right), Vector2.Dot(delta, forward)) * Mathf.Rad2Deg;
            var tolerance = Mathf.Asin(Mathf.Clamp01(targetRadius / d)) * Mathf.Rad2Deg;
            var center = (lo + hi) * 0.5f;
            angle = Mathf.Repeat(angle - center + 180f, 360f) - 180f + center;
            return angle >= lo - tolerance && angle <= hi + tolerance;
        }
    }
}
