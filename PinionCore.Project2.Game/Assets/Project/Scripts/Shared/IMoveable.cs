using UnityEngine;


namespace PinionCore.Project2.Shared
{
    public interface IMoveable : IIdentity
    {

        // direction 為世界座標 XZ 方向(x=+X、y=+Z)。
        // 瞬轉直走:朝向即刻設為該方向,沿直線前進直到下一個 Move 或 Stop。
        // 零向量回傳 false;距上次被接受的 Move 未滿 ActorConfig.MoveAcceptInterval
        // 的 Move 會被拒絕(回傳 false),轉向表現由前端補間。
        PinionCore.Remote.Value<bool> Move(Vector2 direction);

        // 不受 MoveAcceptInterval 限制,隨時接受。
        PinionCore.Remote.Value<bool> Stop();
    }
}
