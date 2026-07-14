namespace PinionCore.Project2.Shared
{
    // 角色識別:IPlayer(自身控制視圖)與 IActor(可見角色視圖)是同一角色的兩個介面,
    // client 以 ActorId 在兩者與殼(Client.Actor)之間對應。
    public interface IIdentity
    {
        PinionCore.Remote.Property<System.Guid> ActorId { get; }
    }
}
