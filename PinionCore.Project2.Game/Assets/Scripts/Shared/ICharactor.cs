namespace PinionCore.Project2.Shared
{

    // World 內的角色狀態資訊不曝光給 client , 只給 user
    public interface ICharactor : IPlayer, IActor
    {
        

    }
}
