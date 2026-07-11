namespace PinionCore.Project2.Shared
{
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
