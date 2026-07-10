using UnityEngine;

namespace PinionCore.Project2.Shared
{
    // 轉向式移動的軌跡描述:等速圓周弧線(直線、駐留、原地轉皆為退化情形),
    // 以 MoveSampler 對 world time 取樣即得任意時刻的位置與朝向。
    public struct MoveInfo
    {
        public Vector2 Position;     // StartTicks 當下世界位置(XZ)
        public Vector2 Facing;       // StartTicks 當下世界朝向(單位向量;鼻子=移動方向)
        public float Speed;          // 線速度;0 = 駐留
        public float AngularSpeed;   // 角速度 rad/s;0 = 直線;正 = 向右(正負號約定見 MoveSampler)
        public long StartTicks;      // 伺服器時間戳(TimeSpan ticks)
    }
    public interface IActor : Remote.Protocolable
    {
        PinionCore.Remote.Property<string> DisplayName { get; }
        PinionCore.Remote.Property<string> ModelName { get; }
        PinionCore.Remote.Property<System.Guid> ActorId { get; }

        // 位置與朝向完全由 MoveEvent 推導:訂閱時 replay 當下 MoveInfo(駐留或移動中),
        // 以 StartTicks + world time 取樣即得任意時刻狀態,故不需要 Position 屬性。
        event System.Action<MoveInfo> MoveEvent;

    }
}
