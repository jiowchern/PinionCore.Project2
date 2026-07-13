using UnityEngine;

namespace PinionCore.Project2.Shared
{
    // 移動軌跡描述:等速直線(Speed = 0 即駐留),
    // 以 MoveSampler 對 world time 取樣即得任意時刻的位置與朝向。
    public struct MoveInfo
    {
        public Vector2 Position;     // StartTicks 當下世界位置(XZ)
        public Vector2 Facing;       // StartTicks 當下世界朝向(單位向量;鼻子=移動方向)
        public float Speed;          // 線速度;0 = 駐留
        public long StartTicks;      // 伺服器時間戳(TimeSpan ticks)
    }
}
