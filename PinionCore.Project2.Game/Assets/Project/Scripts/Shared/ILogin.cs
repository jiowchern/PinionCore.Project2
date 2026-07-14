namespace PinionCore.Project2.Shared.Users
{
    public enum CharactorType
    {
        Cube,
        Unitychan,        
    }
    public interface ILogin : Remote.Protocolable
    {
        PinionCore.Remote.Value<bool> Verify(string name, CharactorType type);
    }
}
