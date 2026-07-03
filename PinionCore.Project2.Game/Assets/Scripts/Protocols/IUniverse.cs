namespace PinionCore.Project2.Protocols
{
    public interface IUniverse : Remote.Protocolable
    {
        PinionCore.Remote.Value<System.Guid> CreateWorld(string name);

        PinionCore.Remote.Notifier<IWorld> WorldNotifier { get; }

        PinionCore.Remote.Value<bool> DestroyWorld(System.Guid worldId);
    }
}
  