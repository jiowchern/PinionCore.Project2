using PinionCore.Remote;
using UnityEngine;


namespace PinionCore.Project2.Shared
{
    // 角色的控制功能跟私有顯示資訊
    // 只曝光給 client 的擁有者
    public interface IPlayer
    {
        // direction 為世界座標 XZ 方向(x=+X、y=+Z)。
        // 瞬轉直走:朝向即刻設為該方向,沿直線前進直到下一個 Move 或 Stop。
        // 零向量回傳 false;距上次被接受的 Move 未滿 ActorConfig.MoveAcceptInterval
        // 的 Move 會被拒絕(回傳 false),轉向表現由前端補間。
        PinionCore.Remote.Value<bool> Move(Vector2 direction);

        // 不受 MoveAcceptInterval 限制,隨時接受。
        PinionCore.Remote.Value<bool> Stop();


        // 在角色視野內的 Actor 離開視野則觸發 UnsupplyEvent,進入視野則觸發 SupplyEvent, 包含自己。
        Notifier<IActor> Actors { get; }
    }
}
