using PinionCore.Remote;
using System.Collections.Generic;
using System.Text;

namespace PinionCore.Project2.Shared
{

    /// <summary>
    /// ECS 世界的抽象介面
    /// 提供外部使用者查詢世界資訊,但不允許直接操作 DOTS 世界。
    /// </summary>
    public interface IWorld : IView
    {
        PinionCore.Remote.Property<System.Guid> Id { get; }

        // actorId 由呼叫端產生:進場方能在送出 Enter 的同一刻註冊 Leave(actorId),
        // 即使回應未消化前 session 就收尾,補償退場也結構性成立。
        PinionCore.Remote.Value<bool> Enter(System.Guid actorId, ActorInfo actor);
        PinionCore.Remote.Value<bool> Leave(System.Guid actorId);

        PinionCore.Remote.Notifier<ICharacter> Players { get; }


    }
}
