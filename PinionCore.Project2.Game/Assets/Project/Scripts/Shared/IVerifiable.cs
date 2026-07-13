namespace PinionCore.Project2.Shared.Users
{
    public interface IVerifiable : Remote.Protocolable
    {
        PinionCore.Remote.Value<bool> Verify(string name);
    }
}
