using PinionCore.Remote;
using System.Collections.Generic;
using System.Text;

namespace PinionCore.Project2.Protocols
{

    /// <summary>
    /// ECS 世界的抽象介面
    /// 提供外部使用者查詢世界資訊,但不允許直接操作 DOTS 世界。
    /// </summary>
    public interface IWorld : IView
    {
       PinionCore.Remote.Property<System.Guid> Id { get; }
    }
}
