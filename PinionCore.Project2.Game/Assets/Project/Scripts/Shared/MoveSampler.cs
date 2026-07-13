using UnityEngine;

namespace PinionCore.Project2.Shared
{
    /// <summary>
    /// client / server 共用的軌跡取樣器:兩端用同一份解析公式,避免各自積分造成飄移。
    /// </summary>
    public static class MoveSampler
    {
        // elapsedSeconds 由呼叫端以 (nowTicks - StartTicks) 換算並 clamp >= 0。
        public static void Sample(in MoveInfo info, double elapsedSeconds, out Vector2 position, out Vector2 facing)
        {
            position = info.Position + info.Facing * (info.Speed * (float)elapsedSeconds);
            facing = info.Facing;
        }
    }
}
