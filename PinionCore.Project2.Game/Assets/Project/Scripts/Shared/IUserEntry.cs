using PinionCore.Project2.Shared.Users;


namespace PinionCore.Project2.Shared
{

    // 給前端使用者的User服務入口 從這入口可以知道該服務會在什麼階段堤共什麼功能給使用者    

    public interface IUserEntry : Remote.Protocolable
    {
        
        // 驗證
        PinionCore.Remote.Notifier<IVerifier> Verifiers { get; }

        // 遊戲
        PinionCore.Remote.Notifier<IGame> Games { get; }

    }
}
