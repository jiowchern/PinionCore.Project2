namespace PinionCore.Project2.Protocols.Users
{
    public interface IVerifiable : Remote.Protocolable
    {
        PinionCore.Remote.Value<bool> Verify(string name);
    }
}
