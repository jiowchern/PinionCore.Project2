namespace PinionCore.Project2.Protocols.Worlds
{
    public interface IView : PinionCore.Remote.Protocolable
    {
        PinionCore.Remote.Property<string> Name { get; }
    }
}
