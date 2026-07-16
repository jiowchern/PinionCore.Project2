namespace PinionCore.Project2.Shared.Users
{
    public enum ModelType
    {
        Cube,
        Unitychan,        
    }
    public interface IVerifier : Remote.Protocolable
    {
        PinionCore.Remote.Value<bool> Verify(string name, ModelType type);
    }
}
