namespace PinionCore.Project2.Shared.Users
{
    public enum ModelType
    {
        // 登入下拉選單選項順序 = enum 值;HumanM 為預設第一支(DemoLoginUIBuilder 同步)
        HumanM,
        Cube,
        Unitychan,
    }
    public interface IVerifier : Remote.Protocolable
    {
        PinionCore.Remote.Value<bool> Verify(string name, ModelType type);
    }
}
