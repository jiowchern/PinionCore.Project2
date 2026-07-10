using UnityEngine;

namespace PinionCore.Project2.Shared
{
    /// <summary>
    /// client / server 共用的軌跡取樣器:兩端用同一份解析公式,避免各自積分造成飄移。
    /// 正負號約定(俯視 XZ 平面,+Z 為前、+X 為右):AngularSpeed > 0 = 向右(順時針),
    /// 對應輸入 direction.x > 0。
    /// </summary>
    public static class MoveSampler
    {
        const float AngularEpsilon = 1e-4f;

        // elapsedSeconds 由呼叫端以 (nowTicks - StartTicks) 換算並 clamp >= 0。
        public static void Sample(in MoveInfo info, double elapsedSeconds, out Vector2 position, out Vector2 facing)
        {
            var t = (float)elapsedSeconds;
            if (Mathf.Abs(info.AngularSpeed) < AngularEpsilon)
            {
                position = info.Position + info.Facing * (info.Speed * t);
                facing = info.Facing;
                return;
            }

            var theta = info.AngularSpeed * t;
            facing = Rotate(info.Facing, theta);

            // 帶號半徑:ω 的正負決定圓心在右側或左側;Speed = 0 時圓心即原地(原地轉)。
            var radius = info.Speed / info.AngularSpeed;
            var center = info.Position + radius * PerpRight(info.Facing);
            position = center + Rotate(info.Position - center, theta);
        }

        // 正角度 = 順時針(向右):+Z=(0,1) 轉 θ 得 (sinθ, cosθ)。
        public static Vector2 Rotate(Vector2 v, float radian)
        {
            var cos = Mathf.Cos(radian);
            var sin = Mathf.Sin(radian);
            return new Vector2(v.x * cos + v.y * sin, -v.x * sin + v.y * cos);
        }

        static Vector2 PerpRight(Vector2 v)
        {
            return new Vector2(v.y, -v.x);
        }
    }
}
